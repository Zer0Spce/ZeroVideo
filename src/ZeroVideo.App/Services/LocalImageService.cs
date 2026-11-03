using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Optional, local-only ComfyUI bridge. Legacy ChatGPT image automation is
/// untouched and stays the default in settings. Only communicates with
/// loopback, and downloads solely from official ComfyUI / Stability AI URLs.
/// </summary>
public sealed class LocalImageService
{
    public const string ComfyUiArchiveUrl =
        "https://github.com/Comfy-Org/ComfyUI/releases/latest/download/ComfyUI_windows_portable_nvidia.7z";
    public const string SdxlUrl =
        "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main/sd_xl_base_1.0.safetensors";
    public const string SdxlFileName = "sd_xl_base_1.0.safetensors";
    public const string SdxlSha256 =
        "31e35c80fc4829d14f90153f4c74cd59c90b779f6afe05a74cd6120b893f7e5b";
    // A fine-tuned illustrated SDXL checkpoint. File and checksum are taken
    // from the creator's public Hugging Face model file metadata.
    public const string IllustratedFileName =
        "DreamShaperXL1.0Alpha2_fixedVae_half_00001_.safetensors";
    public const string IllustratedUrl =
        "https://huggingface.co/Lykon/DreamShaper/resolve/3bfe5f4614b38c17975e06a2094c2b99381bda7b/" +
        IllustratedFileName;
    public const string IllustratedSha256 =
        "0f1b80cfe81b9c3bde7fdcbf6898897b2811b27be1df684583c3d85cbc9b1fa4";
    // Optional FLUX.1-schnell GGUF Q4_K_S (~6.78 GB) for 8 GB GPUs.
    // All four assets have SHA-256 verified by Hugging Face LFS metadata.
    // FLUX.1-schnell is Apache-2.0; FLUX.1-dev is deliberately excluded.
    public const string FluxGgufFileName="flux1-schnell-Q4_K_S.gguf";
    private const string FluxGgufUrl=
        "https://huggingface.co/city96/FLUX.1-schnell-gguf/resolve/main/"+FluxGgufFileName;
    private const string FluxGgufHash=
        "4fd16477b3a5296d0cf722c4b92a9fd7f30d09ac7495826e4465d8de9c9fd973";
    private const string FluxT5FileName="t5xxl_fp8_e4m3fn.safetensors";
    private const string FluxT5Url=
        "https://huggingface.co/comfyanonymous/flux_text_encoders/resolve/main/"+FluxT5FileName;
    private const string FluxT5Hash=
        "7d330da4816157540d6bb7838bf63a0f02f573fc48ca4d8de34bb0cbfd514f09";
    private const string FluxClipFileName="clip_l.safetensors";
    private const string FluxClipUrl=
        "https://huggingface.co/comfyanonymous/flux_text_encoders/resolve/main/"+FluxClipFileName;
    private const string FluxClipHash=
        "660c6f5b1abae9dc498ac2d21e1347d2abdb0cf6c0c0c8576cd796491d9a6cdd";
    private const string FluxVaeFileName="ae.safetensors";
    private const string FluxVaeUrl=
        "https://huggingface.co/Comfy-Org/Lumina_Image_2.0_Repackaged/resolve/main/split_files/vae/"+FluxVaeFileName;
    private const string FluxVaeHash=
        "afc8e28272cd15db3919bacdb6918ce9c1ed22e96cb12c4d5ed0fba823529e38";
    private const string GgufNodeCommit="6ea2651e7df66d7585f6ffee804b20e92fb38b8a";


    public static string Root => Path.Combine(AppPaths.DataDirectory, "LocalAI");
    public static string PortableRoot => Path.Combine(Root,"ComfyUI_windows_portable");
    public static string CheckpointPath => Path.Combine(
        PortableRoot,"ComfyUI","models","checkpoints",SdxlFileName);
    public static string IllustratedCheckpointPath => Path.Combine(
        PortableRoot,"ComfyUI","models","checkpoints",IllustratedFileName);
    public static string FluxModelPath =>
        Path.Combine(PortableRoot,"ComfyUI","models","unet",FluxGgufFileName);
    public static string FluxT5Path =>
        Path.Combine(PortableRoot,"ComfyUI","models","text_encoders",FluxT5FileName);
    public static string FluxClipPath =>
        Path.Combine(PortableRoot,"ComfyUI","models","text_encoders",FluxClipFileName);
    public static string FluxVaePath =>
        Path.Combine(PortableRoot,"ComfyUI","models","vae",FluxVaeFileName);
    private static string GgufNodeFolder =>
        Path.Combine(PortableRoot,"ComfyUI","custom_nodes","ComfyUI-GGUF");
    public bool HasFluxSchnell =>
        File.Exists(FluxModelPath) && File.Exists(FluxT5Path) &&
        File.Exists(FluxClipPath) && File.Exists(FluxVaePath) &&
        Directory.Exists(GgufNodeFolder);

    private static readonly Uri Server = new("http://127.0.0.1:8188/");
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All
    }) { Timeout=Timeout.InfiniteTimeSpan };
    private Process? _serverProcess;
    private string? _activePromptId;

    /// <summary>
    /// Force-stop only work initiated by ZeroVideo. An unrelated ComfyUI
    /// server is never terminated; if we launched ComfyUI, its process tree
    /// can be stopped. A pending job is interrupted before process shutdown.
    /// </summary>
    public async Task InterruptOwnedGenerationAsync()
    {
        var active=_activePromptId;
        if(active is not null)
        {
            try
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var request=await Http.PostAsync(new Uri(Server,"interrupt"),
                    new StringContent("{}"),timeout.Token);
            }
            catch { /* The engine may already have exited. */ }
            finally { _activePromptId=null; }
        }
        var process=_serverProcess;
        if(process is not null)
        {
            try { if(!process.HasExited)process.Kill(entireProcessTree:true); }
            catch(InvalidOperationException) { }
            catch(System.ComponentModel.Win32Exception) { }
            _serverProcess=null;
        }
    }

    static LocalImageService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("ZeroVideo-LocalAI/1.0");
    }

    private static async Task DownloadAsync(string url, string destination,
        string? expectedSha,Action<string>? log,CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial=destination+".partial";
        var resume=File.Exists(partial)?new FileInfo(partial).Length:0;
        using var req=new HttpRequestMessage(HttpMethod.Get,url);
        if(resume>0)req.Headers.Range=new RangeHeaderValue(resume,null);
        using var response=await Http.SendAsync(req,
            HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        var appending=resume>0&&response.StatusCode==HttpStatusCode.PartialContent &&
            response.Content.Headers.ContentRange?.From==resume;
        if(!appending)resume=0;
        var total=response.Content.Headers.ContentLength.GetValueOrDefault()+resume;
        await using(var input=await response.Content.ReadAsStreamAsync(ct))
        await using(var output=new FileStream(partial,appending?
            FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.None,1024*1024,true))
        {
            var buffer=new byte[1024*1024];long done=resume,logged=done;
            while(true)
            {
                var read=await input.ReadAsync(buffer,ct);
                if(read==0)break;
                await output.WriteAsync(buffer.AsMemory(0,read),ct);
                done+=read;
                if(done-logged>=200L*1024*1024)
                {
                    logged=done;
                    log?.Invoke($"Downloaded {done/1073741824.0:N2} GB"+
                        (total>0?$" / {total/1073741824.0:N2} GB":""));
                }
            }
            await output.FlushAsync(ct);
        }
        if(total>0&&new FileInfo(partial).Length!=total)
            throw new IOException("Incomplete download. Click Install Everything to resume.");
        if(expectedSha is not null)
        {
            log?.Invoke("Verifying SHA-256 for "+Path.GetFileName(destination));
            await using var stream=File.OpenRead(partial);
            var sha=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct));
            if(!sha.Equals(expectedSha,StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException("Model SHA-256 mismatch. Download rejected.");
            }
        }
        File.Move(partial,destination,true);
    }

    private static string? FindArchiveExtractor()
    {
        // Recent Windows tar.exe includes libarchive and handles .7z; do not
        // require a Python or third-party archiver installation.
        var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var tar=Path.Combine(windows,"System32","tar.exe");
        if(File.Exists(tar))return tar;
        return new[]{Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles),"7-Zip","7z.exe"),
            Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86),"7-Zip","7z.exe")}
            .FirstOrDefault(File.Exists);
    }

    private static async Task<string> RunToolAsync(string program,
        IEnumerable<string> arguments,string workingDirectory,CancellationToken ct)
    {
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo(program)
        {
            WorkingDirectory=workingDirectory,
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardError=true,RedirectStandardOutput=true
        };
        foreach(var arg in arguments)process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start())throw new IOException("Could not start "+program);
        var stdout=process.StandardOutput.ReadToEndAsync(ct);
        var stderr=process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var (o,e)=(await stdout,await stderr);
        if(process.ExitCode!=0)
            throw new IOException(Path.GetFileName(program)+" exited "+process.ExitCode+
                ": "+e[..Math.Min(700,e.Length)]);
        return o;
    }

    private static string PortablePython =>
        new[]{Path.Combine(PortableRoot,"python_embeded","python.exe"),
            Path.Combine(PortableRoot,"python_embedded","python.exe")}
            .FirstOrDefault(File.Exists)??"";

    public bool HasComfyUi =>
        File.Exists(Path.Combine(PortableRoot,"ComfyUI","main.py")) &&
        PortablePython.Length>0;
    public bool HasCheckpoint => File.Exists(CheckpointPath) &&
        new FileInfo(CheckpointPath).Length>6_000_000_000;
    public bool HasIllustratedCheckpoint =>
        File.Exists(IllustratedCheckpointPath) &&
        new FileInfo(IllustratedCheckpointPath).Length>6_000_000_000;

    public async Task InstallIllustratedCheckpointAsync(Action<string>? log,CancellationToken ct)
    {
        if(!HasComfyUi)
            throw new InvalidOperationException(
                "ComfyUI is not installed. Use Install Everything first.");
        Directory.CreateDirectory(Path.GetDirectoryName(IllustratedCheckpointPath)!);
        if(!HasIllustratedCheckpoint)
        {
            var drive=new DriveInfo(Path.GetPathRoot(IllustratedCheckpointPath)!);
            if(drive.AvailableFreeSpace<8L*1024*1024*1024)
                throw new IOException("Need at least 8 GB available for DreamShaper XL.");
            log?.Invoke("Downloading creator-published DreamShaper XL illustrated checkpoint (6.94 GB).");
            await DownloadAsync(IllustratedUrl,IllustratedCheckpointPath,
                IllustratedSha256,log,ct);
        }
        else
        {
            log?.Invoke("Verifying already downloaded DreamShaper XL image model...");
            await using var stream=File.OpenRead(IllustratedCheckpointPath);
            var actual=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct));
            if(!actual.Equals(IllustratedSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Existing DreamShaper XL checksum mismatch. Do not use the model. " +
                    "Remove the invalid checkpoint from the local model folder before reinstalling.");
        }
        log?.Invoke("DreamShaper XL verified and ready in ComfyUI/checkpoints.");
    }


    private static async Task EnsureVerifiedModelAsync(
        string url,string path,string expectedSha,Action<string>? log,CancellationToken ct)
    {
        if(File.Exists(path))
        {
            await using var existing=File.OpenRead(path);
            var digest=Convert.ToHexString(await SHA256.HashDataAsync(existing,ct));
            if(!digest.Equals(expectedSha,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Existing model checksum does not match published SHA-256: "+
                    Path.GetFileName(path)+". Please move the invalid file away before retrying.");
            log?.Invoke("Verified existing file: "+Path.GetFileName(path));
            return;
        }
        await DownloadAsync(url,path,expectedSha,log,ct);
    }

    private async Task InstallGgufNodesAsync(Action<string>? log,CancellationToken ct)
    {
        if(Directory.Exists(GgufNodeFolder))
        {
            log?.Invoke("ComfyUI-GGUF nodes already present.");
            return;
        }
        var uri="https://github.com/city96/ComfyUI-GGUF/archive/"+GgufNodeCommit+".zip";
        var zipPath=Path.Combine(Root,"ComfyUI-GGUF-"+GgufNodeCommit+".zip");
        if(!File.Exists(zipPath))
            await DownloadAsync(uri,zipPath,null,log,ct);
        var temporary=GgufNodeFolder+".installing";
        if(Directory.Exists(temporary))Directory.Delete(temporary,true);
        Directory.CreateDirectory(temporary);
        try
        {
            using var archive=ZipFile.OpenRead(zipPath);
            foreach(var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var parts=entry.FullName.Replace('\\','/').Split('/');
                if(parts.Length<2)continue;
                var localParts=parts.Skip(1).Where(x=>x.Length>0).ToList();
                if(localParts.Any(x=>x=="."||x==".."||x.Contains(':')))
                    throw new InvalidDataException("Unsafe ComfyUI node archive path.");
                var finalPath=Path.GetFullPath(Path.Combine(temporary,
                    Path.Combine(localParts.ToArray())));
                if(!finalPath.StartsWith(Path.GetFullPath(temporary)+Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("ComfyUI node ZIP path escaped its install folder.");
                if(entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(finalPath);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                entry.ExtractToFile(finalPath,true);
            }
            var required=Path.Combine(temporary,"requirements.txt");
            if(!File.Exists(required))
                throw new InvalidDataException("ComfyUI-GGUF installer lacks requirements.txt.");
            log?.Invoke("Installing the GGUF Python dependency in ComfyUI's bundled Python...");
            await RunToolAsync(PortablePython,
                new[]{"-s","-m","pip","install","--disable-pip-version-check","-r",required},
                PortableRoot,ct);
            Directory.Move(temporary,GgufNodeFolder);
            log?.Invoke("ComfyUI-GGUF nodes installed. Restart ComfyUI to load them.");
        }
        catch
        {
            try{if(Directory.Exists(temporary))Directory.Delete(temporary,true);}catch{}
            throw;
        }
    }

    public async Task InstallFluxSchnellAsync(Action<string>? log,CancellationToken ct)
    {
        if(!HasComfyUi)
            throw new InvalidOperationException(
                "Install the ComfyUI NVIDIA portable runtime first using Install Everything.");
        var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Root))!);
        if(drive.AvailableFreeSpace<14L*1024*1024*1024&&!HasFluxSchnell)
            throw new IOException("Need approximately 14 GB of free space for quantized FLUX, "+
                "its text encoders and VAE. Existing SDXL/DreamShaper files are kept.");
        await InstallGgufNodesAsync(log,ct);
        log?.Invoke("Downloading FLUX.1-schnell Q4_K_S and required encoders. "+
            "All downloads resume after interruptions and are SHA-256 verified.");
        await EnsureVerifiedModelAsync(FluxGgufUrl,FluxModelPath,FluxGgufHash,log,ct);
        await EnsureVerifiedModelAsync(FluxT5Url,FluxT5Path,FluxT5Hash,log,ct);
        await EnsureVerifiedModelAsync(FluxClipUrl,FluxClipPath,FluxClipHash,log,ct);
        await EnsureVerifiedModelAsync(FluxVaeUrl,FluxVaePath,FluxVaeHash,log,ct);
        log?.Invoke("FLUX.1-schnell Q4 and its three supporting model files verified. "+
            "Restart any running ComfyUI instance to load the new GGUF nodes. "+
            "On 8 GB GPUs, CPU/RAM offloading can still make generation slow.");
    }

    public async Task InstallEverythingAsync(Action<string>? log,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows NVIDIA portable installer only.");
        Directory.CreateDirectory(Root);
        var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Root))!);
        // Archive + extracted portable Python/PyTorch + 7GB checkpoint.
        if(!HasComfyUi && drive.AvailableFreeSpace<18L*1024*1024*1024)
            throw new IOException("The first installation needs at least 18 GB free on "+
                drive.Name+". Free space or move LocalAppData to a larger drive.");
        if(!HasComfyUi)
        {
            var archiver=FindArchiveExtractor() ??
                throw new IOException("Windows tar/7-Zip was not found. Update Windows or install 7-Zip.");
            var archive=Path.Combine(Root,"ComfyUI_windows_portable_nvidia.7z");
            if(!File.Exists(archive))
            {
                log?.Invoke("Downloading official ComfyUI NVIDIA portable package. Large download; partial data is resumable.");
                await DownloadAsync(ComfyUiArchiveUrl,archive,null,log,ct);
            }
            log?.Invoke("Checking archive paths before extraction...");
            var is7Zip=Path.GetFileName(archiver).Equals("7z.exe",StringComparison.OrdinalIgnoreCase);
            var list=await RunToolAsync(archiver,
                is7Zip?new[]{"l","-slt",archive}:new[]{"-tf",archive},Root,ct);
            if(!is7Zip)
            {
                foreach(var line in list.Split('\n',StringSplitOptions.RemoveEmptyEntries))
                {
                    var entry=line.Trim().Replace('\\','/');
                    if(entry.StartsWith('/')||entry.Contains("../",StringComparison.Ordinal)||
                       entry.Contains(":/",StringComparison.Ordinal) ||
                       entry.StartsWith("..",StringComparison.Ordinal))
                        throw new InvalidDataException("Unsafe path in downloaded ComfyUI archive.");
                }
            }
            var extract=Path.Combine(Root,"ComfyUI_extract");
            if(Directory.Exists(extract))Directory.Delete(extract,true);
            Directory.CreateDirectory(extract);
            log?.Invoke("Extracting ComfyUI and bundled CUDA/Python files...");
            await RunToolAsync(archiver,is7Zip?
                new[]{"x","-y","-o"+extract,archive}:
                new[]{"-xf",archive,"-C",extract},Root,ct);
            var candidate=Directory.EnumerateFiles(extract,"main.py",
                SearchOption.AllDirectories)
                .Select(x=>Directory.GetParent(Path.GetDirectoryName(x)!)?.FullName)
                .FirstOrDefault(folder=>folder is not null&&
                    (File.Exists(Path.Combine(folder,"python_embeded","python.exe")) ||
                     File.Exists(Path.Combine(folder,"python_embedded","python.exe"))));
            if(candidate is null)
                throw new InvalidDataException("Downloaded ComfyUI portable package has an unknown layout.");
            if(Directory.Exists(PortableRoot))
                throw new IOException("Existing ComfyUI folder found but incomplete. Back it up before retrying.");
            Directory.Move(candidate,PortableRoot);
            // Only discard downloads after successful extraction.
            try{File.Delete(archive);}catch{}
            try{Directory.Delete(extract,true);}catch{}
            log?.Invoke("ComfyUI NVIDIA portable installed in "+PortableRoot);
        }
        else log?.Invoke("ComfyUI already installed; reusing it.");
        if(!HasCheckpoint)
        {
            var modelsDir=Path.GetDirectoryName(CheckpointPath)!;
            Directory.CreateDirectory(modelsDir);
            var driveModel=new DriveInfo(Path.GetPathRoot(CheckpointPath)!);
            if(driveModel.AvailableFreeSpace<8L*1024*1024*1024)
                throw new IOException("Need at least 8 GB free for the SDXL checkpoint.");
            log?.Invoke("Downloading official Stability AI SDXL base model (6.94 GB), resumable...");
            await DownloadAsync(SdxlUrl,CheckpointPath,SdxlSha256,log,ct);
        }
        else
        {
            log?.Invoke("SDXL checkpoint found; validating existing model checksum...");
            await using var stream=File.OpenRead(CheckpointPath);
            var hash=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct));
            if(!hash.Equals(SdxlSha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installed SDXL model checksum differs from official SHA-256.");
        }
        log?.Invoke("Local image engine fully installed. No additional Python, CUDA or model downloads needed.");
    }

    public async Task<bool> IsOnlineAsync(CancellationToken ct)
    {
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var resp=await Http.GetAsync(new Uri(Server,"system_stats"),timeout.Token);
            return resp.IsSuccessStatusCode;
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return false;}
        catch(HttpRequestException){return false;}
    }

    public async Task RestartOwnedServerAsync(Action<string>? log,CancellationToken ct)
    {
        // Do not kill an external ComfyUI session. Only restart the server
        // process ZeroVideo created so its newly installed GGUF nodes load.
        if(_serverProcess is null)
        {
            log?.Invoke("If ComfyUI was launched outside ZeroVideo, restart "+
                "that server manually to load newly installed GGUF nodes.");
            return;
        }
        try { if(!_serverProcess.HasExited)_serverProcess.Kill(entireProcessTree:true); }
        catch { }
        try { _serverProcess.Dispose(); } catch { }
        _serverProcess=null;
        await Task.Delay(800,ct);
        await StartAsync(log,ct);
    }

    public async Task StartAsync(Action<string>? log,CancellationToken ct)
    {
        if(await IsOnlineAsync(ct))
        {
            log?.Invoke("ComfyUI already running at 127.0.0.1:8188.");
            return;
        }
        if(!HasComfyUi)throw new FileNotFoundException(
            "Install ComfyUI using the Install Everything button first.");
        var psi=new ProcessStartInfo(PortablePython)
        {
            WorkingDirectory=PortableRoot,UseShellExecute=false,
            CreateNoWindow=true,RedirectStandardOutput=false,RedirectStandardError=false
        };
        foreach(var arg in new[]{"-s","ComfyUI/main.py",
            "--windows-standalone-build","--lowvram","--disable-auto-launch",
            "--listen","127.0.0.1","--port","8188"})
            psi.ArgumentList.Add(arg);
        _serverProcess=Process.Start(psi) ??
            throw new IOException("Could not launch ComfyUI's embedded Python.");
        log?.Invoke("Starting local ComfyUI. First CUDA startup can take a while...");
        for(var i=0;i<120;i++)
        {
            ct.ThrowIfCancellationRequested();
            if(await IsOnlineAsync(ct)) { log?.Invoke("ComfyUI is online.");return; }
            if(_serverProcess.HasExited)
                throw new IOException("ComfyUI exited during startup. Check NVIDIA drivers and ComfyUI logs.");
            await Task.Delay(1000,ct);
        }
        throw new TimeoutException("Local ComfyUI did not respond within two minutes.");
    }

    public async Task<string> GenerateAsync(VideoProject project,ProjectChapter chapter,
        ProjectScene scene,ZeroVideoSettings settings,Action<string>? log,CancellationToken ct)
    {
        var target=ProjectMedia.GeneratedImagePath(project,chapter,scene);
        var cast=CharacterConsistencyService.Match(project,scene);
        var main=cast.FirstOrDefault();
        var descriptions=cast.Count==0?"":string.Join("; ",cast.Select(character=>
            character.Name+": "+character.Appearance));
        var reference=main is null?null:
            CharacterConsistencyService.GetExistingReference(project,main);
        var seed=main is null?(long?)null:
            1+(long)((unchecked((ulong)main.Seed)+
                (ulong)chapter.Number*113+(ulong)scene.Number*997)%
                ((ulong)long.MaxValue-1));
        if(main is not null)
            log?.Invoke("Character Lock "+main.Name+": "+
                (reference is not null?"reusing saved visual reference":"creating first reference")+
                ", stable seed family.");
        await GenerateToPathAsync(project.Title,project.Topic,project.Style,project.AspectRatio,
            chapter.Title,scene.Narration,scene.ImagePrompt,scene.VisualQuery,
            target,settings,log,ct,seed,reference,descriptions);
        scene.MediaPath=target;
        if(main is not null&&reference is null)
        {
            var anchor=CharacterConsistencyService.CaptureFirstReference(
                project,main,target);
            log?.Invoke("Character Lock: saved reference for "+main.Name+
                " at "+anchor);
        }
        return target;
    }

    private static async Task<string> UploadCharacterReferenceAsync(
        string localFile,CancellationToken ct)
    {
        if(!File.Exists(localFile))throw new FileNotFoundException(
            "Character reference image missing",localFile);
        if(new FileInfo(localFile).Length>25*1024*1024)
            throw new InvalidDataException("Character reference exceeds 25 MB.");
        using var form=new MultipartFormDataContent();
        await using var from=File.OpenRead(localFile);
        using var image=new StreamContent(from);
        image.Headers.ContentType=new MediaTypeHeaderValue(
            Path.GetExtension(localFile).ToLowerInvariant()==".png"?
                "image/png":"image/jpeg");
        form.Add(image,"image",Path.GetFileName(localFile));
        form.Add(new StringContent("input"),"type");
        form.Add(new StringContent("true"),"overwrite");
        using var response=await Http.PostAsync(new Uri(Server,"upload/image"),form,ct);
        var body=await response.Content.ReadAsStringAsync(ct);
        if(!response.IsSuccessStatusCode)
            throw new IOException("ComfyUI could not load character reference: "+
                body[..Math.Min(body.Length,350)]);
        using var doc=JsonDocument.Parse(body);
        var name=doc.RootElement.GetProperty("name").GetString()??"";
        if(name.Length==0)throw new InvalidDataException(
            "ComfyUI returned an empty reference filename.");
        return name;
    }

    private static string CompactLocalText(string? value,int limit=360)
    {
        if(string.IsNullOrWhiteSpace(value))return "";
        var clean=string.Join(' ',value.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        return clean[..Math.Min(clean.Length,limit)];
    }

    // Short, distinct style cues keep the most important artistic instructions
    // early in SDXL's text encoding rather than burying them after a verbose
    // project title and hundreds of tokens of metadata.
    private static string IllustratedStyleCue(string style) => style switch
    {
        "Cinematic Comic Explainer" =>
            "Premium 2D cinematic graphic novel illustration, prominent expressive " +
            "inked human characters, sharp hand-drawn black contours, detailed cel-shaded " +
            "faces, dramatic directional rim lighting, deep rich colorful shadows, " +
            "dynamic composition, illustrated not a photograph, no anime or 3D",
        "Comic Book Explainer" =>
            "CLASSIC AMERICAN VINTAGE PRINTED COMIC BOOK PAGE from the 1960s, " +
            "2 to 3 DIFFERENT colorful story panels with very THICK BLACK PANEL GUTTERS, " +
            "large dominant hero panel showing the main character, Ben-Day halftone " +
            "dots, hard black ink, crosshatching, imperfect CMYK print, vibrant " +
            "red yellow cyan blue and cream flat print colors, aged pulp paper, " +
            "NOT monochrome, NOT a single black-and-white sketch",
        "Editorial Cartoon Explainer" =>
            "Colorful 2D editorial cartoon documentary illustration, prominent " +
            "expressive human protagonist as the main focal point, slightly " +
            "exaggerated hand-drawn faces, bold clean ink contours, warm painterly " +
            "cel shading, recognizable detailed setting, dynamic storytelling, " +
            "never photorealistic, no empty still-life composition if people appear",
        "Cartoon explainer" =>
            "Clean expressive 2D cartoon illustration, clear focal subject, bold " +
            "contour lines, bright intentional colors, flat cel shading",
        "Stickman explainer" =>
            "Strict stick-figure 2D educational cartoon, round white heads, simple " +
            "black eyes and mouths, stick limbs and minimal flat colors, " +
            "NO detailed human anatomy",
        "Whiteboard explainer" =>
            "Whiteboard marker drawing with uncluttered white background, simple black " +
            "lines, a few accent colors, readable educational spatial hierarchy",
        "Hand-drawn explainer" =>
            "Sketchbook editorial illustration, organic pencil and ink linework, " +
            "hand-rendered textures, lightly colored paper, not photorealistic",
        "VOX-inspired paper/editorial explainer" =>
            "Paper-cut editorial documentary collage, aged newspapers, tactile torn " +
            "paper layers, archival photos and halftone-print details, clear story subject",
        "History documentary" =>
            "Historically plausible period reconstruction, realistic old architecture " +
            "and clothing, archival photo color treatment, dramatic documentary composition",
        "Mystery / investigation" =>
            "Tense investigative documentary illustration, accurate evidence and period " +
            "clues, cinematic noir lighting, main story subject sharply visible",
        "Science explainer" =>
            "Accurate explanatory science visualization, clear simple cause-and-effect " +
            "composition, realistic scale, no unnecessary readable text",
        "Documentary explainer with fast visual variety" =>
            "Detailed documentary illustration with clear subject, evidence, maps " +
            "and environment as appropriate, editorial lighting, restrained style",
        _ => "Detailed scene-specific illustration, intentional composition, " +
            "consistent style, clear subject and recognizable setting"
    };

    public async Task GenerateToPathAsync(string title,string topic,string style,
        string ratio,string chapter,string narration,string direction,string query,
        string destination,ZeroVideoSettings settings,Action<string>? log,CancellationToken ct,
        long? fixedSeed=null,string? characterReferenceImage=null,
        string? characterDescription=null)
    {
        if(!await IsOnlineAsync(ct))
            await StartAsync(log,ct);
        var checkpoint=string.IsNullOrWhiteSpace(settings.LocalImageCheckpointFile)?
            SdxlFileName:Path.GetFileName(settings.LocalImageCheckpointFile);
        var isFlux=checkpoint==FluxGgufFileName;
        // Exact file name only. Never accept arbitrary local/network paths in
        // the ComfyUI checkpoint loader.
        if(isFlux)
        {
            if(!HasFluxSchnell)
                throw new FileNotFoundException("FLUX.1-schnell Q4 is incomplete. "+
                    "Use the Install FLUX (8GB GPU) button first.");
        }
        else if(checkpoint != SdxlFileName &&
           !File.Exists(Path.Combine(PortableRoot,"ComfyUI","models","checkpoints",checkpoint)))
            throw new FileNotFoundException("Local checkpoint is not installed: "+checkpoint);
        if(checkpoint==SdxlFileName && !HasCheckpoint)
            throw new FileNotFoundException("SDXL model is missing. Click Install Everything.");
        if(checkpoint==IllustratedFileName && !HasIllustratedCheckpoint)
            throw new FileNotFoundException("DreamShaper XL not installed.");
        var portrait=ratio=="9:16";
        // Reduce FLUX's working resolution to help an 8 GB GPU; default SDXL
        // dimensions and all other existing generation settings remain intact.
        var width=portrait?(isFlux?576:704):(isFlux?1024:1248);
        var height=portrait?(isFlux?1024:1248):(isFlux?576:704);
        var isComicPage=style=="Comic Book Explainer";
        var styleCue=IllustratedStyleCue(style);
        // Most important text first, then the action and props. A previous
        // verbose prompt buried the main character in hundreds of words.
        var positive=string.Join(". ",new[]{
            styleCue,
            !string.IsNullOrWhiteSpace(characterDescription)?
                "LOCKED RECURRING CHARACTERS: "+CompactLocalText(characterDescription,460):"",
            "MOST IMPORTANT: "+CompactLocalText(narration,420),
            "SHOW ON SCREEN: "+CompactLocalText(direction,240),
            "Key props and setting: "+CompactLocalText(query,120),
            "Video topic: "+CompactLocalText(topic,85),
            portrait?"vertical 9:16 portrait":"horizontal 16:9 widescreen",
            isComicPage?"complete multi-panel printed comic PAGE":"one coherent illustrated scene",
            "no captions, readable text, watermarks, logos or interface"
        }.Where(x=>!string.IsNullOrWhiteSpace(x)));
        var negative="bad anatomy, distorted hands, blurry, low-detail, text, " +
            "words, lettering, speech bubbles, captions, watermark, logo, "+
            "interface, screen capture, random extra objects, generic empty room";
        if(style=="Comic Book Explainer")
            negative+=", grayscale only, monochrome, black-and-white-only, "+
                "glossy digital illustration, smooth gradients, photorealism";
        if(style=="Cinematic Comic Explainer"||
           style=="Editorial Cartoon Explainer")
            negative+=", photorealistic, empty still life, subject missing, " +
                "stock photo, 3D, plastic faces";
        static object Ref(string id,int n)=>new object[]{id,n};
        var graph=new Dictionary<string,object>();
        var seed=fixedSeed??Random.Shared.NextInt64(1,long.MaxValue);
        object latent=Ref("5",0);
        if(isFlux)
        {
            // Flux Schnell uses GGUF UNET, dual text encoders, FluxGuidance,
            // an SD3-shaped empty latent and FOUR sampling steps.
            graph["4"]=new {class_type="UnetLoaderGGUF",inputs=new {unet_name=FluxGgufFileName}};
            graph["11"]=new {class_type="DualCLIPLoader",inputs=new
            {
                clip_name1=FluxClipFileName,clip_name2=FluxT5FileName,
                type="flux",device="default"
            }};
            graph["12"]=new {class_type="CLIPTextEncode",inputs=new
                {clip=Ref("11",0),text=positive}};
            graph["13"]=new {class_type="FluxGuidance",inputs=new
                {conditioning=Ref("12",0),guidance=3.5}};
            graph["20"]=new {class_type="VAELoader",inputs=new{vae_name=FluxVaeFileName}};
            graph["5"]=new {class_type="EmptySD3LatentImage",inputs=new
                {width,height,batch_size=1}};
        }
        else
        {
            graph["4"]=new {class_type="CheckpointLoaderSimple",
                inputs=new{ckpt_name=checkpoint}};
            graph["6"]=new {class_type="CLIPTextEncode",
                inputs=new{clip=Ref("4",1),text=positive}};
            graph["7"]=new {class_type="CLIPTextEncode",
                inputs=new{clip=Ref("4",1),text=negative}};
            graph["5"]=new {class_type="EmptyLatentImage",
                inputs=new{width,height,batch_size=1}};
        }

        var hasReference=!string.IsNullOrWhiteSpace(characterReferenceImage) &&
            File.Exists(characterReferenceImage);
        if(hasReference)
        {
            var uploaded=await UploadCharacterReferenceAsync(characterReferenceImage,ct);
            graph["14"]=new{class_type="LoadImage",inputs=new{image=uploaded}};
            graph["15"]=new{class_type="ImageScale",inputs=new
            {
                image=Ref("14",0),upscale_method="lanczos",
                width,height,crop="center"
            }};
            graph["16"]=new{class_type="VAEEncode",inputs=new
            {
                pixels=Ref("15",0),
                vae=isFlux?Ref("20",0):Ref("4",2)
            }};
            latent=Ref("16",0);
            log?.Invoke("Character Lock: image-to-image identity reference loaded. "+
                "The master image guides appearance, but exact facial identity "+
                "cannot be guaranteed across all angles.");
        }
        if(isFlux)
            graph["3"]=new {class_type="KSampler",inputs=new
            {
                seed,steps=4,cfg=1.0,sampler_name="euler",scheduler="simple",
                denoise=hasReference?0.75:1.0,
                model=Ref("4",0),positive=Ref("13",0),negative=Ref("12",0),
                latent_image=latent
            }};
        else
            graph["3"]=new {class_type="KSampler",inputs=new
            {
                seed,steps=30,cfg=6.0,sampler_name="dpmpp_2m",scheduler="karras",
                denoise=hasReference?0.68:1.0,
                model=Ref("4",0),positive=Ref("6",0),negative=Ref("7",0),
                latent_image=latent
            }};
        graph["8"]=new{class_type="VAEDecode",inputs=new
        {
            samples=Ref("3",0),
            vae=isFlux?Ref("20",0):Ref("4",2)
        }};
        graph["9"]=new{class_type="SaveImage",
            inputs=new{filename_prefix="ZeroVideoLocal",images=Ref("8",0)}};
        log?.Invoke("ComfyUI generating "+width+"x"+height+" "+style+" image with "+
            (isFlux?"FLUX.1-schnell Q4":"SDXL illustrated checkpoint")+"...");
        using var req=new HttpRequestMessage(HttpMethod.Post,new Uri(Server,"prompt"))
        {
            Content=JsonContent.Create(new{prompt=graph,client_id="zerovideo-"+Guid.NewGuid().ToString("N")})
        };
        using var submit=await Http.SendAsync(req,ct);
        var submitBody=await submit.Content.ReadAsStringAsync(ct);
        if(!submit.IsSuccessStatusCode)
            throw new IOException("ComfyUI rejected workflow: "+
                submitBody[..Math.Min(submitBody.Length,950)]);
        using var ack=JsonDocument.Parse(submitBody);
        if(!ack.RootElement.TryGetProperty("prompt_id",out var token))
            throw new InvalidDataException("ComfyUI returned no prompt ID.");
        var promptId=token.GetString()??"";
        if(!Guid.TryParse(promptId,out _))
            throw new InvalidDataException("ComfyUI returned an invalid prompt ID.");
        _activePromptId=promptId;
        try
        {
        for(var i=0;i<420;i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000,ct);
            using var history=await Http.GetAsync(new Uri(Server,"history/"+
                Uri.EscapeDataString(promptId)),ct);
            history.EnsureSuccessStatusCode();
            using var doc=JsonDocument.Parse(await history.Content.ReadAsStringAsync(ct));
            if(!doc.RootElement.TryGetProperty(promptId,out var entry))continue;
            if(entry.TryGetProperty("status",out var status) &&
               status.TryGetProperty("status_str",out var state) &&
               state.GetString()=="error")
                throw new InvalidOperationException("ComfyUI reported an image generation error. "+
                    "See the local ComfyUI log; try a smaller resolution or a compatible checkpoint.");
            if(!entry.TryGetProperty("outputs",out var outputs) ||
               !outputs.TryGetProperty("9",out var output) ||
               !output.TryGetProperty("images",out var images) ||
               images.GetArrayLength()==0)continue;
            var image=images[0];
            var filename=image.GetProperty("filename").GetString()??"";
            var subfolder=image.TryGetProperty("subfolder",out var sub)?
                sub.GetString()??"":"";
            var type=image.TryGetProperty("type",out var t)?t.GetString()??"output":"output";
            if(filename.Length==0||type!="output")
                throw new InvalidDataException("Invalid image reference from ComfyUI.");
            var uri=new Uri(Server,"view?filename="+Uri.EscapeDataString(filename)+
                "&subfolder="+Uri.EscapeDataString(subfolder)+"&type=output");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary=destination+".partial";
            using var img=await Http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);
            img.EnsureSuccessStatusCode();
            await using(var from=await img.Content.ReadAsStreamAsync(ct))
            await using(var to=File.Create(temporary))
                await from.CopyToAsync(to,ct);
            await using(var verify=File.OpenRead(temporary))
            {
                var png=new byte[8];
                if(await verify.ReadAsync(png,ct)!=8 ||
                   !png.SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))
                    throw new InvalidDataException("ComfyUI returned an invalid PNG.");
            }
            File.Move(temporary,destination,true);
            log?.Invoke("Local image saved: "+destination);
            return;
        }
        throw new TimeoutException("ComfyUI generation exceeded 14 minutes.");
        }
        finally { if(_activePromptId==promptId)_activePromptId=null; }
    }
}
