using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ZeroVideo.App.Services;

/// <summary>Official upload APIs. OAuth access tokens are supplied per session and never persisted.</summary>
public sealed class PublishingService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public async Task<string> UploadYouTubeAsync(string file, string title, string description,
        string accessToken, string privacy, DateTimeOffset? publishAt, CancellationToken ct)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Video not found", file);
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("YouTube OAuth access token required (youtube.upload scope).");
        if (publishAt.HasValue && publishAt <= DateTimeOffset.UtcNow) throw new ArgumentException("Schedule must be in the future.");
        if (!new[] { "private", "unlisted", "public" }.Contains(privacy)) throw new ArgumentException("Invalid YouTube privacy level.");
        var length = new FileInfo(file).Length;
        var metadata = new
        {
            snippet = new { title = title.Length > 100 ? title[..100] : title, description, categoryId = "22" },
            status = new
            {
                privacyStatus = publishAt.HasValue ? "private" : privacy,
                publishAt = publishAt?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                selfDeclaredMadeForKids = false
            }
        };
        using var start = new HttpRequestMessage(HttpMethod.Post,
            "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status");
        start.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        start.Headers.TryAddWithoutValidation("X-Upload-Content-Length", length.ToString());
        start.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "video/mp4");
        start.Content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");
        using var started = await Http.SendAsync(start, ct);
        var responseText = await started.Content.ReadAsStringAsync(ct);
        if (!started.IsSuccessStatusCode) throw new InvalidOperationException($"YouTube session failed ({(int)started.StatusCode}): {responseText}");
        var uri = started.Headers.Location ?? throw new InvalidOperationException("YouTube did not provide an upload URL.");
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "www.googleapis.com")
            throw new InvalidOperationException("Unexpected YouTube upload host.");
        using var stream = File.OpenRead(file);
        using var upload = new HttpRequestMessage(HttpMethod.Put, uri);
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        upload.Content = new StreamContent(stream);
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        upload.Content.Headers.ContentLength = length;
        using var uploaded = await Http.SendAsync(upload, ct);
        var body = await uploaded.Content.ReadAsStringAsync(ct);
        if (!uploaded.IsSuccessStatusCode) throw new InvalidOperationException($"YouTube upload failed ({(int)uploaded.StatusCode}): {body}");
        using var result = JsonDocument.Parse(body);
        var id = result.RootElement.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("YouTube did not return a video ID.");
        return $"YouTube upload accepted: https://www.youtube.com/watch?v={id}" +
               (publishAt.HasValue ? $" (scheduled for {publishAt.Value:u})" : $" ({privacy})");
    }

    public async Task<string> UploadTikTokAsync(string file, string title, string accessToken,
        string privacy, CancellationToken ct)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Video not found", file);
        if (string.IsNullOrWhiteSpace(accessToken)) throw new InvalidOperationException("TikTok OAuth token with video.publish scope required.");
        using var creatorRequest = new HttpRequestMessage(HttpMethod.Post, "https://open.tiktokapis.com/v2/post/publish/creator_info/query/");
        creatorRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        creatorRequest.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var creatorResponse = await Http.SendAsync(creatorRequest, ct);
        var creatorJson = await creatorResponse.Content.ReadAsStringAsync(ct);
        if (!creatorResponse.IsSuccessStatusCode) throw new InvalidOperationException($"TikTok creator query failed: {creatorJson}");
        using var creatorDoc = JsonDocument.Parse(creatorJson);
        CheckTikTokError(creatorDoc.RootElement);
        var creator = creatorDoc.RootElement.GetProperty("data");
        var allowed = creator.GetProperty("privacy_level_options").EnumerateArray()
            .Select(x => x.GetString()).ToList();
        if (!allowed.Contains(privacy)) throw new InvalidOperationException($"TikTok privacy {privacy} not allowed for this account. Allowed: {string.Join(", ", allowed)}");
        if (creator.TryGetProperty("max_video_post_duration_sec", out var maxDuration))
        {
            // TikTok validates duration server-side. Do not claim acceptance before the API confirms it.
        }
        var size = new FileInfo(file).Length;
        const long unit = 10_000_000;
        var chunks = Math.Max(1L, size / unit);
        var chunkSize = chunks == 1 ? size : unit;
        var metadata = new
        {
            post_info = new
            {
                title = title.Length > 2200 ? title[..2200] : title,
                privacy_level = privacy,
                disable_duet = true,
                disable_comment = false,
                disable_stitch = true,
                is_aigc = true
            },
            source_info = new { source = "FILE_UPLOAD", video_size = size, chunk_size = chunkSize, total_chunk_count = chunks }
        };
        using var init = new HttpRequestMessage(HttpMethod.Post, "https://open.tiktokapis.com/v2/post/publish/video/init/");
        init.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        init.Content = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8, "application/json");
        using var initResponse = await Http.SendAsync(init, ct);
        var initJson = await initResponse.Content.ReadAsStringAsync(ct);
        if (!initResponse.IsSuccessStatusCode) throw new InvalidOperationException($"TikTok init failed: {initJson}");
        using var initDoc = JsonDocument.Parse(initJson);
        CheckTikTokError(initDoc.RootElement);
        var data = initDoc.RootElement.GetProperty("data");
        var uploadUrl = new Uri(data.GetProperty("upload_url").GetString()!);
        if (uploadUrl.Scheme != Uri.UriSchemeHttps || uploadUrl.Host != "open-upload.tiktokapis.com")
            throw new InvalidOperationException("Unexpected TikTok upload host.");
        var publishId = data.GetProperty("publish_id").GetString();
        await using var stream = File.OpenRead(file);
        for (long i = 0; i < chunks; i++)
        {
            var offset = i * chunkSize;
            var length = (int)(i == chunks - 1 ? size - offset : chunkSize);
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, ct);
            using var upload = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
            upload.Content = new ByteArrayContent(bytes);
            upload.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            upload.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {offset}-{offset + length - 1}/{size}");
            using var response = await Http.SendAsync(upload, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"TikTok chunk {i + 1}/{chunks} failed: {await response.Content.ReadAsStringAsync(ct)}");
        }
        return $"TikTok upload accepted for processing (publish ID: {publishId}). Check TikTok for final status.";
    }

    private static void CheckTikTokError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) &&
            error.TryGetProperty("code", out var code) && code.GetString() != "ok")
            throw new InvalidOperationException($"TikTok API: {error}");
    }
}
