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

                // Prepare dependencies in the EXISTING ComfyUI Python; never
                // create a second Python and never touch FLUX/SDXL weights.
                log?.Invoke("Installing LivePortrait requirements into " +
                    "existing ComfyUI Python. This may update ComfyUI packages.");
                var result = await runner.RunAsync(EmbeddedPython,
                    "-m pip install --disable-pip-version-check -r \"" +
                    Path.Combine(staging, "requirements.txt") + "\" " +
                    "\"mediapipe==0.10.21\"",
                    LocalImageService.PortableRoot, ct);
                if (result.ExitCode != 0)
                    throw new IOException(
                        "LivePortrait Python dependency installation failed: " +
                        (result.StdErr.Length > 1000
                            ? result.StdErr[^1000..] : result.StdErr));
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

        EnsureDriver();
        log?.Invoke("LivePortrait and ZeroVideo idle driver installed. " +
            "Restart ComfyUI, then test connection. Model weights will " +
            "download locally the first time LivePortrait runs.");
    }
}
