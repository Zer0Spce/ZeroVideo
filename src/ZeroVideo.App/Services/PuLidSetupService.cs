using System.IO.Compression;
using System.Net.Http;
using System.Reflection;

namespace ZeroVideo.App.Services;

/// <summary>
/// User-initiated, non-destructive setup of the official PuLID-FLUX FaceNet
/// ComfyUI nodes in the *existing* ZeroVideo ComfyUI installation.
/// Never installs system Python, never removes another custom node folder,
/// and never downloads noncommercial InsightFace pretrained model weights.
/// </summary>
public sealed class PuLidSetupService(
    ProcessRunner runner, LocalImageService localImages)
{
    private const string Commit = "7c7362b806c2c0f4bde8742ada9e7cb05b44d249";
    private const string Repo = "ComfyUI_PuLID_Flux_ll";
    private const string ArchiveUrl =
        "https://github.com/lldacing/ComfyUI_PuLID_Flux_ll/archive/" +
        Commit + ".zip";

    public static string Folder => Path.Combine(
        LocalImageService.PortableRoot, "ComfyUI", "custom_nodes", Repo);

    private static string PythonExecutable => new[]
    {
        Path.Combine(LocalImageService.PortableRoot,
            "python_embeded","python.exe"),
        Path.Combine(LocalImageService.PortableRoot,
            "python_embedded","python.exe")
    }.FirstOrDefault(File.Exists) ?? "";

    private static void RequireSuccess(
        (int ExitCode,string StdOut,string StdErr) result, string stage)
    {
        if(result.ExitCode==0) return;
        var detail=(result.StdErr + Environment.NewLine + result.StdOut).Trim();
        if(detail.Length>2300) detail=detail[^2300..];
        throw new InvalidOperationException(stage+" failed. "+
            "Existing models and projects were not removed. "+detail);
    }

    public async Task InstallAndVerifyAsync(
        Action<string>? log, CancellationToken ct)
    {
        var python=PythonExecutable;
        var comfy=Path.Combine(LocalImageService.PortableRoot,"ComfyUI");
        if(!File.Exists(python)||!File.Exists(Path.Combine(comfy,"main.py")))
            throw new FileNotFoundException(
                "Install ZeroVideo Local ComfyUI first. PuLID uses its " +
                "bundled Python, not system Python.");

        var probe=await runner.RunAsync(python,
            "-s -c \"import sys,torch,numpy; print(sys.version); "+
            "print(torch.__version__); print(numpy.__version__)\"",
            LocalImageService.PortableRoot,ct);
        RequireSuccess(probe,"ComfyUI Python detection");
        log?.Invoke("Using existing ComfyUI Python and PyTorch: "+
            probe.StdOut.Trim().Replace("\r","").Replace("\n"," | "));

        var installed=File.Exists(Path.Combine(Folder,"pulidflux.py"));
        var staged="";
        if(!installed)
        {
            if(Directory.Exists(Folder))
                throw new IOException(
                    "PuLID custom-node folder exists but is incomplete. " +
                    "ZeroVideo will not overwrite it. Inspect: "+Folder);

            var parent=Path.GetDirectoryName(Folder)!;
            Directory.CreateDirectory(parent);
            var archive=Path.Combine(AppPaths.ManagedToolsDirectory,
                "PuLID-FLUX-"+Commit[..12]+".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            if(!File.Exists(archive))
            {
                log?.Invoke("Downloading pinned official PuLID-FLUX extension " +
                    "source from GitHub...");
                var partial=archive+".partial";
                using var http=new HttpClient
                {
                    Timeout=TimeSpan.FromMinutes(10)
                };
                using var response=await http.GetAsync(ArchiveUrl,
                    HttpCompletionOption.ResponseHeadersRead,ct);
                response.EnsureSuccessStatusCode();
                await using(var input=await response.Content.ReadAsStreamAsync(ct))
                await using(var output=File.Create(partial))
                    await input.CopyToAsync(output,ct);
                File.Move(partial,archive,overwrite:true);
            }

            staged=Path.Combine(parent,
                ".zerovideo-pulid-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staged);
            try
            {
                using var zip=ZipFile.OpenRead(archive);
                var prefix=Repo+"-"+Commit+"/";
                var root=Path.GetFullPath(staged)+
                    Path.DirectorySeparatorChar;
                foreach(var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name=entry.FullName.Replace('\\','/');
                    if(name==prefix) continue;
                    if(!name.StartsWith(prefix,StringComparison.Ordinal))
                        throw new InvalidDataException(
                            "Unexpected PuLID extension ZIP root.");
                    var relative=name[prefix.Length..];
                    if(relative.Length==0)continue;
                    var parts=relative.Split('/',
                        StringSplitOptions.RemoveEmptyEntries);
                    if(relative.StartsWith('/')||
                        parts.Any(x=>x is "." or ".." || x.Contains(':')))
                        throw new InvalidDataException(
                            "Unsafe PuLID ZIP entry.");
                    var unixType=(entry.ExternalAttributes>>16)&0xF000;
                    if(unixType==0xA000)
                        throw new InvalidDataException(
                            "Symlink entry is not allowed.");
                    var dest=Path.GetFullPath(
                        Path.Combine(staged,Path.Combine(parts)));
                    if(!dest.StartsWith(root,
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            "PuLID ZIP entry escaped extraction folder.");
                    if(name.EndsWith('/'))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest,overwrite:false);
                }
                if(!File.Exists(Path.Combine(staged,"pulidflux.py"))||
                   !File.Exists(Path.Combine(staged,"__init__.py")))
                    throw new InvalidDataException(
                        "PuLID archive is missing custom-node files.");

                await EnsurePythonDependenciesAsync(python,log,ct);
                // Put the extension into the active custom-nodes folder only
                // after dependencies finish. Never touch other custom nodes.
                Directory.Move(staged,Folder);
                staged="";
            }
            finally
            {
                if(staged.Length>0&&Directory.Exists(staged))
                {
                    try{Directory.Delete(staged,recursive:true);}
                    catch(IOException){}
                    catch(UnauthorizedAccessException){}
                }
            }
        }
        else
        {
            log?.Invoke("Reusing the existing PuLID-FLUX extension files.");
            await EnsurePythonDependenciesAsync(python,log,ct);
        }

        // Check packages before spending bandwidth on >2GB of weights.
        await VerifyPythonImportsAsync(python,log,ct);
        log?.Invoke("Fetching official SHA-256 verified PuLID and EVA-CLIP " +
            "identity weights (~2GB; cached when already installed)...");
        await localImages.InstallPulidIdentityAssetsAsync(log,ct);
        log?.Invoke("PuLID source, Python imports and identity weights ready. " +
            "Restart ComfyUI to register nodes. Models are opt-in, and " +
            "identity synthesis on FLUX.1-schnell Q4/8GB is experimental.");
    }

    private async Task EnsurePythonDependenciesAsync(
        string python,Action<string>? log,CancellationToken ct)
    {
        // InsightFace 2.1 is a pure-Python wheel, but its import eagerly
        // requires ONNX plus image/science dependencies. A previous installer
        // used --no-deps for insightface without installing those packages.
        // Repair them explicitly without upgrading the existing PyTorch or
        // touching any image model weights. FaceNet is selected in the
        // identity graph, NOT pretrained InsightFace models.
        // --only-binary avoids silently compiling native extensions.
        var steps=new[]
        {
            ("Core PuLID packages",
                "-s -m pip install --disable-pip-version-check "+
                "--only-binary=:all: --upgrade-strategy only-if-needed "+
                "\"ftfy\" \"timm\" \"facexlib\" \"cython\""),
            ("InsightFace dependency repair (ONNX, science, image toolkit)",
                "-s -m pip install --disable-pip-version-check "+
                "--only-binary=:all: --upgrade-strategy only-if-needed "+
                "\"onnx>=1.17,<2\" \"scipy\" \"scikit-image\" "+
                "\"scikit-learn\" \"requests\" \"tqdm\" "+
                "\"onnxruntime-gpu\""),
            ("Current official InsightFace Python module (no model download)",
                "-s -m pip install --disable-pip-version-check "+
                "--only-binary=:all: --no-deps \"insightface==2.1\""),
            ("FaceNet for identity embedding (preserve installed PyTorch)",
                "-s -m pip install --disable-pip-version-check "+
                "--only-binary=:all: --no-deps \"facenet-pytorch\"")
        };
        foreach(var(stage,command) in steps)
        {
            ct.ThrowIfCancellationRequested();
            log?.Invoke("PuLID setup: "+stage+"...");
            var result=await runner.RunAsync(python,command,
                LocalImageService.PortableRoot,ct);
            RequireSuccess(result,stage);
        }
    }

    private async Task VerifyPythonImportsAsync(
        string python,Action<string>? log,CancellationToken ct)
    {
        var check=await runner.RunAsync(python,
            "-s -c \"import torch,torchvision,cv2,numpy,onnx,onnxruntime,"+
            "scipy,skimage,sklearn,requests,tqdm,insightface,facexlib,"+
            "facenet_pytorch,ftfy,timm; "+
            "from insightface.utils import face_align; "+
            "from insightface.utils.download import download_file; "+
            "from insightface.utils.storage import BASE_REPO_URL; "+
            "print('PuLID dependencies imported successfully')\"",
            LocalImageService.PortableRoot,ct);
        RequireSuccess(check,"PuLID FaceNet dependency import validation");
        log?.Invoke("PuLID Python imports verified. "+
            "ComfyUI must restart to load newly installed nodes.");
    }
}
