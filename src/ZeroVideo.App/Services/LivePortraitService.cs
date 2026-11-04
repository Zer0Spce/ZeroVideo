using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Runs LivePortrait on the existing loopback ComfyUI instance using
/// Kijai's nodes plus ZeroVideo's original low-motion driving image batch.
/// Never redraws the full scene with a diffusion model.
/// </summary>
public sealed class LivePortraitService(LocalImageService localImages)
{
    private static readonly Uri Server = new("http://127.0.0.1:8188/");
    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>
    /// Avoid submitting landscape / object-only illustrations to LivePortrait.
    /// Uses the existing ComfyUI Python and MediaPipe model on CPU only.
    /// A face failure never interrupts video production.
    /// </summary>
    public async Task<bool> CanAnimateAsync(
        string original, Action<string>? log, CancellationToken ct)
    {
        if (!File.Exists(original))
        {
            log?.Invoke("LivePortrait skipped: scene image is missing.");
            return false;
        }
        try
        {
            var python = LivePortraitSetupService.BundledPythonExecutable;
            var model = Path.Combine(LivePortraitSetupService.NodeFolder,
                "media_pipe", "mp_models",
                "face_landmarker_v2_with_blendshapes.task");
            if (string.IsNullOrWhiteSpace(python) || !File.Exists(python) ||
                !File.Exists(model))
                throw new FileNotFoundException(
                    "LivePortrait Python or MediaPipe face landmarks missing. " +
                    "Use Install LivePortrait to repair the setup.");

            var script = Path.Combine(AppPaths.ManagedToolsDirectory,
                "liveportrait_face_probe.py");
            using (var source = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(
                    "ZeroVideo.App.Resources.liveportrait_face_probe.py"))
            {
                if (source is null)
                    throw new FileNotFoundException(
                        "The embedded LivePortrait face-check script is missing.");
                Directory.CreateDirectory(Path.GetDirectoryName(script)!);
                var temporary = script + ".partial";
                using (var destination = File.Create(temporary))
                    source.CopyTo(destination);
                File.Move(temporary, script, overwrite:true);
            }
            var result = await new ProcessRunner().RunAsync(python,
                "-X utf8 -s -u \"" + script + "\" --image \"" +
                Path.GetFullPath(original) + "\" --landmarks \"" + model + "\"",
                LivePortraitSetupService.NodeFolder, ct);
            if (result.ExitCode == 0)
            {
                log?.Invoke("LivePortrait face check passed: " +
                    result.StdOut.Trim());
                return true;
            }
            if (result.ExitCode == 3)
            {
                log?.Invoke("LivePortrait skipped scene: " +
                    result.StdOut.Trim() + ". Using lightweight camera motion.");
                return false;
            }
            var details = (result.StdErr + " " + result.StdOut).Trim();
            log?.Invoke("LivePortrait face check failed (" +
                result.ExitCode + "): " +
                (details.Length > 850 ? details[^850..] : details) +
                ". Using camera motion.");
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke("LivePortrait face detection unavailable: " +
                ex.Message + ". Using camera motion.");
            return false;
        }
    }

    private static readonly string[] RequiredNodes =
    [
        "DownloadAndLoadLivePortraitModels", "LivePortraitLoadMediaPipeCropper",
        "LivePortraitCropper", "LivePortraitProcess", "LivePortraitComposite",
        "ZeroVideoIdleDriver", "CreateVideo", "SaveVideo"
    ];

    public async Task TestConnectionAsync(CancellationToken ct)
    {
        if (!LivePortraitSetupService.IsInstalled)
            throw new FileNotFoundException(
                "Install LivePortrait in Settings first (reuses local ComfyUI).");
        if (!await localImages.IsOnlineAsync(ct))
            throw new IOException(
                "ComfyUI is offline at 127.0.0.1:8188. Start ComfyUI from Local Images.");
        foreach (var node in RequiredNodes)
        {
            using var request = new CancellationTokenSource(
                TimeSpan.FromSeconds(12));
            using var combined = CancellationTokenSource
                .CreateLinkedTokenSource(ct, request.Token);
            using var result = await Http.GetAsync(
                new Uri(Server, "object_info/" + node), combined.Token);
            result.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(
                await result.Content.ReadAsStringAsync(combined.Token));
            if (!doc.RootElement.TryGetProperty(node, out _))
            {
                // The pinned upstream class name is correct. A missing
                // registration generally means the Python extension threw
                // during import or a different/stale server owns port 8188.
                var diag = await new LivePortraitSetupService(new ProcessRunner())
                    .DiagnoseImportsAsync(ct);
                throw new InvalidOperationException(
                    "ComfyUI at 127.0.0.1:8188 is missing " + node +
                    ", but the local Python import check succeeded. " +
                    "Another or stale ComfyUI instance may be using port 8188. " +
                    "Close any external ComfyUI server and restart the " +
                    "ZeroVideo-owned server. " + diag);
            }
        }
    }

    public async Task<string?> TryAnimateAsync(
        string original, VideoProject project, ProjectScene scene,
        ZeroVideoSettings settings, Action<string>? log, CancellationToken ct)
    {
        if (settings.AiImageAnimationEngine != "LivePortrait (Local)") return null;
        try
        {
            if (!File.Exists(original))
                throw new FileNotFoundException("Original portrait missing.", original);
            if (!LivePortraitSetupService.IsInstalled)
                throw new InvalidOperationException(
                    "LivePortrait isn't installed; use Install LivePortrait in Settings.");
            if (!await localImages.IsOnlineAsync(ct))
                await localImages.StartAsync(log, ct);
            await TestConnectionAsync(ct);

            var seconds = Math.Clamp(settings.LivePortraitClipSeconds, 3.0, 5.0);
            var strength = settings.LivePortraitMotionStrength == "Medium"
                ? "Medium" : "Low";
            var info = new FileInfo(original);
            var fingerprint = string.Join("|",
                Path.GetFullPath(original), info.Length,
                info.LastWriteTimeUtc.Ticks, seconds, strength,
                settings.LivePortraitBlink, "LivePortrait-ZeroVideo-v1");
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint));
            var hash = Convert.ToHexString(digest)[..24].ToLowerInvariant();
            var folder = Path.Combine(project.ProjectDirectory, "animated");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, "liveportrait-" + hash + ".mp4");
            if (File.Exists(target) && new FileInfo(target).Length > 16384)
            {
                log?.Invoke("LivePortrait: reusing cached facial animation.");
                return target;
            }

            var uploaded = await UploadImageAsync(original, ct);
            object[] Ref(string id, int index) => [id, index];
            const int fps = 12;
            var graph = new Dictionary<string, object>
            {
                ["1"] = new { class_type = "LoadImage",
                    inputs = new { image = uploaded } },
                ["2"] = new { class_type = "DownloadAndLoadLivePortraitModels",
                    inputs = new { precision = "fp16", mode = "human" } },
                ["3"] = new { class_type = "LivePortraitLoadMediaPipeCropper",
                    inputs = new { landmarkrunner_onnx_device = "CPU",
                                   keep_model_loaded = true } },
                ["4"] = new { class_type = "LivePortraitCropper",
                    inputs = new { pipeline = Ref("2",0),
                                   cropper = Ref("3",0),
                                   source_image = Ref("1",0),
                                   dsize = 512, scale = 2.3,
                                   vx_ratio = 0.0, vy_ratio = -0.125,
                                   face_index = 0, face_index_order = "large-small",
                                   rotate = true } },
                ["5"] = new { class_type = "ZeroVideoIdleDriver",
                    inputs = new { source_image = Ref("1",0),
                                   seconds, fps,
                                   blink = settings.LivePortraitBlink,
                                   strength } },
                ["6"] = new { class_type = "LivePortraitProcess",
                    inputs = new {
                        pipeline = Ref("2",0), crop_info = Ref("4",1),
                        source_image = Ref("1",0), driving_images = Ref("5",0),
                        lip_zero = false, lip_zero_threshold = 0.03,
                        stitching = true, delta_multiplier = 0.4,
                        mismatch_method = "constant",
                        relative_motion_mode = "relative",
                        driving_smooth_observation_variance = 0.000003
                    } },
                ["7"] = new { class_type = "LivePortraitComposite",
                    inputs = new {
                        source_image = Ref("1",0),
                        cropped_image = Ref("6",0),
                        liveportrait_out = Ref("6",1)
                    } },
                ["8"] = new { class_type = "CreateVideo",
                    inputs = new { images = Ref("7",0), fps = 12.0 } },
                ["9"] = new { class_type = "SaveVideo",
                    inputs = new {
                        video = Ref("8",0), filename_prefix = "ZeroVideoPortrait",
                        format = "mp4", codec = new { codec = "h264" }
                    } }
            };

            log?.Invoke("LivePortrait: generating " + seconds.ToString("0.0") +
                "s gentle blink/head motion. Existing illustration is preserved.");
            var promptId = await SubmitAsync(graph, ct);
            try
            {
                await DownloadResultAsync(promptId, target, ct);
            }
            catch (OperationCanceledException)
            {
                await InterruptAsync();
                throw;
            }
            log?.Invoke("LivePortrait: animated facial clip saved at " + target);
            return target;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A scenic shot or stylized tiny face can fail detection.
            // Do not interrupt the established scene or publishing pipeline.
            log?.Invoke("LivePortrait unavailable for this scene; " +
                "using camera motion instead. " + ex.Message);
            return null;
        }
    }

    private static async Task<string> UploadImageAsync(
        string original, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        await using var input = File.OpenRead(original);
        using var body = new StreamContent(input);
        var ext = Path.GetExtension(original).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            throw new InvalidDataException("LivePortrait requires PNG/JPEG/WebP.");
        body.Headers.ContentType = new MediaTypeHeaderValue(
            ext == ".png" ? "image/png"
                : ext == ".webp" ? "image/webp" : "image/jpeg");
        form.Add(body, "image", Path.GetFileName(original));
        form.Add(new StringContent("input"), "type");
        form.Add(new StringContent("true"), "overwrite");
        using var response = await Http.PostAsync(
            new Uri(Server,"upload/image"), form, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(ct));
        var name = doc.RootElement.GetProperty("name").GetString();
        return !string.IsNullOrWhiteSpace(name)
            ? name : throw new InvalidDataException(
                "ComfyUI returned an empty uploaded image name.");
    }

    private static async Task<string> SubmitAsync(
        Dictionary<string, object> graph, CancellationToken ct)
    {
        using var response = await Http.PostAsJsonAsync(new Uri(Server,"prompt"),
            new { prompt = graph,
                  client_id = "zerovideo-liveportrait-" +
                      Guid.NewGuid().ToString("N") }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException("ComfyUI rejected LivePortrait graph: " +
                body[..Math.Min(body.Length, 900)]);
        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("prompt_id").GetString();
        return Guid.TryParse(id, out _)
            ? id! : throw new InvalidDataException(
                "ComfyUI did not return a valid prompt ID.");
    }

    private static bool FindMp4(JsonElement element, out string file,
        out string folder)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("filename", out var name) &&
                name.ValueKind == JsonValueKind.String &&
                name.GetString() is { } s &&
                s.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                file = s;
                folder = element.TryGetProperty("subfolder", out var sub)
                    ? sub.GetString() ?? "" : "";
                return true;
            }
            foreach (var child in element.EnumerateObject())
                if (FindMp4(child.Value, out file, out folder)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                if (FindMp4(child, out file, out folder)) return true;
        }
        file = ""; folder = "";
        return false;
    }

    /// <summary>
    /// ComfyUI stores node-specific Python exceptions in status.messages.
    /// Surface the actual failure rather than labeling every failure
    /// "unrecognizable face" (which hid model download, VRAM and node errors).
    /// </summary>
    private static string ExplainExecutionError(JsonElement history)
    {
        if (history.TryGetProperty("status", out var status) &&
            status.TryGetProperty("messages", out var messages) &&
            messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var pair in messages.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array ||
                    pair.GetArrayLength() < 2 ||
                    pair[0].ValueKind != JsonValueKind.String ||
                    pair[0].GetString() != "execution_error" ||
                    pair[1].ValueKind != JsonValueKind.Object)
                    continue;
                var info = pair[1];
                static string Get(JsonElement node, string name) =>
                    node.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String
                        ? value.GetString() ?? "" : "";
                var nodeId = Get(info, "node_id");
                var nodeType = Get(info, "node_type");
                var error = Get(info, "exception_message");
                var errorType = Get(info, "exception_type");
                var text = $"Node {nodeId} ({nodeType}): {errorType}: {error}";
                if (info.TryGetProperty("traceback", out var trace) &&
                    trace.ValueKind == JsonValueKind.Array)
                {
                    var lines = trace.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString() ?? "")
                        .TakeLast(4);
                    text += " | " + string.Join(" ", lines);
                }
                return text.Length > 1600 ? text[^1600..] : text;
            }
        }
        return "ComfyUI returned an error but did not include node details; " +
            "inspect its Python console for the failure.";
    }

    private static async Task DownloadResultAsync(
        string promptId, string target, CancellationToken ct)
    {
        for (var i = 0; i < 360; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct);
            using var response = await Http.GetAsync(
                new Uri(Server, "history/" + Uri.EscapeDataString(promptId)), ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty(promptId, out var history))
                continue;
            if (history.TryGetProperty("status", out var status) &&
                status.TryGetProperty("status_str", out var state) &&
                state.GetString() == "error")
                throw new InvalidOperationException(
                    "ComfyUI LivePortrait execution failed: " +
                    ExplainExecutionError(history));
            if (!history.TryGetProperty("outputs", out var outputs) ||
                !outputs.TryGetProperty("9", out var result) ||
                !FindMp4(result, out var name, out var folder))
                continue;
            if (name.Contains('/') || name.Contains('\\') ||
                folder.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException(
                    "LivePortrait output contained an unsafe filename.");
            var uri = new Uri(Server, "view?filename=" +
                Uri.EscapeDataString(name) + "&subfolder=" +
                Uri.EscapeDataString(folder) + "&type=output");
            using var video = await Http.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, ct);
            video.EnsureSuccessStatusCode();
            var partial = target + ".partial";
            try
            {
                await using (var from = await video.Content.ReadAsStreamAsync(ct))
                await using (var to = File.Create(partial))
                    await from.CopyToAsync(to, ct);
                if (new FileInfo(partial).Length < 16384)
                    throw new InvalidDataException(
                        "ComfyUI returned an empty LivePortrait clip.");
                await using (var verify = File.OpenRead(partial))
                {
                    var header = new byte[12];
                    if (await verify.ReadAsync(header, ct) != 12 ||
                        Encoding.ASCII.GetString(header,4,4) != "ftyp")
                        throw new InvalidDataException(
                            "LivePortrait generated a non-MP4 file.");
                }
                File.Move(partial, target, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(partial)) File.Delete(partial); }
                catch (IOException) { }
            }
            return;
        }
        throw new TimeoutException("LivePortrait generation timed out " +
            "(12 minutes). The original scene image remains unchanged.");
    }

    private static async Task InterruptAsync()
    {
        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var result = await Http.PostAsJsonAsync(
                new Uri(Server, "interrupt"), new { }, ct.Token);
        }
        catch { /* Cancellation should not depend on a running server. */ }
    }
}
