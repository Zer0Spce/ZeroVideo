using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Native ComfyUI Wan2.2 5B image-to-video. Uses existing local ComfyUI on
/// 127.0.0.1:8188; no separate video server or second Python installation.
/// The source illustration is always preserved. Failed generations never
/// replace camera motion.
/// </summary>
public sealed class WanVideoService(LocalImageService localImages)
{
    private static readonly Uri Server = new("http://127.0.0.1:8188/");
    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private const string Engine = "Wan2.2 5B (ComfyUI)";

    public async Task<string?> TryAnimateAsync(
        string original, VideoProject project, ProjectScene scene,
        ZeroVideoSettings settings, Action<string>? log, CancellationToken ct)
    {
        if (settings.AiImageAnimationEngine != Engine) return null;
        try
        {
            if (!File.Exists(original))
                throw new FileNotFoundException("Original image is missing.", original);
            if (!localImages.HasWanVideoModels)
                throw new FileNotFoundException(
                    "Wan2.2 5B models are missing. In Settings click 'Install Wan2.2 Video Models (~17 GB)'.");

            var seconds = Math.Clamp(settings.FramePackClipSeconds, 3.0, 5.0);
            var source = new FileInfo(original);
            var fingerprint = string.Join("|", Path.GetFullPath(original),
                source.Length, source.LastWriteTimeUtc.Ticks,
                seconds, project.AspectRatio, scene.ShotDirection,
                "wan22-5b-v1");
            var hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(fingerprint)))[..24].ToLowerInvariant();
            var directory = Path.Combine(project.ProjectDirectory, "animated");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "wan22-" + hash + ".mp4");
            if (File.Exists(target) && new FileInfo(target).Length > 16384)
            {
                log?.Invoke("Wan2.2: reusing cached animated clip.");
                return target;
            }

            if (!await localImages.IsOnlineAsync(ct))
                await localImages.StartAsync(log, ct);

            // Warn about an old ComfyUI build without native video nodes.
            foreach (var node in new[] { "Wan22ImageToVideoLatent", "CreateVideo", "SaveVideo" })
            {
                using var check = await Http.GetAsync(
                    new Uri(Server, "object_info/" + node), ct);
                if (!check.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        "ComfyUI is missing video node " + node +
                        ". Update ComfyUI portable to a version supporting Wan2.2 5B.");
                using var json = JsonDocument.Parse(
                    await check.Content.ReadAsStringAsync(ct));
                if (!json.RootElement.TryGetProperty(node, out _))
                    throw new InvalidOperationException(
                        "ComfyUI video node " + node + " was not registered.");
            }

            var uploaded = await UploadImageAsync(original, ct);
            var (width, height) = project.AspectRatio == "9:16"
                ? (352, 640) : (640, 352);
            var frames = 1 + 24 * Math.Clamp((int)Math.Round(seconds), 3, 5);
            var motion = BuildPrompt(scene.ShotDirection);
            var seed = BitConverter.ToInt64(
                SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)), 0)
                & long.MaxValue;
            object[] Ref(string node, int port) => [node, port];
            var graph = new Dictionary<string, object>
            {
                ["1"] = new { class_type = "UNETLoader", inputs = new {
                    unet_name = LocalImageService.WanVideoModel, weight_dtype = "default" } },
                ["2"] = new { class_type = "CLIPLoader", inputs = new {
                    clip_name = LocalImageService.WanVideoText, type = "wan",
                    device = "default" } },
                ["3"] = new { class_type = "VAELoader", inputs = new {
                    vae_name = LocalImageService.WanVideoVae } },
                ["4"] = new { class_type = "ModelSamplingSD3", inputs = new {
                    model = Ref("1", 0), shift = 8.0 } },
                ["5"] = new { class_type = "CLIPTextEncode", inputs = new {
                    clip = Ref("2", 0), text = motion } },
                ["6"] = new { class_type = "CLIPTextEncode", inputs = new {
                    clip = Ref("2", 0),
                    text = "violent action, explosions, new people, extra limbs, face morph, " +
                        "deformed hands, unnatural movements, sudden camera cuts, excessive " +
                        "camera movement, text, watermark, low quality, jitter, flicker" } },
                ["7"] = new { class_type = "LoadImage", inputs = new { image = uploaded } },
                ["8"] = new { class_type = "Wan22ImageToVideoLatent", inputs = new {
                    vae = Ref("3", 0), start_image = Ref("7", 0),
                    width, height, length = frames, batch_size = 1 } },
                ["9"] = new { class_type = "KSampler", inputs = new {
                    model = Ref("4", 0), positive = Ref("5", 0),
                    negative = Ref("6", 0), latent_image = Ref("8", 0),
                    seed, steps = 20, cfg = 5.0, sampler_name = "uni_pc",
                    scheduler = "simple", denoise = 1.0 } },
                ["10"] = new { class_type = "VAEDecode", inputs = new {
                    samples = Ref("9", 0), vae = Ref("3", 0) } },
                ["11"] = new { class_type = "CreateVideo", inputs = new {
                    images = Ref("10", 0), fps = 24.0 } },
                ["12"] = new { class_type = "SaveVideo", inputs = new {
                    video = Ref("11", 0), filename_prefix = "ZeroVideoWan",
                    format = "mp4", codec = new { codec = "h264" } } }
            };
            log?.Invoke($"Wan2.2: generating {seconds:0}s of subtle AI image motion " +
                $"at {width}x{height}; 8 GB GPU offloading may be slow.");
            var promptId = await SubmitAsync(graph, ct);
            try
            {
                await DownloadResultAsync(promptId, target, ct);
            }
            catch (OperationCanceledException)
            {
                await InterruptPromptAsync();
                throw;
            }
            log?.Invoke("Wan2.2: AI animation saved, original image preserved: " + target);
            return target;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke("Wan2.2 AI motion unavailable; original camera motion preserved. " +
                ex.Message);
            return null;
        }
    }

    private static string BuildPrompt(string? direction)
    {
        var shot = direction?.ToLowerInvariant() ?? "";
        var camera = shot.Contains("zoom") || shot.Contains("push")
            ? "almost imperceptible slow camera push"
            : shot.Contains("pan") ? "subtle camera drift"
            : "mostly locked camera";
        return "Animate this exact input illustration with very subtle natural motion. " +
            "If a human is present, very slight breathing and one natural blink. " +
            "Only animate rain, fog, light, fabric or hair if those are visible in the source. " +
            camera + ". Keep face, clothing, framing, all objects, artistic line work, " +
            "lighting, scene details and character identity consistent from beginning to end. " +
            "No dramatic movement, no walking, no explosions, no extra objects, no text.";
    }

    private static async Task<string> UploadImageAsync(
        string original, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        await using var file = File.OpenRead(original);
        using var body = new StreamContent(file);
        body.Headers.ContentType = new MediaTypeHeaderValue(
            Path.GetExtension(original).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png" : "image/jpeg");
        form.Add(body, "image", Path.GetFileName(original));
        form.Add(new StringContent("input"), "type");
        form.Add(new StringContent("true"), "overwrite");
        using var response = await Http.PostAsync(new Uri(Server, "upload/image"), form, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var filename = doc.RootElement.GetProperty("name").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(filename))
            throw new InvalidDataException("ComfyUI did not return an image name.");
        return filename;
    }

    private static async Task<string> SubmitAsync(
        Dictionary<string, object> graph, CancellationToken ct)
    {
        using var response = await Http.PostAsJsonAsync(
            new Uri(Server, "prompt"),
            new { prompt = graph, client_id = "zerovideo-wan-" + Guid.NewGuid().ToString("N") },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException("ComfyUI rejected Wan2.2 graph: " +
                body[..Math.Min(body.Length, 1100)]);
        using var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.GetProperty("prompt_id").GetString();
        if (!Guid.TryParse(id, out _))
            throw new InvalidDataException("ComfyUI did not return a valid prompt ID.");
        return id!;
    }

    private static async Task InterruptPromptAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await Http.PostAsJsonAsync(
                new Uri(Server, "interrupt"), new { }, timeout.Token);
        }
        catch { /* Cancellation must not block on a failed server. */ }
    }

    private static bool TryFindMp4(JsonElement element, out string name,
        out string subfolder)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("filename", out var f) &&
                f.ValueKind == JsonValueKind.String &&
                f.GetString() is { } value &&
                value.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
                subfolder = element.TryGetProperty("subfolder", out var sub)
                    ? sub.GetString() ?? "" : "";
                return true;
            }
            foreach (var property in element.EnumerateObject())
                if (TryFindMp4(property.Value, out name, out subfolder))
                    return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (TryFindMp4(item, out name, out subfolder))
                    return true;
        }
        name = "";
        subfolder = "";
        return false;
    }

    private static async Task DownloadResultAsync(
        string id, string target, CancellationToken ct)
    {
        // Video diffusion on RTX 3060 can take much longer than image creation.
        for (var retry = 0; retry < 1800; retry++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct);
            using var response = await Http.GetAsync(new Uri(Server,
                "history/" + Uri.EscapeDataString(id)), ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty(id, out var history))
                continue;
            if (history.TryGetProperty("status", out var status) &&
                status.TryGetProperty("status_str", out var statusStr) &&
                statusStr.GetString() == "error")
                throw new InvalidOperationException("ComfyUI failed to synthesize video. " +
                    "Check its logs for CUDA out of memory, update status or missing nodes.");
            if (!history.TryGetProperty("outputs", out var outputs) ||
                !outputs.TryGetProperty("12", out var output))
                continue;
            if (!TryFindMp4(output, out var name, out var subfolder))
                continue;
            if (name.Contains('/') || name.Contains('\\') ||
                subfolder.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException("ComfyUI returned unsafe video metadata.");
            var url = new Uri(Server, "view?filename=" + Uri.EscapeDataString(name) +
                "&subfolder=" + Uri.EscapeDataString(subfolder) + "&type=output");
            using var video = await Http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct);
            video.EnsureSuccessStatusCode();
            var temporary = target + ".partial";
            try
            {
                await using (var input = await video.Content.ReadAsStreamAsync(ct))
                await using (var outFile = File.Create(temporary))
                    await input.CopyToAsync(outFile, ct);
                if (new FileInfo(temporary).Length < 16384)
                    throw new InvalidDataException("ComfyUI returned an empty video.");
                // MP4 container signature at byte offset four.
                await using (var check = File.OpenRead(temporary))
                {
                    var header = new byte[12];
                    if (await check.ReadAsync(header, ct) < 12 ||
                        Encoding.ASCII.GetString(header, 4, 4) != "ftyp")
                        throw new InvalidDataException("ComfyUI returned a non-MP4 file.");
                }
                File.Move(temporary, target, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
            return;
        }
        throw new TimeoutException("Wan2.2 animation exceeded the one-hour " +
            "generation limit. The original image remains safe.");
    }
}
