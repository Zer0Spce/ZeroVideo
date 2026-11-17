using System.Security.Cryptography;
using System.Net;
using System.Globalization;
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
public sealed class PixabayRateLimitException : InvalidOperationException
{
    public TimeSpan? RetryAfter { get; }
    public PixabayRateLimitException(TimeSpan? retryAfter)
        : base(retryAfter is null
            ? "Pixabay returned HTTP 429. This API key is rate-limited. Retry after its quota resets; your existing project checkpoints remain saved."
            : $"Pixabay returned HTTP 429. The API indicates a reset in about {Math.Ceiling(retryAfter.Value.TotalSeconds)} seconds. Retry after that; existing project checkpoints remain saved.")
    {
        RetryAfter = retryAfter;
    }
}

public sealed class PixabayVideoService
{
    public const int MaxDownloadsPerVideo = 10;
    private const long MaxClipBytes = 100L * 1024 * 1024;
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly HttpClient _http;
    private readonly string _dataDirectory;
    private readonly TimeSpan _maxAutoWait;
    public bool RateLimitedDuringLastAcquire { get; private set; }

    // Optional client and storage for deterministic tests without a live API key.
    public PixabayVideoService(HttpClient? http = null, string? dataDirectory = null,
        TimeSpan? maxAutoWait = null)
    {
        _http = http ?? SharedHttp;
        _dataDirectory = dataDirectory ?? AppPaths.DataDirectory;
        _maxAutoWait = maxAutoWait ?? TimeSpan.FromSeconds(65);
    }

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

        RateLimitedDuringLastAcquire = false;
        var usedIds = new HashSet<long>();
        var clips = new List<PixabayFlowClip>();
        var cacheDir = Path.Combine(_dataDirectory, "Pixabay", "ApiCache");
        var videoDir = Path.Combine(_dataDirectory, "Pixabay", "LicensedVideos");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(videoDir);
        PixabayRateLimitException? rateLimit = null;

        // Prefer 24h cached queries before fresh API requests; restore script
        // sequence at the end so the video still follows the narration.
        var lookupOrder = queries.OrderByDescending(q =>
            IsFreshCache(q, apiKey.Trim(), cacheDir)).ToList();
        foreach (var query in lookupOrder)
        {
            ct.ThrowIfCancellationRequested();
            JsonDocument response;
            try
            {
                if (rateLimit is not null && !IsFreshCache(query, apiKey.Trim(), cacheDir))
                    continue;
                response = await QueryAsync(query, apiKey.Trim(), cacheDir, log, ct);
            }
            catch (PixabayRateLimitException ex)
            {
                rateLimit = ex;
                RateLimitedDuringLastAcquire = true;
                log?.Invoke("Pixabay quota reached: " + ex.Message +
                    " Continuing with cached footage, without more fresh API requests.");
                continue;
            }
            using (response)
            {
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
        }
        if (rateLimit is not null)
        {
            var minimumClips = Math.Min(3, queries.Count);
            if (clips.Count < minimumClips)
                throw new PixabayRateLimitException(rateLimit.RetryAfter);
            log?.Invoke($"Pixabay quota reached: continuing with {clips.Count} locally available clips; some searches were skipped.");
        }
        if (clips.Count == 0)
            throw new InvalidOperationException(
                "Pixabay returned no usable videos for the script. Change the script/topic or footage queries.");
        return clips.OrderBy(c => queries.FindIndex(q =>
            q.Equals(c.SearchQuery, StringComparison.OrdinalIgnoreCase))).ToList();
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

    private static string CacheFile(string query, string key, string cacheDirectory)
    {
        // Isolate cached responses by API key; never put the plaintext key in paths.
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(key + "|" + query)));
        return Path.Combine(cacheDirectory, fingerprint + ".json");
    }

    private static bool IsFreshCache(string query, string key, string cacheDirectory)
    {
        var file = CacheFile(query, key, cacheDirectory);
        return File.Exists(file) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromHours(24);
    }

    private static async Task<JsonDocument?> ReadCacheAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try { return JsonDocument.Parse(await File.ReadAllTextAsync(path, ct)); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private static TimeSpan? RateReset(HttpResponseMessage response)
    {
        // Pixabay documents X-RateLimit-Reset as seconds remaining, not a Unix epoch.
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            double.TryParse(values.FirstOrDefault(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= 0 && seconds <= 86400)
            return TimeSpan.FromSeconds(Math.Max(1, seconds));
        if (response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
            return delta;
        if (response.Headers.RetryAfter?.Date is { } date)
            return date > DateTimeOffset.UtcNow
                ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1);
        return null;
    }

    private async Task<JsonDocument> QueryAsync(
        string query, string key, string cacheDirectory,
        Action<string>? log, CancellationToken ct)
    {
        var file = CacheFile(query, key, cacheDirectory);
        if (IsFreshCache(query, key, cacheDirectory))
        {
            var cached = await ReadCacheAsync(file, ct);
            if (cached is not null) return cached;
        }

        var url = "https://pixabay.com/api/videos/?key=" +
            Uri.EscapeDataString(key) + "&q=" + Uri.EscapeDataString(query) +
            "&per_page=20&video_type=all";
        // At most one retry, only after the server-supplied reset timer.
        for (var attempt = 0; attempt <= 1; attempt++)
        {
            using var result = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (result.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var reset = RateReset(result);
                if (attempt == 0 && reset is { } wait && wait <= _maxAutoWait)
                {
                    var delay = wait + TimeSpan.FromSeconds(1);
                    log?.Invoke($"Pixabay API key temporarily rate-limited. Respecting its reset timer ({Math.Ceiling(delay.TotalSeconds)} seconds) before one retry.");
                    await Task.Delay(delay, ct);
                    continue;
                }
                // Using prior local results is not another API request.
                var stale = await ReadCacheAsync(file, ct);
                if (stale is not null)
                {
                    log?.Invoke("Pixabay rate-limited; using existing local search cache for '" + query + "'.");
                    return stale;
                }
                throw new PixabayRateLimitException(reset);
            }
            result.EnsureSuccessStatusCode();
            var json = await result.Content.ReadAsStringAsync(ct);
            var document = JsonDocument.Parse(json);
            try
            {
                // Cache only successful searches, never HTTP 429 responses.
                await File.WriteAllTextAsync(file, json, ct);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        throw new InvalidOperationException("Pixabay request retry ended unexpectedly.");
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken ct)
    {
        if (!IsPixabayVideoUrl(url))
            throw new InvalidDataException("Pixabay returned a non-Pixabay video URL.");
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > MaxClipBytes)
            throw new InvalidDataException("Stock footage file is over the 100 MB per-clip limit.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            long total = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxClipBytes)
                        throw new InvalidDataException("Stock footage download exceeded 100 MB.");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await target.FlushAsync(ct);
            } // Both streams must be closed before File.Move on Windows.
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
