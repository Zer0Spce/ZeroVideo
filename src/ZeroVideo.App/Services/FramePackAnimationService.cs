using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Optional offline integration with an already running, user-installed
/// official FramePack Gradio application (demo_gradio.py).
/// No models, samples or user images are uploaded to any cloud service.
/// </summary>
public sealed class FramePackAnimationService(ProcessRunner runner)
{
    private static string Root =>
        Path.Combine(AppPaths.DataDirectory, "LocalVideo", "FramePack");

    private static string WorkerPath => Path.Combine(Root, "framepack_bridge.py");

    private static void WriteWorker()
    {
        Directory.CreateDirectory(Root);
        using var stream = typeof(FramePackAnimationService).Assembly
            .GetManifestResourceStream("ZeroVideo.App.Resources.framepack_bridge.py");
        if (stream is null)
            throw new FileNotFoundException("Embedded FramePack adapter is missing from this build.");
        var temporary = WorkerPath + ".partial";
        using (var output = File.Create(temporary))
            stream.CopyTo(output);
        File.Move(temporary, WorkerPath, true);
    }

    private static bool IsSafeLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host == "127.0.0.1" || uri.Host == "[::1]")
        && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public async Task<string?> TryAnimateAsync(
        string original, VideoProject project, ProjectScene scene,
        ZeroVideoSettings settings, Action<string>? log, CancellationToken ct)
    {
        if (settings.AiImageAnimationEngine != "FramePack (Local)")
            return null;
        ct.ThrowIfCancellationRequested();

        try
        {
            if (!File.Exists(original))
                throw new FileNotFoundException("Scene image is missing.", original);
            var python = settings.FramePackPythonPath?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(python) || !File.Exists(python))
                throw new InvalidOperationException(
                    "Set FramePack Python EXE in Settings. Use the Python inside the official FramePack Windows install.");
            var server = settings.FramePackServerUrl?.Trim() ?? "";
            if (!IsSafeLoopback(server))
                throw new InvalidOperationException(
                    "FramePack must run on http://127.0.0.1:7860 (loopback only).");

            var seconds = Math.Clamp(settings.FramePackClipSeconds, 3.0, 5.0);
            var info = new FileInfo(original);
            // Cache is invalidated whenever the original image or motion settings change.
            var fingerprint = string.Join("|", original, info.Length,
                info.LastWriteTimeUtc.Ticks, seconds,
                scene.ShotDirection, server);
            var hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(fingerprint)))[..24].ToLowerInvariant();

            var clipDir = Path.Combine(project.ProjectDirectory, "animated");
            Directory.CreateDirectory(clipDir);
            var path = Path.Combine(clipDir, hash + ".mp4");
            if (File.Exists(path) && new FileInfo(path).Length > 8192)
            {
                log?.Invoke("FramePack: reusing previously rendered AI motion clip.");
                return path;
            }

            WriteWorker();
            var jobDir = Path.Combine(Root, "Jobs");
            Directory.CreateDirectory(jobDir);
            var jobPath = Path.Combine(jobDir, Guid.NewGuid().ToString("N") + ".json");
            var job = new Dictionary<string, object>
            {
                ["server"] = server,
                ["image"] = Path.GetFullPath(original),
                ["output"] = path,
                ["seconds"] = seconds,
                ["shot_direction"] = scene.ShotDirection ?? "hold"
            };
            await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job), ct);
            try
            {
                log?.Invoke($"FramePack: creating {seconds:0.0}s local AI micro-motion (a running FramePack instance is required)...");
                // Force UTF-8 even if Windows locale uses cp1252/charmap.
                // Gradio client prints non-ASCII progress text on startup.
                var result = await runner.RunAsync(python,
                    $"-X utf8 -u \"{WorkerPath}\" --job \"{jobPath}\"", Root, ct);
                if (result.ExitCode != 0 || !File.Exists(path)
                    || new FileInfo(path).Length <= 8192)
                    throw new IOException("Local FramePack adapter failed: " +
                        (result.StdErr.Length > 900
                            ? result.StdErr[^900..]
                            : result.StdErr));
            }
            finally
            {
                try { File.Delete(jobPath); } catch (IOException) { }
            }
            log?.Invoke($"FramePack: animated clip saved at {path}. Original image retained.");
            return path;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            // Never interrupt the established publishing/rendering pipeline
            // because optional GPU animation failed or wasn't installed.
            log?.Invoke("FramePack unavailable; keeping lightweight image motion. " +
                error.Message);
            return null;
        }
    }
}
