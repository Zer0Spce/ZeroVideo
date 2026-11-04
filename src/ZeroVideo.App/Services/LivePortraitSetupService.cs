using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Installs only the Kijai LivePortrait ComfyUI custom nodes and ZeroVideo's
/// small procedural idle driver, reusing the existing ComfyUI Python/server.
/// All installs are explicit; no files from the old AI video engines are
/// automatically deleted or overwritten.
/// </summary>
public sealed class LivePortraitSetupService(ProcessRunner runner)
{
    private const string SourceCommit =
        "4d9dc6205b793ffd0fb319816136d9b8c0dbfdff";
    private const string ArchiveUrl =
        "https://github.com/kijai/ComfyUI-LivePortraitKJ/archive/" +
        SourceCommit + ".zip";
    // Official LivePortrait ONNX landmark model. The upstream extension
    // downloads checkpoints only when its model FOLDER is missing, so an
    // incomplete folder can leave this file absent indefinitely.
    private const string LandmarkUrl =
        "https://huggingface.co/Kijai/LivePortrait_safetensors/resolve/" +
        "45213bbb94ead0c64a94bcfabb0688dc087d89c9/landmark.onnx";
    private const long LandmarkModelBytes = 114666491;
    private const string LandmarkSha256 =
        "31d22a5041326c31f19b78886939a634a5aedcaa5ab8b9b951a1167595d147db";
    private static readonly SemaphoreSlim LandmarkDownloadGate = new(1, 1);
    public static string LandmarkModelPath => Path.Combine(
        LocalImageService.PortableRoot, "ComfyUI", "models", "liveportrait",
        "landmark.onnx");

    public static string NodeFolder => Path.Combine(
        LocalImageService.PortableRoot, "ComfyUI", "custom_nodes",
        "ComfyUI-LivePortraitKJ");
    public static string DriverFolder => Path.Combine(
        LocalImageService.PortableRoot, "ComfyUI", "custom_nodes",
        "ZeroVideo-IdleDriver");
    public static string BundledPythonExecutable => EmbeddedPython;
    private static string EmbeddedPython =>
        new[]
        {
            Path.Combine(LocalImageService.PortableRoot, "python_embeded", "python.exe"),
            Path.Combine(LocalImageService.PortableRoot, "python_embedded", "python.exe")
        }.FirstOrDefault(File.Exists) ?? "";

    public static bool IsInstalled =>
        File.Exists(Path.Combine(NodeFolder, "nodes.py")) &&
        File.Exists(Path.Combine(DriverFolder, "idle_driver.py")) &&
        File.Exists(Path.Combine(DriverFolder, "__init__.py"));

    private static void CopyResource(string resource, string destination)
    {
        using var source = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("ZeroVideo.App.Resources." + resource)
            ?? throw new FileNotFoundException(
                "LivePortrait driver resource missing: " + resource);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tmp = destination + ".partial";
        using (var output = File.Create(tmp)) source.CopyTo(output);
        File.Move(tmp, destination, overwrite: true);
    }

    private static void EnsureDriver()
    {
        CopyResource("zerovideo_idle_driver.py",
            Path.Combine(DriverFolder, "idle_driver.py"));
        CopyResource("zerovideo_idle_driver_init.py",
            Path.Combine(DriverFolder, "__init__.py"));
    }

    public async Task InstallAsync(Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(EmbeddedPython))
            throw new FileNotFoundException(
                "ComfyUI portable Python was not found. Install Local ComfyUI first.");
        if (File.Exists(Path.Combine(NodeFolder, "nodes.py")))
        {
            log?.Invoke("Reusing existing LivePortrait custom nodes.");
        }
        else
        {
            var parent = Path.GetDirectoryName(NodeFolder)!;
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent,
                ".zerovideo-liveportrait-" + Guid.NewGuid().ToString("N"));
            var archive = Path.Combine(AppPaths.ManagedToolsDirectory,
                "liveportrait-kijai-" + SourceCommit[..12] + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);

            if (!File.Exists(archive))
            {
                log?.Invoke("Downloading pinned LivePortrait ComfyUI extension " +
                    "from its official GitHub source...");
                using var http = new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(8)
                };
                using var response = await http.GetAsync(ArchiveUrl,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var partial = archive + ".partial";
                await using (var stream = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = File.Create(partial))
                    await stream.CopyToAsync(output, ct);
                File.Move(partial, archive, overwrite: true);
            }

            var expectedRoot = "ComfyUI-LivePortraitKJ-" + SourceCommit + "/";
            try
            {
                Directory.CreateDirectory(staging);
                using var file = ZipFile.OpenRead(archive);
                foreach (var entry in file.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('\\', '/');
                    if (name == expectedRoot) continue;
                    if (!name.StartsWith(expectedRoot,
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            "Unexpected LivePortrait archive root.");
                    var relative = name[expectedRoot.Length..];
                    if (relative.Length == 0) continue;
                    var segments = relative.Split('/',
                        StringSplitOptions.RemoveEmptyEntries);
                    if (relative.StartsWith('/') ||
                        segments.Any(s => s is "." or ".." || s.Contains(':')))
                        throw new InvalidDataException(
                            "Unsafe LivePortrait archive entry.");
                    // Reject symlinks; never allow extraction outside staging.
                    var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (unixType == 0xA000)
                        throw new InvalidDataException(
                            "Unexpected symlink in LivePortrait archive.");
                    var dest = Path.GetFullPath(
                        Path.Combine(staging, Path.Combine(segments)));
                    var stagingRoot = Path.GetFullPath(staging) +
                        Path.DirectorySeparatorChar;
                    if (!dest.StartsWith(stagingRoot,
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            "LivePortrait archive path escaped extraction root.");
                    if (name.EndsWith('/'))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: false);
                }
                if (!File.Exists(Path.Combine(staging, "nodes.py")) ||
                    !File.Exists(Path.Combine(staging, "requirements.txt")))
                    throw new InvalidDataException(
                        "LivePortrait source missing its nodes/requirements.");

                ct.ThrowIfCancellationRequested();
                if (Directory.Exists(NodeFolder))
                    throw new IOException(
                        "Existing LivePortrait folder is incomplete. " +
                        "No existing files were removed; inspect: " + NodeFolder);
                Directory.Move(staging, NodeFolder);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    try { Directory.Delete(staging, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        // Always run compatibility repair even when the extension folder
        // exists from an earlier/partially completed installation.
        await InstallCompatibleDependenciesAsync(log, ct);
        await EnsureLandmarkModelAsync(log, ct);
        EnsureDriver();
        await EnsureMediaPipeTasksCompatibilityAsync(log, ct);
        // A successful pip install is insufficient: ComfyUI must actually
        // import and register the pinned Kijai extension and ZeroVideo node.
        var probe = await DiagnoseImportsAsync(ct);
        log?.Invoke(probe);
        log?.Invoke("LivePortrait and ZeroVideo idle driver installed. " +
            "Restart ComfyUI, then test connection. Model weights will " +
            "download locally the first time LivePortrait runs.");
    }
    /// <summary>
    /// Repair only the missing/corrupt ONNX landmark checkpoint. The pinned
    /// official 114,666,491-byte file is SHA256 verified before activation.
    /// Existing successful images, other LivePortrait models and ComfyUI
    /// dependencies are not modified. Runs only on explicit Install/Repair
    /// or when LivePortrait is selected for a scene with a detected face.
    /// </summary>
    public async Task<string> EnsureLandmarkModelAsync(
        Action<string>? log, CancellationToken ct)
    {
        await LandmarkDownloadGate.WaitAsync(ct);
        try
        {
            var destination = LandmarkModelPath;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            async Task<bool> ValidAsync(string path)
            {
                if (!File.Exists(path) ||
                    new FileInfo(path).Length != LandmarkModelBytes)
                    return false;
                await using var stream = File.OpenRead(path);
                var actual = await System.Security.Cryptography.SHA256
                    .HashDataAsync(stream, ct);
                return Convert.ToHexString(actual).Equals(
                    LandmarkSha256, StringComparison.OrdinalIgnoreCase);
            }

            if (await ValidAsync(destination))
            {
                log?.Invoke("LivePortrait landmark.onnx verified (115 MB, SHA-256).");
                return "LivePortrait landmark.onnx verified; no download needed.";
            }

            log?.Invoke("Repairing LivePortrait landmark.onnx from the official " +
                "Kijai model repository (~115 MB). Only this file will be replaced.");
            var partial = destination + ".zerovideo.partial";
            var existing = File.Exists(partial) ?
                new FileInfo(partial).Length : 0L;
            if (existing >= LandmarkModelBytes)
            {
                if (await ValidAsync(partial))
                {
                    File.Move(partial, destination, overwrite: true);
                    log?.Invoke("LivePortrait landmark.onnx restored from verified download.");
                    return "Landmark model ready (115 MB, verified).";
                }
                File.Delete(partial);
                existing = 0;
            }

            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(20)
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, LandmarkUrl);
            if (existing > 0)
                request.Headers.Range =
                    new System.Net.Http.Headers.RangeHeaderValue(existing, null);
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var resumes = existing > 0 &&
                response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (resumes && response.Content.Headers.ContentRange?.From != existing)
                throw new InvalidDataException(
                    "The model server returned an invalid download offset. " +
                    "Retry the Repair button.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(partial,
                resumes ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 131072, useAsync: true))
                await input.CopyToAsync(file, ct);

            if (!await ValidAsync(partial))
                throw new InvalidDataException(
                    "LivePortrait landmark.onnx download is incomplete or " +
                    "failed the official SHA-256 check. The current model has " +
                    "not been replaced. Click Repair to retry.");

            // Atomic replacement only after full model checksum verification.
            File.Move(partial, destination, overwrite: true);
            log?.Invoke("LivePortrait landmark.onnx repaired and verified " +
                "(114,666,491 bytes, SHA-256 matches publisher).");
            return "LivePortrait landmark.onnx is ready (115 MB, verified).";
        }
        finally
        {
            LandmarkDownloadGate.Release();
        }
    }

    private static readonly SemaphoreSlim MediaPipeRepairGate = new(1,1);
    private static string _mediaPipeTasksValidatedStamp = "";

    /// <summary>
    /// The pinned Kijai LivePortrait extension imports a private 2023
    /// MediaPipe face_landmark.py incompatible with modern Python 3.13
    /// wheels. Patch ONLY mp_utils.py to use public Tasks APIs, keeping
    /// the original source as a namespaced local backup. No pip/downgrade.
    /// Called during Install and automatically during LivePortrait test.
    /// </summary>
    public async Task<string> EnsureMediaPipeTasksCompatibilityAsync(
        Action<string>? log, CancellationToken ct)
    {
        await MediaPipeRepairGate.WaitAsync(ct);
        try
        {
            var file = Path.Combine(NodeFolder,"media_pipe","mp_utils.py");
            if (!File.Exists(file))
                throw new FileNotFoundException(
                    "LivePortrait MediaPipe adapter not installed. " +
                    "Use Install LivePortrait to restore the official nodes.",file);
            var metadata = new FileInfo(file);
            var stamp = file + ":" + metadata.Length + ":" +
                metadata.LastWriteTimeUtc.Ticks;
            if (stamp == _mediaPipeTasksValidatedStamp)
                return "LivePortrait public MediaPipe Tasks compatibility already verified.";

            if (string.IsNullOrWhiteSpace(EmbeddedPython))
                throw new FileNotFoundException(
                    "Existing ComfyUI Python interpreter was not found.");
            var script = Path.Combine(AppPaths.ManagedToolsDirectory,
                "liveportrait_mediapipe_compat.py");
            CopyResource("liveportrait_mediapipe_compat.py",script);
            log?.Invoke("LivePortrait: checking Kijai media_pipe/mp_utils.py " +
                "compatibility with public MediaPipe Tasks (Python 3.13)...");
            using var limit=CancellationTokenSource
                .CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(120));
            (int ExitCode,string StdOut,string StdErr) result;
            try
            {
                result=await runner.RunAsync(EmbeddedPython,
                    "-X utf8 -s -u \"" + script +
                    "\" --extension \"" + NodeFolder + "\"",
                    LocalImageService.PortableRoot,limit.Token);
            }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "LivePortrait MediaPipe Tasks import check exceeded 120 seconds; " +
                    "no ComfyUI image models or checkpoints were modified.");
            }
            if (result.ExitCode != 0)
            {
                var details=(result.StdErr + Environment.NewLine +
                    result.StdOut).Trim();
                var first=details.Split(new[] {'\r','\n'},
                    StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line=>line.Contains(
                        "LIVEPORTRAIT_MEDIAPIPE_COMPAT_ERROR:",
                        StringComparison.Ordinal))
                    ?? "Unknown MediaPipe import or source compatibility failure";
                if(details.Length>900) details=details[^900..];
                throw new InvalidOperationException(
                    "LivePortrait MediaPipe Tasks repair failed: "+first+
                    " | Python detail: "+details);
            }
            var status=result.StdOut.Trim();
            log?.Invoke(status);
            metadata.Refresh();
            _mediaPipeTasksValidatedStamp=file+":"+metadata.Length+
                ":"+metadata.LastWriteTimeUtc.Ticks;
            return status;
        }
        finally
        {
            MediaPipeRepairGate.Release();
        }
    }

    private async Task InstallCompatibleDependenciesAsync(
        Action<string>? log, CancellationToken ct)
    {
        // The official 2026 ComfyUI NVIDIA portable ships with Python 3.13.
        // MediaPipe 0.10.21 has Windows wheels only through Python 3.12;
        // upstream LivePortrait also pins numpy<=1.26.4, which cannot be
        // installed on Python 3.13. Probe before changing *any* packages.
        var probe = await runner.RunAsync(EmbeddedPython,
            "-s -c \"import sys,numpy; " +
            "print(str(sys.version_info.major)+'.'+str(sys.version_info.minor)); " +
            "print(numpy.__version__)\"",
            LocalImageService.PortableRoot, ct);
        if (probe.ExitCode != 0)
            throw new IOException(
                "Could not read existing ComfyUI Python/NumPy versions. " +
                "No packages were changed. " + probe.StdErr);
        var lines = probe.StdOut
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        if (lines.Length < 2 ||
            !Version.TryParse(lines[^2], out var pythonVersion) ||
            !Version.TryParse(lines[^1], out var numpyVersion))
            throw new InvalidDataException(
                "Unexpected ComfyUI Python/NumPy version output. " +
                "No dependencies were installed: " + probe.StdOut);
        if (pythonVersion.Major != 3 || pythonVersion.Minor < 10 ||
            pythonVersion.Minor > 13)
            throw new NotSupportedException(
                "LivePortrait tested compatibility requires Python 3.10–3.13. " +
                "Detected " + pythonVersion + ". No packages were modified.");

        var modern = pythonVersion.Minor >= 13 || numpyVersion.Major >= 2;
        // For Python 3.13, use wheels that actually exist. Keep NumPy 2.x
        // rather than allowing older LivePortrait requirements to downgrade
        // (and thereby break FLUX/SDXL/ComfyUI).
        var mediaPipe = modern
            ? "mediapipe==0.10.35"
            : "mediapipe==0.10.21";
        var numpy = modern ? "numpy>=2,<3" : "numpy>=1.26,<2";
        log?.Invoke("LivePortrait dependency check: Python " +
            pythonVersion + ", NumPy " + numpyVersion +
            ", selecting " + mediaPipe + ". Original ComfyUI preserved.");

        var result = await runner.RunAsync(EmbeddedPython,
            "-s -m pip install --disable-pip-version-check " +
            "--only-binary=:all: --upgrade-strategy only-if-needed " +
            "\"pyyaml\" \"opencv-python\" \"onnxruntime-gpu\" " +
            "\"pykalman\" \"onnx2torch\" \"kornia\" \"safetensors\" " +
            "\"huggingface_hub\" \"tqdm\" \"" + mediaPipe +
            "\" \"" + numpy + "\"",
            LocalImageService.PortableRoot, ct);
        if (result.ExitCode != 0)
            throw new IOException(
                "Compatible LivePortrait Python package install failed. " +
                "No model checkpoints were deleted. " +
                (result.StdErr.Length > 1400 ?
                    result.StdErr[^1400..] : result.StdErr));

        // The new LivePortrait driver and the official Kijai extension both
        // use the MediaPipe Tasks API; testing this avoids a false "success"
        // where the installed wheel lacks the required API surface.
        var verification = await runner.RunAsync(EmbeddedPython,
            "-s -c \"import mediapipe as mp; " +
            "from mediapipe.tasks import python; " +
            "from mediapipe.tasks.python import vision; " +
            "import cv2,numpy,onnxruntime,pykalman,onnx2torch,kornia; " +
            "print('LivePortrait dependencies verified')\"",
            LocalImageService.PortableRoot, ct);
        if (verification.ExitCode != 0)
            throw new IOException(
                "LivePortrait packages installed but import validation failed. " +
                "ComfyUI image models are unchanged. " +
                (verification.StdErr.Length > 1400 ?
                    verification.StdErr[^1400..] : verification.StdErr));
        log?.Invoke("LivePortrait dependency imports verified in ComfyUI Python. " +
            "Restart ComfyUI before running its face nodes.");
    }

    /// <summary>
    /// Imports the actual extensions using the installed ComfyUI Python
    /// without generating frames or changing checkpoints. Unlike a generic
    /// node-not-found error this returns the real missing module/traceback.
    /// </summary>
    public async Task<string> DiagnoseImportsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(EmbeddedPython))
            throw new FileNotFoundException("ComfyUI's bundled Python is missing.");
        var file = Path.Combine(AppPaths.ManagedToolsDirectory,
            "liveportrait_import_probe.py");
        CopyResource("liveportrait_import_probe.py", file);
        var root = Path.Combine(LocalImageService.PortableRoot, "ComfyUI");
        var args = "-X utf8 -s -u \"" + file +
            "\" --comfy-root \"" + root +
            "\" --liveportrait-root \"" + NodeFolder +
            "\" --driver-root \"" + DriverFolder + "\"";
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(120));
        var result = await runner.RunAsync(
            EmbeddedPython, args, LocalImageService.PortableRoot, limit.Token);
        if (result.ExitCode != 0)
        {
            var error = (result.StdErr + Environment.NewLine + result.StdOut).Trim();
            if (error.Length > 3500) error = error[^3500..];
            throw new InvalidOperationException(
                "LivePortrait extension failed to import into ComfyUI. " +
                "Actual Python traceback: " + error);
        }
        return "LivePortrait Python imports verified: " +
            result.StdOut.Trim().Replace("\r", "").Replace("\n", " | ");
    }

}
