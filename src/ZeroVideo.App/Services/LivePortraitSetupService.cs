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

    public static string NodeFolder => Path.Combine(
        LocalImageService.PortableRoot, "ComfyUI", "custom_nodes",
        "ComfyUI-LivePortraitKJ");
    public static string DriverFolder => Path.Combine(
        LocalImageService.PortableRoot, "ComfyUI", "custom_nodes",
        "ZeroVideo-IdleDriver");
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
        EnsureDriver();
        log?.Invoke("LivePortrait and ZeroVideo idle driver installed. " +
            "Restart ComfyUI, then test connection. Model weights will " +
            "download locally the first time LivePortrait runs.");
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
            "\"pykalman\" \"onnx2torch\" \"" + mediaPipe +
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
            "import cv2,numpy,onnxruntime,pykalman,onnx2torch; " +
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

}
