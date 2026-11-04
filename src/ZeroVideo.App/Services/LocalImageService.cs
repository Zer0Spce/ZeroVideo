using System.Collections.Concurrent;
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
/// <summary>Configuration or startup error that cannot be fixed by rerunning a scene.</summary>
public sealed class LocalImageSetupException(string message) : InvalidOperationException(message);

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
    private readonly ConcurrentDictionary<int,Process> _activeInstallTools=new();

    /// <summary>
    /// Force-stop only work initiated by ZeroVideo. An unrelated ComfyUI
    /// server is never terminated; if we launched ComfyUI, its process tree
    /// can be stopped. A pending job is interrupted before process shutdown.
    /// </summary>
    public async Task InterruptOwnedGenerationAsync()
    {
        var active=_activePromptId;
        foreach(var child in _activeInstallTools.Values)
            try { if(!child.HasExited)child.Kill(entireProcessTree:true); }
            catch { /* The child may have already completed. */ }
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
        // Never kill an idle ComfyUI instance simply because a TikTok or
        // ChatGPT job was force-stopped. Only a managed active image prompt
        // justifies terminating the engine.
        if(active is not null && process is not null)
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

    private async Task<string> RunToolAsync(string program,
        IEnumerable<string> arguments,string workingDirectory,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo(program)
        {
            WorkingDirectory=workingDirectory,
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardError=true,RedirectStandardOutput=true
        };
        foreach(var arg in arguments)process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start())throw new IOException("Could not start "+program);
        _activeInstallTools[process.Id]=process;
        using var registration=ct.Register(()=>
        {
            try { if(!process.HasExited)process.Kill(entireProcessTree:true); }
            catch { }
        });
        try
        {
            var stdout=process.StandardOutput.ReadToEndAsync(ct);
            var stderr=process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var (o,e)=(await stdout,await stderr);
            if(process.ExitCode!=0)
                throw new IOException(Path.GetFileName(program)+" exited "+process.ExitCode+
                    ": "+e[..Math.Min(700,e.Length)]);
            return o;
        }
        finally
        {
            _activeInstallTools.TryRemove(process.Id,out _);
            if(ct.IsCancellationRequested)
                try { if(!process.HasExited)process.Kill(entireProcessTree:true); }
                catch { }
        }
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
            log?.Invoke("ComfyUI-GGUF files already present. "+
                "The Repair button verifies Python dependencies and the live node registry.");
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
            var expectedRoot="ComfyUI-GGUF-"+GgufNodeCommit;
            var extractionRoot=Path.GetFullPath(temporary)
                .TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+
                Path.DirectorySeparatorChar;
            foreach(var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var archiveName=entry.FullName.Replace('\\','/');
                // GitHub ZIPs contain a harmless top-level directory entry:
                // "ComfyUI-GGUF-<commit>/". It resolves to the extraction
                // root itself, so skip it instead of calling it a traversal.
                if(archiveName.Equals(expectedRoot+"/",StringComparison.OrdinalIgnoreCase))
                    continue;
                var prefix=expectedRoot+"/";
                if(!archiveName.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unexpected ComfyUI-GGUF archive root.");
                var relative=archiveName[prefix.Length..];
                if(relative.Length==0)continue;
                var segments=relative.Split('/',StringSplitOptions.RemoveEmptyEntries);
                if(relative.StartsWith('/') ||
                   segments.Any(x=>x=="."||x==".."||x.Contains(':')) ||
                   segments.Length==0)
                    throw new InvalidDataException("Unsafe ComfyUI node archive path.");
                var finalPath=Path.GetFullPath(Path.Combine(temporary,
                    Path.Combine(segments)));
                if(!finalPath.StartsWith(extractionRoot,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("ComfyUI node ZIP path escaped its install folder.");
                if(archiveName.EndsWith('/'))
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

    /// <summary>
    /// ComfyUI being online does NOT mean an optional GGUF custom node loaded.
    /// Query the live node registry before submitting any FLUX workflow.
    /// </summary>
    public async Task<bool> IsGgufLoaderRegisteredAsync(CancellationToken ct)
    {
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(7));
            using var response=await Http.GetAsync(
                new Uri(Server,"object_info/UnetLoaderGGUF"),timeout.Token);
            if(!response.IsSuccessStatusCode)return false;
            using var doc=JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(timeout.Token));
            return doc.RootElement.ValueKind==JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("UnetLoaderGGUF",out var definition) &&
                definition.ValueKind==JsonValueKind.Object;
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return false;}
        catch(HttpRequestException){return false;}
        catch(JsonException){return false;}
    }

    public async Task EnsureFluxLoaderReadyAsync(
        Action<string>? log,CancellationToken ct)
    {
        if(!HasFluxSchnell)
            throw new LocalImageSetupException(
                "FLUX model files or ComfyUI-GGUF extension are incomplete. " +
                "Open Local Images and click Install FLUX.1-schnell Q4 again.");
        if(!await IsOnlineAsync(ct))
            await StartAsync(log,ct);
        if(!await IsGgufLoaderRegisteredAsync(ct))
            throw new LocalImageSetupException(
                "ComfyUI is running but does not recognize UnetLoaderGGUF. " +
                "Go to Local Images > Repair FLUX GGUF Nodes. " +
                "If ComfyUI was started outside ZeroVideo, close that ComfyUI " +
                "server first, then start it again after repair. " +
                "ZeroVideo will not retry this same invalid workflow.");
    }

    public async Task RepairGgufNodesAsync(Action<string>? log,CancellationToken ct)
    {
        if(!HasComfyUi)
            throw new LocalImageSetupException(
                "Install ComfyUI using Install Everything before repairing GGUF.");
        log?.Invoke("Checking ComfyUI-GGUF custom node files and Python module...");
        await InstallGgufNodesAsync(log,ct);
        var init=Path.Combine(GgufNodeFolder,"__init__.py");
        var nodes=Path.Combine(GgufNodeFolder,"nodes.py");
        var requirements=Path.Combine(GgufNodeFolder,"requirements.txt");
        if(!File.Exists(init)||!File.Exists(nodes)||!File.Exists(requirements))
            throw new LocalImageSetupException(
                "ComfyUI-GGUF folder exists but is incomplete. " +
                "Preserve the folder and repair its contents manually before retrying.");
        try
        {
            await RunToolAsync(PortablePython,
                new[]{"-s","-c","import gguf; print('GGUF Python module OK')"},
                PortableRoot,ct);
            log?.Invoke("GGUF Python dependency loads successfully.");
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex)
        {
            log?.Invoke("GGUF Python import failed ("+ex.Message+
                "). Reinstalling the extension's required Python dependencies...");
            await RunToolAsync(PortablePython,
                new[]{"-s","-m","pip","install","--disable-pip-version-check",
                      "-r",requirements},PortableRoot,ct);
            // Fail visibly if pip reported success but the module still cannot import.
            await RunToolAsync(PortablePython,
                new[]{"-s","-c","import gguf; print('GGUF Python module OK')"},
                PortableRoot,ct);
        }
        // If we own the running server, restart it. A ComfyUI instance
        // launched by another process may have active work: never kill it.
        if(await IsOnlineAsync(ct))
        {
            if(_serverProcess is not null && !_serverProcess.HasExited)
            {
                log?.Invoke("Restarting ZeroVideo-owned ComfyUI so the custom "+
                    "GGUF node can register...");
                await RestartOwnedServerAsync(log,ct);
            }
            else
            {
                if(await IsGgufLoaderRegisteredAsync(ct))
                {
                    log?.Invoke("GGUF node is already registered by the running ComfyUI server.");
                    return;
                }
                throw new LocalImageSetupException(
                    "GGUF dependency and files are installed, but an already " +
                    "running ComfyUI server on 127.0.0.1:8188 has not loaded " +
                    "the node. Close that external ComfyUI server manually, " +
                    "then click Start Local Engine in ZeroVideo. Do not delete " +
                    "models or re-download anything.");
            }
        }
        else
            await StartAsync(log,ct);

        if(!await IsGgufLoaderRegisteredAsync(ct))
            throw new LocalImageSetupException(
                "ComfyUI restarted but UnetLoaderGGUF is still not registered. " +
                "The extension may have failed to import or your ComfyUI version " +
                "may be incompatible. Check ComfyUI startup logs for the " +
                "ComfyUI-GGUF import traceback; do not retry scene generation yet.");

        log?.Invoke("GGUF loader verified in ComfyUI's live node registry. "+
            "FLUX is ready for a test image.");
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

    /// <summary>
    /// Restart the dedicated ComfyUI for LivePortrait. A previous ZeroVideo
    /// session can leave its Python server on :8188 after _serverProcess is
    /// lost. Reclaim only a verified instance using our exact embedded
    /// Python, running ComfyUI/main.py and owning the port, and only after
    /// user confirmation. Never kill all python.exe processes.
    /// </summary>
    public async Task<string> RestartForLivePortraitAsync(
        Action<string>? log, Func<string,bool> confirmOldInstance,
        CancellationToken ct)
    {
        if (_serverProcess is { HasExited: false })
        {
            log?.Invoke("Restarting the ComfyUI Python process owned by this ZeroVideo session...");
            await RestartOwnedServerAsync(log,ct);
            return "Restarted ZeroVideo-owned ComfyUI at http://127.0.0.1:8188.";
        }

        if (!await IsOnlineAsync(ct))
        {
            log?.Invoke("ComfyUI is offline; starting a new local instance...");
            await StartAsync(log,ct);
            return "Started new ZeroVideo ComfyUI at http://127.0.0.1:8188.";
        }

        var pid = await FindStaleOwnedComfyUiOnPortAsync(ct);
        if (pid is null)
            throw new InvalidOperationException(
                "ComfyUI is already responding at 127.0.0.1:8188, but it is not " +
                "a verifiable ZeroVideo-managed Python instance. The restart " +
                "button will not terminate unrelated Python processes. Close " +
                "that ComfyUI server using its own window/Task Manager, then " +
                "click Start / Restart ComfyUI again.");

        var warning =
            "An older ZeroVideo ComfyUI process (PID " + pid.Value +
            ") is still listening on port 8188. ZeroVideo can safely " +
            "restart this specific Python instance to reload LivePortrait nodes." +
            Environment.NewLine + Environment.NewLine +
            "Running ComfyUI generations in that process will be interrupted. " +
            "Other Python processes and OmniVoice will remain untouched." +
            Environment.NewLine + Environment.NewLine +
            "Restart this ZeroVideo ComfyUI process?";
        if (!confirmOldInstance(warning))
        {
            log?.Invoke("ComfyUI restart canceled; no Python process was stopped.");
            return "Restart canceled; existing ComfyUI was not changed.";
        }

        // Reconfirm ownership immediately before termination, protecting
        // against stale PIDs and unrelated processes taking over the port.
        if (await FindStaleOwnedComfyUiOnPortAsync(ct) != pid)
            throw new IOException(
                "The ComfyUI process changed during confirmation. Nothing was " +
                "terminated. Click Restart again to inspect the current process.");

        using (var oldProcess = Process.GetProcessById(pid.Value))
        {
            log?.Invoke("Stopping only verified stale ZeroVideo ComfyUI PID " +
                pid.Value + "...");
            if (!oldProcess.HasExited)
            {
                oldProcess.Kill(entireProcessTree:true);
                await oldProcess.WaitForExitAsync(ct);
            }
        }
        // The old port can remain bound briefly while its child processes exit.
        for (var i=0; i<15 && await IsOnlineAsync(ct); i++)
            await Task.Delay(500,ct);
        if (await IsOnlineAsync(ct))
            throw new IOException(
                "Port 8188 is still occupied after stopping the old ZeroVideo " +
                "process. ZeroVideo will not start another GPU-heavy server. " +
                "Inspect the ComfyUI process in Task Manager.");

        await StartAsync(log,ct);
        return "Restarted the older ZeroVideo ComfyUI process at " +
               "http://127.0.0.1:8188. LivePortrait nodes can now reload.";
    }

    private async Task<int?> FindStaleOwnedComfyUiOnPortAsync(CancellationToken ct)
    {
        // Built-in Windows commands identify the TCP LISTENING owner
        // *and* the executable/command line; no extra NuGet dependencies.
        // EncodedCommand avoids quoting user-supplied paths in the shell.
        const string script =
            "$owners = @(Get-NetTCPConnection -State Listen -LocalPort 8188 " +
            "-ErrorAction SilentlyContinue | " +
            "Select-Object -ExpandProperty OwningProcess -Unique); " +
            "if($owners.Count -gt 0) { " +
            "Get-CimInstance Win32_Process -Filter \"Name = 'python.exe'\" | " +
            "Where-Object { $owners -contains $_.ProcessId } | " +
            "Select-Object ProcessId,ExecutablePath,CommandLine | " +
            "ConvertTo-Json -Compress }";
        var base64=Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var output=await new ProcessRunner().RunAsync("powershell.exe",
            "-NoProfile -NonInteractive -EncodedCommand " + base64, PortableRoot,ct);
        if(output.ExitCode!=0 || string.IsNullOrWhiteSpace(output.StdOut))
            return null;

        using var json=JsonDocument.Parse(output.StdOut);
        var root=json.RootElement;
        var candidates=root.ValueKind==JsonValueKind.Array
            ? root.EnumerateArray().Select(x=>x.Clone()).ToArray()
            : root.ValueKind==JsonValueKind.Object
                ? [root.Clone()]
                : [];

        foreach(var entry in candidates)
        {
            if(!entry.TryGetProperty("ProcessId",out var pidField) ||
               !pidField.TryGetInt32(out var pid) || pid<1 ||
               !entry.TryGetProperty("ExecutablePath",out var exeField) ||
               !entry.TryGetProperty("CommandLine",out var cmdField))
                continue;
            var exe=exeField.GetString();
            var cmd=cmdField.GetString()??"";
            if(string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                continue;
            if(!string.Equals(Path.GetFullPath(exe),
                Path.GetFullPath(PortablePython),StringComparison.OrdinalIgnoreCase))
                continue;
            if(!cmd.Contains("ComfyUI/main.py",StringComparison.OrdinalIgnoreCase) &&
               !cmd.Contains("ComfyUI\\main.py",StringComparison.OrdinalIgnoreCase))
                continue;
            if(!System.Text.RegularExpressions.Regex.IsMatch(cmd,
                @"--port(?:\s+|=)[""']?8188\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                continue;
            return pid;
        }
        return null;
    }

    public async Task RestartOwnedServerAsync(Action<string>? log,CancellationToken ct)
    {
        // Only restart the process this service actually launched. Old
        // ZeroVideo instances are handled by RestartForLivePortraitAsync with
        // a process/port check and explicit user approval.
        if(_serverProcess is null)
            throw new InvalidOperationException(
                "ComfyUI is running outside this ZeroVideo session. " +
                "Use the Restart button to inspect its process and approve a safe restart.");
        var old=_serverProcess;
        try
        {
            if(!old.HasExited)
            {
                old.Kill(entireProcessTree:true);
                await old.WaitForExitAsync(ct);
            }
        }
        finally
        {
            old.Dispose();
            _serverProcess=null;
        }
        for(var i=0;i<20 && await IsOnlineAsync(ct);i++)
            await Task.Delay(400,ct);
        if(await IsOnlineAsync(ct))
            throw new IOException(
                "ComfyUI port 8188 remained occupied after stopping this server. " +
                "Refusing to launch another Python instance until the old port is free.");
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
            character.Name+": "+CharacterConsistencyService.IdentityDescription(character)));
        var reference=main is null?null:
            CharacterConsistencyService.GetExistingReference(project,main);
        // Hash each scene independently. Small arithmetic offsets of a
        // fixed character seed and a full-scene img2img anchor made shots
        // unnecessarily alike in older builds.
        var seed=CharacterConsistencyService.StableSeed(
            project.Id+"|shot|"+chapter.Number+"|"+scene.Number+
            "|"+(main?.Seed.ToString()??"no-character"));
        if(main is not null)
            log?.Invoke("Character Bible "+main.Name+": identity guidance only; "+
                (reference is not null?"approved imported portrait selected. "
                    :"text description only; no previous scene image reused. ")+
                "Independent per-scene seed.");
        if(project.Style=="Comic Book Explainer" &&
           project.Options.VintageComicPanelComposition)
        {
            // Compose a genuine three-panel page from three individual,
            // close-up/medium/wide images rather than trusting diffusion to
            // invent reliable page geometry or readable speech bubbles.
            var panelsDir=Path.Combine(project.ProjectDirectory,"VintageComicPanels",
                chapter.Number.ToString("00")+"-"+scene.Number.ToString("00"));
            Directory.CreateDirectory(panelsDir);
            var panelPaths=new List<string>();
            var directions=new[]
            {
                "SINGLE PRINTED COMIC PANEL, WIDE establishing frame. "+
                    "Show this scene's distinct location and action, not a repeated shot. ",
                "SINGLE PRINTED COMIC PANEL, MEDIUM action frame. "+
                    "Show this scene's important action, with a person only if relevant. ",
                "SINGLE PRINTED COMIC PANEL, CLOSE-UP dramatic reaction or "+
                    "important object detail. Never copy the whole establishing shot. "
            };
            for(var i=0;i<3;i++)
            {
                ct.ThrowIfCancellationRequested();
                var panel=Path.Combine(panelsDir,"panel"+(i+1)+".png");
                panelPaths.Add(panel);
                var panelSeed=CharacterConsistencyService.StableSeed(
                    project.Id+"|"+chapter.Number+"|"+scene.Number+"|panel|"+i+
                    "|"+(main?.Seed.ToString()??"no-character"));
                if(!File.Exists(panel))
                {
                    await GenerateToPathAsync(
                        project.Title,project.Topic,"Vintage Comic Panel",
                        project.AspectRatio,chapter.Title,scene.Narration,
                        directions[i]+scene.ImagePrompt,scene.VisualQuery,
                        panel,settings,log,ct,panelSeed,reference,descriptions);
                }
                // Do not auto-capture a full panel as a character reference.
                // An approved imported portrait is the only image anchor.
            }
            var width=project.AspectRatio=="9:16"?704:1248;
            var height=project.AspectRatio=="9:16"?1248:704;
            VintageComicPageService.Compose(panelPaths,target,width,height);
            scene.MediaPath=target;
            log?.Invoke("Vintage comic page assembled with exactly 3 "+
                "separate illustrated panels and thick gutters.");
            return target;
        }
        await GenerateToPathAsync(project.Title,project.Topic,project.Style,project.AspectRatio,
            chapter.Title,scene.Narration,scene.ImagePrompt,scene.VisualQuery,
            target,settings,log,ct,seed,reference,descriptions);
        scene.MediaPath=target;
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
        var ext=Path.GetExtension(localFile).ToLowerInvariant();
        image.Headers.ContentType=new MediaTypeHeaderValue(
            ext==".png"?"image/png":ext==".webp"?"image/webp":"image/jpeg");
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
        "Vintage Comic Panel" =>
            "SINGLE 1960s printed American pulp comic PANEL, expressive inked " +
            "protagonist in prominent foreground, vintage four-color process " +
            "yellowed newsprint, flat teal mustard red orange and black inks, " +
            "bold hand-drawn black contour lines, tiny Ben-Day halftone " +
            "dots and crosshatch shading, visible aged paper grain, " +
            "retro comic color registration, NO lettering, NO speech bubbles, " +
            "no other panels, a single cinematic narrative moment, " +
            "NEVER a modern digitally painted graphic novel",
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
            // This is a setup error, not a transient image failure. Check
            // once before building/submitting an invalid FLUX workflow.
            await EnsureFluxLoaderReadyAsync(log,ct);
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
            "COMPOSITION: create a fresh scene-specific viewpoint, shot scale, "+
                "staging and environmental details. Any character reference "+
                "is for identity only, NEVER copy its background, camera angle, "+
                "position, lighting, vehicle or surroundings",
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
        if(style=="Vintage Comic Panel")
            negative+=", multiple panels, comic page layout, modern digital "+
                "painterly gradients, gray monochrome pencil, 3D, photorealism";
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
            log?.Invoke("Character Bible: approved portrait reference loaded. "+
                "Higher denoise prioritizes the current scene composition; "+
                "facial identity is not guaranteed.");
        }
        if(isFlux)
            graph["3"]=new {class_type="KSampler",inputs=new
            {
                seed,steps=4,cfg=1.0,sampler_name="euler",scheduler="simple",
                denoise=hasReference?0.89:1.0,
                model=Ref("4",0),positive=Ref("13",0),negative=Ref("12",0),
                latent_image=latent
            }};
        else
            graph["3"]=new {class_type="KSampler",inputs=new
            {
                seed,steps=30,cfg=6.0,sampler_name="dpmpp_2m",scheduler="karras",
                denoise=hasReference?0.86:1.0,
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
