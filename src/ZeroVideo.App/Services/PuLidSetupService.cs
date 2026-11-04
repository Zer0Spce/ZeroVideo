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
        var concise=detail.Split(new[] {'\r','\n'},
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries)
            .LastOrDefault(line =>
                line.StartsWith("ModuleNotFoundError:",StringComparison.Ordinal) ||
                line.StartsWith("ImportError:",StringComparison.Ordinal) ||
                line.StartsWith("OSError:",StringComparison.Ordinal) ||
                line.StartsWith("RuntimeError:",StringComparison.Ordinal) ||
                line.Contains("DLL load failed",StringComparison.OrdinalIgnoreCase))
            ?? "See Python traceback below";
        if(detail.Length>2400) detail=detail[^2400..];
        throw new InvalidOperationException(stage+" failed: "+concise+
            ". Existing models and projects were not removed. "+
            "Full Python traceback tail: "+detail);
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

        // Fix the pinned extension's obsolete FLUX forward wrapper against
        // the current ComfyUI timestep_zero_index API, including an already
        // installed extension from previous ZeroVideo builds. This patches
        // just two verified source files with local backups, not models.
        await RepairComfyFluxCompatibilityAsync(python,comfy,log,ct);
        // Check the full custom-node import before spending bandwidth
        // on >2 GB of weights. A generic "import insightface" alone is
        // insufficient: the pinned PuLID extension eagerly loads many modules.
        await VerifyPythonImportsAsync(python,log,ct);
        await VerifyComfyUiPulidNodesAsync(python,comfy,log,ct);
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
                "\"scikit-learn\" \"requests\" \"tqdm\""),
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

        // onnxruntime and onnxruntime-gpu are separate pip distributions
        // exposing the same import. Installing both can overwrite shared
        // binaries and break an already healthy LivePortrait/ComfyUI.
        // Reuse whichever working ORT the user already has.
        var ortProbe=await runner.RunAsync(python,
            "-s -c \"import onnxruntime as ort; print(ort.__version__)\"",
            LocalImageService.PortableRoot,ct);
        if(ortProbe.ExitCode==0)
            log?.Invoke("PuLID setup: reusing the installed ONNX Runtime "+
                ortProbe.StdOut.Trim()+"; no provider replacement.");
        else
        {
            log?.Invoke("PuLID setup: ONNX Runtime missing; installing the "+
                "official NVIDIA-capable runtime for embedded Python.");
            var install=await runner.RunAsync(python,
                "-s -m pip install --disable-pip-version-check "+
                "--only-binary=:all: --upgrade-strategy only-if-needed "+
                "\"onnxruntime-gpu\"",
                LocalImageService.PortableRoot,ct);
            RequireSuccess(install,"ONNX Runtime dependency installation");
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
    private async Task VerifyComfyUiPulidNodesAsync(
        string python, string comfy, Action<string>? log, CancellationToken ct)
    {
        var script = Path.Combine(AppPaths.ManagedToolsDirectory,
            "pulid_import_probe.py");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        using (var embedded = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("ZeroVideo.App.Resources.pulid_import_probe.py"))
        {
            if (embedded is null)
                throw new FileNotFoundException(
                    "ZeroVideo's embedded PuLID node diagnostic is missing.");
            var temporary = script + ".partial";
            using (var output = File.Create(temporary))
                embedded.CopyTo(output);
            File.Move(temporary,script,overwrite:true);
        }

        log?.Invoke("PuLID setup: importing the COMPLETE ComfyUI PuLID " +
            "extension to verify FaceNet node registration...");
        var args = "-X utf8 -s -u \"" + script +
            "\" --comfy-root \"" + comfy +
            "\" --pulid-root \"" + Folder + "\"";
        var result = await runner.RunAsync(python,args,
            LocalImageService.PortableRoot,ct);
        RequireSuccess(result,
            "PuLID ComfyUI custom-node import validation");
        log?.Invoke(result.StdOut.Trim());
    }

    private static string FluxModelSource => Path.Combine(
        LocalImageService.PortableRoot, "ComfyUI", "comfy", "ldm",
        "flux", "model.py");

    /// <summary>
    /// Detect a pinned PuLID extension still using a 2024-era
    /// forward_orig monkeypatch. Current ComfyUI passes the keyword
    /// timestep_zero_index and requires its native forward call to remain
    /// active. This is a cheap check; no model weights are loaded.
    /// </summary>
    public static bool NeedsComfyFluxCompatibilityRepair()
    {
        var plugin = Path.Combine(Folder, "pulidflux.py");
        var hook = Path.Combine(Folder, "PulidFluxHook.py");
        if (!File.Exists(plugin) || !File.Exists(hook) ||
            !File.Exists(FluxModelSource))
            return false;
        var current = File.ReadAllText(FluxModelSource);
        return current.Contains("timestep_zero_index",
                StringComparison.Ordinal) &&
            (File.ReadAllText(plugin).Contains(
                "wrappers_name, pulid_outer_sample_wrappers_with_override)",
                StringComparison.Ordinal) ||
             File.ReadAllText(hook).Contains(
                "transformer_options = extra_options[\"transformer_options\"]",
                StringComparison.Ordinal));
    }

    private async Task RepairComfyFluxCompatibilityAsync(
        string python, string comfy, Action<string>? log, CancellationToken ct)
    {
        var script = Path.Combine(AppPaths.ManagedToolsDirectory,
            "pulid_flux_compat.py");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        using (var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("ZeroVideo.App.Resources.pulid_flux_compat.py"))
        {
            if (stream is null)
                throw new FileNotFoundException(
                    "ZeroVideo's embedded PuLID FLUX compatibility patch is missing.");
            var partial = script + ".partial";
            using (var target = File.Create(partial))
                stream.CopyTo(target);
            File.Move(partial, script, overwrite:true);
        }
        log?.Invoke("PuLID setup: checking FLUX forward_orig compatibility " +
            "with the bundled ComfyUI before running any identity inference...");
        var args = "-X utf8 -s -u \"" + script +
            "\" --extension \"" + Folder +
            "\" --flux-model \"" + FluxModelSource + "\"";
        var result = await runner.RunAsync(python,args,
            LocalImageService.PortableRoot,ct);
        RequireSuccess(result, "PuLID forward_orig compatibility repair");
        log?.Invoke(result.StdOut.Trim());
    }

}
