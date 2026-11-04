using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Optional official Windows FramePack installation and owned local server.
/// Models are NOT downloaded during installation; FramePack fetches its
/// required weights on first start. Existing user installations are preserved.
/// </summary>
public sealed class FramePackSetupService(ProcessRunner runner)
{
    private const string PackageUrl =
        "https://github.com/lllyasviel/FramePack/releases/download/windows/framepack_cu126_torch26.7z";
    private const long ExpectedArchiveBytes = 1811224113L;
    private const string ExtractorUrl =
        "https://github.com/ip7z/7zip/releases/download/26.05/7zr.exe";
    private const string ExtractorHash =
        "9bf582b894dfa665f852d7e9112b02290a1c303c2d95a21fcdc41ffcb03143b7";

    public static string InstallRoot =>
        Path.Combine(AppPaths.ManagedToolsDirectory, "FramePack");
    public static string PackageRoot => Path.Combine(InstallRoot, "app");
    public static string ServerLogPath => Path.Combine(InstallRoot, "server.log");
    private static string ArchivePath =>
        Path.Combine(InstallRoot, "downloads", "framepack_cu126_torch26.7z");
    private static string ExtractorPath =>
        Path.Combine(InstallRoot, "downloads", "7zr.exe");

    private static readonly object ServerLock = new();
    private static readonly object LogLock = new();
    private static Process? _ownedServer;

    public sealed record Installation(string PythonExe, string AppDirectory);

    public static Installation? FindExisting(ZeroVideoSettings settings)
    {
        var configured = FindFromPython(settings.FramePackPythonPath);
        return configured ?? FindInstallation(PackageRoot)
            ?? FindInstallation(Path.Combine(AppPaths.DataDirectory, "Framepack"));
    }

    private static Installation? FindFromPython(string? pythonExe)
    {
        if (string.IsNullOrWhiteSpace(pythonExe) || !File.Exists(pythonExe))
            return null;
        var directory = new DirectoryInfo(Path.GetDirectoryName(pythonExe)!);
        for (var i = 0; i < 5 && directory is not null; i++, directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, "demo_gradio.py");
            if (File.Exists(direct))
                return new Installation(Path.GetFullPath(pythonExe), directory.FullName);
            var nested = Path.Combine(directory.FullName, "FramePack", "demo_gradio.py");
            if (File.Exists(nested))
                return new Installation(Path.GetFullPath(pythonExe),
                    Path.GetDirectoryName(nested)!);
        }
        return null;
    }

    private static Installation? FindInstallation(string root)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            // The official one-click archive uses system/python/python.exe,
            // with demo_gradio.py inside its FramePack application folder.
            var python = Directory.EnumerateFiles(root, "python.exe",
                SearchOption.AllDirectories)
                .FirstOrDefault(x => x.Contains(
                    Path.DirectorySeparatorChar + "system" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            var script = Directory.EnumerateFiles(root, "demo_gradio.py",
                SearchOption.AllDirectories).FirstOrDefault();
            return python is null || script is null ? null :
                new Installation(Path.GetFullPath(python),
                    Path.GetDirectoryName(Path.GetFullPath(script))!);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task<Installation> InstallAsync(ZeroVideoSettings settings,
        Action<string>? report, CancellationToken ct)
    {
        if (FindExisting(settings) is { } existing)
        {
            report?.Invoke("FramePack already installed: " + existing.AppDirectory);
            return existing;
        }
        AppPaths.Ensure();
        Directory.CreateDirectory(Path.GetDirectoryName(ArchivePath)!);
        var drive = new DriveInfo(Path.GetPathRoot(InstallRoot)!);
        if (drive.AvailableFreeSpace < 12L * 1024 * 1024 * 1024)
            throw new IOException("FramePack installation needs at least 12 GB free " +
                "for the archive and extracted app. The first model load requires " +
                "more than 30 GB additional free storage.");
        if (drive.AvailableFreeSpace < 48L * 1024 * 1024 * 1024)
            report?.Invoke("Storage warning: first FramePack model launch " +
                "requires over 30 GB additional free disk space.");

        report?.Invoke("Downloading OFFICIAL FramePack Windows package (1.81 GB). " +
            "This download resumes after interruption. No model weights yet.");
        await DownloadAsync(PackageUrl, ArchivePath, ExpectedArchiveBytes, report, ct);
        var extractor = await GetExtractorAsync(report, ct);

        report?.Invoke("Testing official .7z archive integrity...");
        var tested = await runner.RunAsync(extractor,
            "t -bd -y \"" + ArchivePath + "\"", InstallRoot, ct);
        if (tested.ExitCode != 0)
            throw new InvalidDataException("FramePack archive integrity check failed. " +
                "The downloaded file will be retained for inspection.");

        // Check paths explicitly before running 7-Zip's extractor.
        var listed = await runner.RunAsync(extractor,
            "l -slt \"" + ArchivePath + "\"", InstallRoot, ct);
        if (listed.ExitCode != 0)
            throw new InvalidDataException("Could not inspect official archive entries.");
        var detail = listed.StdOut.Replace("\r\n", "\n");
        var separator = detail.IndexOf("\n----------\n", StringComparison.Ordinal);
        if (separator < 0)
            throw new InvalidDataException("Unexpected 7-Zip archive listing format.");
        foreach (var line in detail[(separator + 12)..].Split('\n'))
        {
            if (!line.StartsWith("Path = ", StringComparison.Ordinal)) continue;
            var entry = line[7..].Trim();
            if (Path.IsPathRooted(entry) || entry.Contains(':') ||
                entry.Replace('\\', '/').Split('/').Contains(".."))
                throw new InvalidDataException("The FramePack archive has an unsafe path.");
        }

        if (Directory.Exists(PackageRoot))
            throw new IOException("The FramePack install folder already exists but " +
                "does not contain a valid package. No existing files were deleted: " +
                PackageRoot);

        var staging = Path.Combine(InstallRoot, ".extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            report?.Invoke("Extracting FramePack to a separate staging folder...");
            var extracted = await runner.RunAsync(extractor,
                "x -bd -y -o\"" + staging + "\" \"" + ArchivePath + "\"",
                InstallRoot, ct);
            if (extracted.ExitCode != 0)
                throw new IOException("FramePack archive extraction failed: " +
                    (extracted.StdErr.Length > 1000
                        ? extracted.StdErr[^1000..] : extracted.StdErr));

            var installation = FindInstallation(staging);
            if (installation is null)
                throw new InvalidDataException("The extracted Windows package does not " +
                    "contain system/python/python.exe and FramePack/demo_gradio.py.");
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, PackageRoot);
            var moved = FindInstallation(PackageRoot)
                ?? throw new InvalidDataException("FramePack setup validation failed.");
            report?.Invoke("FramePack installed: " + moved.AppDirectory);
            report?.Invoke("The required model weights are not yet downloaded. " +
                "Click Start FramePack to initialize the models when ready.");
            return moved;
        }
        finally
        {
            // Only remove this call's private temporary extraction folder.
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<string> GetExtractorAsync(
        Action<string>? report, CancellationToken ct)
    {
        if (File.Exists(ExtractorPath) &&
            await HasExpectedHashAsync(ExtractorPath, ExtractorHash, ct))
            return ExtractorPath;
        report?.Invoke("Downloading verified official 7-Zip extractor (~0.6 MB)...");
        await DownloadAsync(ExtractorUrl, ExtractorPath, 602624, report, ct);
        if (!await HasExpectedHashAsync(ExtractorPath, ExtractorHash, ct))
        {
            try { File.Delete(ExtractorPath); } catch (IOException) { }
            throw new InvalidDataException(
                "7-Zip extractor hash mismatch. No executable was launched.");
        }
        return ExtractorPath;
    }

    private static async Task<bool> HasExpectedHashAsync(
        string path, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).Equals(expected,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadAsync(string url, string path,
        long expectedBytes, Action<string>? report, CancellationToken ct)
    {
        if (File.Exists(path) && new FileInfo(path).Length == expectedBytes)
            return;
        var part = path + ".partial";
        var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset > expectedBytes)
        {
            File.Delete(part);
            offset = 0;
        }
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var isResume = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (isResume && response.Content.Headers.ContentRange?.From != offset)
            throw new InvalidDataException("Unexpected download resume offset.");
        if (!isResume) offset = 0;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using (var destination = new FileStream(part,
            isResume ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read, 131072, useAsync: true))
        {
            var buffer = new byte[1024 * 1024];
            long written = offset;
            long lastReported = written;
            while (true)
            {
                var read = await source.ReadAsync(buffer, ct);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                if (written - lastReported >= 128L * 1024 * 1024)
                {
                    report?.Invoke("FramePack download: " +
                        (written * 100.0 / expectedBytes).ToString("0.0") + "%");
                    lastReported = written;
                }
            }
        }
        var size = new FileInfo(part).Length;
        if (size != expectedBytes)
            throw new InvalidDataException("Incomplete FramePack download: " +
                size + " of " + expectedBytes + " bytes. Click Install again to resume.");
        File.Move(part, path, overwrite: true);
    }

    public string StartServer(Installation installation, string serverUrl,
        Action<string>? report)
    {
        if (!File.Exists(installation.PythonExe) ||
            !File.Exists(Path.Combine(installation.AppDirectory, "demo_gradio.py")))
            throw new FileNotFoundException(
                "FramePack installation was not found. Install it first.");
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" ||
            (uri.Host != "127.0.0.1" && uri.Host != "localhost") ||
            uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                "Use a local FramePack URL such as http://127.0.0.1:7860.");

        lock (ServerLock)
        {
            if (_ownedServer is not null && !_ownedServer.HasExited)
                return "FramePack server is already running (ZeroVideo-owned).";
            _ownedServer?.Dispose();
            _ownedServer = null;
            Directory.CreateDirectory(InstallRoot);
            var info = new ProcessStartInfo
            {
                FileName = installation.PythonExe,
                WorkingDirectory = installation.AppDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] {
                "-X", "utf8", "-u", "demo_gradio.py",
                "--server", "127.0.0.1",
                "--port", uri.Port.ToString()
            })
                info.ArgumentList.Add(arg);
            info.Environment["PYTHONIOENCODING"] = "utf-8";
            info.Environment["PYTHONUTF8"] = "1";
            info.Environment["GRADIO_ANALYTICS_ENABLED"] = "False";
            var proc = new Process { StartInfo = info, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => WriteServerLog(e.Data);
            proc.ErrorDataReceived += (_, e) => WriteServerLog(e.Data);
            proc.Exited += (_, _) =>
                WriteServerLog("FramePack process exited with code " +
                    proc.ExitCode.ToString());
            if (!proc.Start())
            {
                proc.Dispose();
                throw new IOException("FramePack Python process could not start.");
            }
            _ownedServer = proc;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            report?.Invoke("FramePack launched locally (PID " + proc.Id + "). " +
                "First start downloads required models (>30 GB); check " +
                ServerLogPath + ". Test Connection once initialization finishes.");
            return "FramePack launched. Downloading/loading models if needed. " +
                "Test Connection after the server is ready.";
        }
    }

    private static void WriteServerLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(InstallRoot);
                File.AppendAllText(ServerLogPath,
                    "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + "\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void StopOwnedServer()
    {
        lock (ServerLock)
        {
            var owned = _ownedServer;
            if (owned is null) return;
            try { if (!owned.HasExited) owned.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                owned.Dispose();
                _ownedServer = null;
            }
        }
    }
}
