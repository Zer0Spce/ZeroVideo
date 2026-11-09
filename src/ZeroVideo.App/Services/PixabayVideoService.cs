using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZeroVideo.App.Services;

public sealed record PixabayFlowClip(
    long Id, string FilePath, string SourcePage, string SearchQuery, string Tags);

/// <summary>
/// Small, user-initiated Pixabay retrieval. No scraping or background bulk harvesting.
/// API responses are cached for at least 24h as required by Pixabay.
/// </summary>
public sealed class PixabayVideoService
{
    public const int MaxDownloadsPerVideo = 10;
    private const long MaxClipBytes = 100L * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public async Task<IReadOnlyList<PixabayFlowClip>> AcquireAsync(
        IEnumerable<string> requestedQueries, string apiKey, string ratio,
        Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Enter your own Pixabay API key in YouTube Flow.");
        var queries = requestedQueries
            .Select(x => Regex.Replace(x?.Trim() ?? "", @"\s+", " "))
            .Where(x => x.Length > 1)
            .Select(x => x.Length > 100 ? x[..100] : x)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxDownloadsPerVideo).ToList();
        if (queries.Count == 0)
            throw new InvalidOperationException("No footage searches were supplied.");

        var usedIds = new HashSet<long>();
        var clips = new List<PixabayFlowClip>();
        var cacheDir = Path.Combine(AppPaths.DataDirectory, "Pixabay", "ApiCache");
        var videoDir = Path.Combine(AppPaths.DataDirectory, "Pixabay", "LicensedVideos");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(videoDir);
        foreach (var query in queries)
        {
            ct.ThrowIfCancellationRequested();
            using var response = await QueryAsync(query, apiKey.Trim(), cacheDir, ct);
            if (!response.RootElement.TryGetProperty("hits", out var hits) ||
                hits.ValueKind != JsonValueKind.Array)
                continue;

            var matches = new List<(long Id, string Url, string Page, string Tags, double Score)>();
            foreach (var hit in hits.EnumerateArray())
            {
                if (!hit.TryGetProperty("id", out var node) || !node.TryGetInt64(out var id) ||
                    usedIds.Contains(id) ||
                    !hit.TryGetProperty("videos", out var versions))
                    continue;
                if (!TrySelectVideo(versions, out var url, out var width, out var height))
                    continue;
                var sourcePage = ReadString(hit, "pageURL");
                var tags = ReadString(hit, "tags");
                var target = ratio == "9:16" ? 9d / 16 : 16d / 9;
                var aspectPenalty = Math.Abs(Math.Log((double)width / height / target));
                var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var matchingTerms = terms.Count(term => tags.Contains(term, StringComparison.OrdinalIgnoreCase));
                matches.Add((id, url, sourcePage, tags,
                    matchingTerms * 10d - aspectPenalty * 3d));
            }

            bool downloaded = false;
            foreach (var match in matches.OrderByDescending(x => x.Score).Take(5))
            {
                var path = Path.Combine(videoDir, match.Id + "-medium.mp4");
                try
                {
                    if (!File.Exists(path) || new FileInfo(path).Length < 4096)
                        await DownloadAsync(match.Url, path, ct);
                    usedIds.Add(match.Id);
                    clips.Add(new PixabayFlowClip(
                        match.Id, path, match.Page, query, match.Tags));
                    log?.Invoke("Pixabay: selected licensed stock clip " + match.Id +
                        " for '" + query + "'.");
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log?.Invoke("Pixabay: clip " + match.Id + " could not be used: " + ex.Message);
                }
            }
            if (!downloaded)
                log?.Invoke("Pixabay: no downloadable match for '" + query + "'.");
        }
        if (clips.Count == 0)
            throw new InvalidOperationException(
                "Pixabay returned no usable videos for the script. Change the script/topic or footage queries.");
        return clips;
    }

    private static bool TrySelectVideo(JsonElement versions,
        out string url, out int width, out int height)
    {
        url = ""; width = height = 0;
        foreach (var size in new[] { "medium", "small" })
        {
            if (!versions.TryGetProperty(size, out var version)) continue;
            var candidate = ReadString(version, "url");
            var w = version.TryGetProperty("width", out var wn) ? wn.GetInt32() : 0;
            var h = version.TryGetProperty("height", out var hn) ? hn.GetInt32() : 0;
            var length = version.TryGetProperty("size", out var sn) ? sn.GetInt64() : 0;
            if (w < 320 || h < 240 || length > MaxClipBytes ||
                !IsPixabayVideoUrl(candidate)) continue;
            url = candidate; width = w; height = h;
            return true;
        }
        return false;
    }

    private static string ReadString(JsonElement value, string name)
        => value.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? "" : "";

    private static bool IsPixabayVideoUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttps &&
           (uri.Host.Equals("pixabay.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".pixabay.com", StringComparison.OrdinalIgnoreCase)) &&
           uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

    private static async Task<JsonDocument> QueryAsync(
        string query, string key, string cacheDirectory, CancellationToken ct)
    {
        // Hash key+query so multiple Pixabay keys cannot share cached account responses.
        // The real key must never appear in file names, logs or GitHub.
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(key + "|" + query)));
        var file = Path.Combine(cacheDirectory, fingerprint + ".json");
        if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromHours(24))
            return JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));

        var url = "https://pixabay.com/api/videos/?key=" +
            Uri.EscapeDataString(key) + "&q=" + Uri.EscapeDataString(query) +
            "&per_page=20&safesearch=true&video_type=film";
        using var result = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (result.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("Pixabay rate limit reached. Retry after the API limit resets.");
        result.EnsureSuccessStatusCode();
        var json = await result.Content.ReadAsStringAsync(ct);
        var document = JsonDocument.Parse(json);
        try
        {
            await File.WriteAllTextAsync(file, json, ct);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static async Task DownloadAsync(string url, string destination, CancellationToken ct)
    {
        if (!IsPixabayVideoUrl(url))
            throw new InvalidDataException("Pixabay returned a non-Pixabay video URL.");
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > MaxClipBytes)
            throw new InvalidDataException("Stock footage file is over the 100 MB per-clip limit.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxClipBytes)
                    throw new InvalidDataException("Stock footage download exceeded 100 MB.");
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await target.FlushAsync(ct);
            if (total < 4096)
                throw new InvalidDataException("Pixabay clip was incomplete.");
            File.Move(temporary, destination, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
