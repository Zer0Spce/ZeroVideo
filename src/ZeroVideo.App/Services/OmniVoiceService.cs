using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class OmniVoiceProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ReferenceWavPath { get; set; } = "";
    public string ReferenceTranscript { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public override string ToString() => Name;
}

/// <summary>
/// Isolated, optional OmniVoice evaluation environment. Model weights are
/// CC BY-NC 4.0. This implementation NEVER touches ComfyUI's Python or CUDA
/// dependencies and NEVER enables local TTS without explicit user selection.
/// </summary>
public sealed class OmniVoiceService
{
    public static string Root => Path.Combine(AppPaths.DataDirectory,"LocalTTS","OmniVoice");
    public static string UvPath => Path.Combine(Root,"uv.exe");
    public static string VenvDirectory => Path.Combine(Root,"venv");
    public static string PythonPath => Path.Combine(VenvDirectory,"Scripts","python.exe");
    private static string CacheRoot => Path.Combine(Root,"Cache");
    public static string VoiceDirectory => Path.Combine(Root,"VoiceProfiles");
    private static string ProfilesFile => Path.Combine(VoiceDirectory,"profiles.json");
    private static string WorkerPath => Path.Combine(Root,"omnivoice_worker.py");
    private static string ReadyMarker => Path.Combine(Root,"installed-0.2.1.txt");
    private const string UvUrl =
        "https://github.com/astral-sh/uv/releases/download/0.12.3/uv-x86_64-pc-windows-msvc.zip";
    private const string UvSha256 =
        "b23350c79e8ad0192b8124af13a0f17e8d4e4549524785e1aef389ae5a06990e";
    private const string ModelRepo = "k2-fsa/OmniVoice";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect=true,AutomaticDecompression=DecompressionMethods.All
    }){Timeout=Timeout.InfiniteTimeSpan};

    private readonly ConcurrentDictionary<int,Process> _children=new();
    public bool IsInstalled => File.Exists(PythonPath)&&File.Exists(ReadyMarker);

    public void StopActiveProcesses()
    {
        foreach(var process in _children.Values)
            try { if(!process.HasExited)process.Kill(entireProcessTree:true); }
            catch(InvalidOperationException) { }
            catch(System.ComponentModel.Win32Exception) { }
    }

    private static ProcessStartInfo MakeProcessInfo(string program,
        IReadOnlyList<string> arguments,string directory)
    {
        var info=new ProcessStartInfo(program)
        {
            WorkingDirectory=directory,
            CreateNoWindow=true,UseShellExecute=false,
            RedirectStandardOutput=true,RedirectStandardError=true
        };
        foreach(var arg in arguments)info.ArgumentList.Add(arg);
        info.Environment["HF_HOME"]=CacheRoot;
        info.Environment["HF_HUB_CACHE"]=Path.Combine(CacheRoot,"hub");
        info.Environment["UV_CACHE_DIR"]=Path.Combine(CacheRoot,"uv");
        info.Environment["UV_PYTHON_INSTALL_DIR"]=Path.Combine(Root,"Python");
        info.Environment["PYTHONUTF8"]="1";
        info.Environment["PYTHONIOENCODING"]="utf-8";
        return info;
    }

    private async Task RunAsync(string program,IReadOnlyList<string> arguments,
        Action<string>? log,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var proc=new Process
        {
            StartInfo=MakeProcessInfo(program,arguments,Root)
        };
        if(!proc.Start())
            throw new IOException("Failed to launch "+Path.GetFileName(program));
        _children[proc.Id]=proc;
        var last=new Queue<string>();
        var lastLock=new object();
        using var registration=ct.Register(() =>
        {
            try { if(!proc.HasExited)proc.Kill(entireProcessTree:true); }
            catch { }
        });
        async Task Pump(StreamReader reader)
        {
            while(true)
            {
                var line=await reader.ReadLineAsync(ct);
                if(line is null)break;
                if(line.Length==0)continue;
                var safe=line.Length>420?line[..420]:line;
                lock(lastLock)
                {
                    if(last.Count>=15)last.Dequeue();
                    last.Enqueue(safe);
                }
                // Avoid echoing entire inference scripts or file contents.
                log?.Invoke("OmniVoice: "+safe);
            }
        }
        try
        {
            var stdout=Pump(proc.StandardOutput);
            var stderr=Pump(proc.StandardError);
            await Task.WhenAll(proc.WaitForExitAsync(ct),stdout,stderr);
            if(proc.ExitCode!=0)
            {
                string details;
                lock(lastLock)details=string.Join(" | ",last.TakeLast(5));
                throw new IOException(Path.GetFileName(program)+
                    " exited "+proc.ExitCode+"; "+details);
            }
        }
        finally
        {
            _children.TryRemove(proc.Id,out _);
            if(ct.IsCancellationRequested)
                try { if(!proc.HasExited)proc.Kill(entireProcessTree:true); }
                catch { }
        }
    }

    private static async Task EnsureUvAsync(Action<string>? log,CancellationToken ct)
    {
        if(File.Exists(UvPath))return;
        Directory.CreateDirectory(Root);
        var zip=Path.Combine(Root,"uv-windows-x64.zip");
        var partial=zip+".partial";
        log?.Invoke("Downloading verified Astral uv Windows installer into isolated TTS folder...");
        using(var response=await Http.GetAsync(UvUrl,
            HttpCompletionOption.ResponseHeadersRead,ct))
        {
            response.EnsureSuccessStatusCode();
            await using var src=await response.Content.ReadAsStreamAsync(ct);
            await using var dst=File.Create(partial);
            await src.CopyToAsync(dst,ct);
        }
        await using(var src=File.OpenRead(partial))
        {
            var sha=Convert.ToHexString(await SHA256.HashDataAsync(src,ct));
            if(!sha.Equals(UvSha256,StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException("Astral uv installer SHA-256 mismatch.");
            }
        }
        File.Move(partial,zip,true);
        using(var archive=ZipFile.OpenRead(zip))
        {
            var exe=archive.Entries.FirstOrDefault(x=>
                Path.GetFileName(x.FullName).Equals("uv.exe",StringComparison.OrdinalIgnoreCase)
                && !x.FullName.Split('/').Any(p=>p==".."));
            if(exe is null)throw new InvalidDataException("No uv.exe in verified archive.");
            exe.ExtractToFile(UvPath,true);
        }
        File.Delete(zip);
        log?.Invoke("Verified local uv.exe installed. No system PATH changes made.");
    }

    private static void WriteWorker()
    {
        Directory.CreateDirectory(Root);
        using var stream=typeof(OmniVoiceService).Assembly.GetManifestResourceStream(
            "ZeroVideo.App.Resources.omnivoice_worker.py");
        if(stream is null)
            throw new FileNotFoundException(
                "OmniVoice worker was not embedded in this ZeroVideo build.");
        var temporary=WorkerPath+".tmp";
        using(var to=File.Create(temporary))stream.CopyTo(to);
        File.Move(temporary,WorkerPath,true);
    }

    public async Task InstallAsync(Action<string>? log,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("OmniVoice setup targets Windows x64.");
        Directory.CreateDirectory(Root);
        var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Root))!);
        if(drive.AvailableFreeSpace<15L*1024*1024*1024&&!IsInstalled)
            throw new IOException("OmniVoice setup needs approximately 15 GB free "+
                "for a managed Python environment, CUDA PyTorch and model cache.");
        await EnsureUvAsync(log,ct);
        WriteWorker();
        if(IsInstalled)
        {
            log?.Invoke("OmniVoice installation already present. Nothing replaced.");
            return;
        }
        log?.Invoke("Creating an isolated managed Python 3.11 environment (no system Python changes)...");
        await RunAsync(UvPath,["venv",VenvDirectory,"--python","3.11",
            "--python-preference","only-managed"],log,ct);
        log?.Invoke("Installing NVIDIA CUDA PyTorch in OmniVoice-only Python...");
        await RunAsync(UvPath,["pip","install","--python",PythonPath,
            "torch==2.8.0+cu128","torchaudio==2.8.0+cu128",
            "--index","https://download.pytorch.org/whl/cu128"],log,ct);
        log?.Invoke("Installing original OmniVoice 0.2.1 and required Python packages...");
        await RunAsync(UvPath,["pip","install","--python",PythonPath,
            "omnivoice==0.2.1","huggingface_hub"],log,ct);
        await RunAsync(PythonPath,["-c",
            "import omnivoice, torch, soundfile; print('OmniVoice import OK; torch='+torch.__version__)"],log,ct);
        log?.Invoke("Downloading official OmniVoice weights into ZeroVideo LocalTTS cache...");
        await RunAsync(PythonPath,[WorkerPath,"--download-only"],log,ct);
        await File.WriteAllTextAsync(ReadyMarker,
            "OmniVoice 0.2.1 installed for NON-COMMERCIAL evaluation only.\n"+
            "Model weights: CC BY-NC 4.0; user must obtain commercial license "+
            "before monetized deployment.\n",ct);
        log?.Invoke("OmniVoice installed. Voice generation is opt-in and requires a consented sample.");
    }

    public List<OmniVoiceProfile> LoadProfiles()
    {
        try
        {
            if(!File.Exists(ProfilesFile))return [];
            var items=JsonSerializer.Deserialize<List<OmniVoiceProfile>>(
                File.ReadAllText(ProfilesFile))??[];
            return items.Where(p=>!string.IsNullOrWhiteSpace(p.Id)&&
                !string.IsNullOrWhiteSpace(p.Name)&&File.Exists(p.ReferenceWavPath)).ToList();
        }
        catch{return [];}
    }

    public void RemoveProfileFromLibrary(string id)
    {
        if(string.IsNullOrWhiteSpace(id))return;
        var remaining=LoadProfiles().Where(p=>
            !p.Id.Equals(id,StringComparison.OrdinalIgnoreCase)).ToList();
        // Remove only the entry from the library, not the audio reference on
        // disk. Deleting recordings should be an explicit separate action.
        SaveProfiles(remaining);
    }

    /// <summary>
    /// Test a new reference before adding anything to the saved voice library.
    /// The temporary converted reference is deleted after each test.
    /// </summary>
    public async Task<string> PreviewNewSampleAsync(
        string sample,string transcript,string testText,
        ZeroVideoSettings settings,Action<string>? log,CancellationToken ct)
    {
        if(!IsInstalled)throw new InvalidOperationException(
            "OmniVoice must be installed before voice preview.");
        if(string.IsNullOrWhiteSpace(transcript))
            throw new ArgumentException("Enter the exact reference transcript.");
        if(!File.Exists(sample))throw new FileNotFoundException("Voice sample not found.",sample);
        var folder=Path.Combine(Root,"Previews");
        Directory.CreateDirectory(folder);
        var token=Guid.NewGuid().ToString("N");
        var wav=Path.Combine(folder,token+"-reference.wav");
        var preview=Path.Combine(folder,token+"-voice.wav");
        var ffmpeg=SettingsService.ResolveTool(settings.FfmpegPath,"ffmpeg.exe");
        if(string.IsNullOrWhiteSpace(ffmpeg)||!File.Exists(ffmpeg))
            throw new FileNotFoundException("Install FFmpeg before testing voice cloning.");
        try
        {
            // A short sample is the officially recommended voice-cloning
            // reference. Use the first ten seconds only for this preview.
            await RunAsync(ffmpeg,["-hide_banner","-loglevel","error","-nostdin",
                "-y","-i",sample,"-t","10","-vn","-ac","1","-ar",
                "24000","-c:a","pcm_s16le",wav],log,ct);
            var profile=new OmniVoiceProfile
            {
                Name="Unsaved voice preview",ReferenceWavPath=wav,
                ReferenceTranscript=transcript.Trim()
            };
            await GenerateAsync(testText,preview,profile,settings.OmniVoiceSpeed,log,ct);
            return preview;
        }
        finally
        {
            try{File.Delete(wav);}catch{}
        }
    }

    private static void SaveProfiles(IEnumerable<OmniVoiceProfile> profiles)
    {
        Directory.CreateDirectory(VoiceDirectory);
        var temporary=ProfilesFile+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(profiles,
            new JsonSerializerOptions{WriteIndented=true}));
        File.Move(temporary,ProfilesFile,true);
    }

    public async Task<OmniVoiceProfile> ImportProfileAsync(
        string name,string reference,string transcript,ZeroVideoSettings settings,
        Action<string>? log,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>70)
            throw new ArgumentException("Enter a voice profile name (1–70 characters).");
        if(string.IsNullOrWhiteSpace(transcript)||transcript.Length>3000)
            throw new ArgumentException("Paste the exact words spoken in the reference clip.");
        if(!File.Exists(reference))
            throw new FileNotFoundException("Reference recording not found.",reference);
        if(new FileInfo(reference).Length>30*1024*1024)
            throw new InvalidDataException("Voice sample exceeds 30 MB.");
        var ffmpeg=SettingsService.ResolveTool(settings.FfmpegPath,"ffmpeg.exe");
        if(string.IsNullOrWhiteSpace(ffmpeg)||!File.Exists(ffmpeg))
            throw new FileNotFoundException("Install FFmpeg first to convert voice samples.");
        var profile=new OmniVoiceProfile
        {
            Id=Guid.NewGuid().ToString("N"),
            Name=name.Trim(),ReferenceTranscript=transcript.Trim()
        };
        Directory.CreateDirectory(VoiceDirectory);
        var folder=Path.Combine(VoiceDirectory,profile.Id);
        Directory.CreateDirectory(folder);
        profile.ReferenceWavPath=Path.Combine(folder,"reference.wav");
        var tmp=profile.ReferenceWavPath+".partial.wav";
        try
        {
            await RunAsync(ffmpeg,["-hide_banner","-loglevel","error","-nostdin",
                "-y","-i",reference,"-t","10","-vn","-ac","1","-ar","24000",
                "-c:a","pcm_s16le",tmp],log,ct);
            if(!File.Exists(tmp)||new FileInfo(tmp).Length<4096)
                throw new InvalidDataException("Converted voice sample is empty.");
            File.Move(tmp,profile.ReferenceWavPath,true);
            var existing=LoadProfiles();
            existing.Add(profile);
            SaveProfiles(existing);
            log?.Invoke("Saved voice profile '"+profile.Name+
                "' locally. Reference is private to this PC.");
            return profile;
        }
        catch
        {
            try {if(File.Exists(tmp))File.Delete(tmp);}catch{}
            throw;
        }
    }

    public async Task<string> GenerateAsync(
        string narration,string output,OmniVoiceProfile profile,double speed,
        Action<string>? log,CancellationToken ct,string language="English")
    {
        if(!IsInstalled)
            throw new InvalidOperationException(
                "OmniVoice is not installed. Open Local TTS and click Install Everything.");
        if(!File.Exists(profile.ReferenceWavPath))
            throw new FileNotFoundException("Voice reference missing.",profile.ReferenceWavPath);
        if(string.IsNullOrWhiteSpace(profile.ReferenceTranscript))
            throw new InvalidOperationException("A matching sample transcript is required.");
        if(string.IsNullOrWhiteSpace(narration))
            throw new InvalidOperationException("Narration is empty.");
        WriteWorker();
        var jobFolder=Path.Combine(Root,"Jobs");
        Directory.CreateDirectory(jobFolder);
        var job=Path.Combine(jobFolder,Guid.NewGuid().ToString("N")+".json");
        var json=JsonSerializer.Serialize(new Dictionary<string,object>
        {
            ["text"]=narration,["ref_audio"]=profile.ReferenceWavPath,
            ["ref_text"]=profile.ReferenceTranscript,
            ["output"]=output,["speed"]=Math.Clamp(speed,0.7,1.3),
            ["language"]=string.IsNullOrWhiteSpace(language)?"English":language,
            // Save/load the encoded voice identity for named library profiles.
            // Unsaved previews have no profile ID, so no prompt is retained.
            ["voice_prompt"]=string.IsNullOrWhiteSpace(profile.Id)?"":Path.Combine(
                Path.GetDirectoryName(profile.ReferenceWavPath)!,"voice-clone.pt")
        });
        await File.WriteAllTextAsync(job,json,ct);
        try
        {
            log?.Invoke("Generating OmniVoice narration using approved local "+
                "profile '"+profile.Name+"' (no Clipchamp).");
            await RunAsync(PythonPath,[WorkerPath,"--job",job],log,ct);
            if(!File.Exists(output)||new FileInfo(output).Length<4096)
                throw new IOException("OmniVoice returned no usable WAV audio.");
            return output;
        }
        finally
        {
            try{File.Delete(job);}catch{}
        }
    }

    public async Task<string> GenerateChapterAsync(
        ProjectChapter chapter,VideoProject project,ZeroVideoSettings settings,
        Action<string>? log,CancellationToken ct)
    {
        if(!settings.OmniVoiceNonCommercialAcknowledged)
            throw new InvalidOperationException(
                "OmniVoice model weights are non-commercial. Acknowledge "+
                "the license in the Local TTS tab before evaluation.");
        var profile=LoadProfiles().FirstOrDefault(x=>
            x.Id.Equals(settings.OmniVoiceProfileId,StringComparison.OrdinalIgnoreCase));
        if(profile is null)
            throw new InvalidOperationException(
                "Select an imported, consented voice profile in the Local TTS tab.");
        var filename=Path.Combine(project.ProjectDirectory,"audio",
            $"chapter-{chapter.Number:00}-omnivoice.wav");
        await GenerateAsync(chapter.Narration,filename,profile,
            settings.OmniVoiceSpeed,log,ct,project.Language);
        chapter.AudioPath=filename;
        return filename;
    }
}
