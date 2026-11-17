using System.Security.Cryptography;
using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZeroVideo.App.Services;

public sealed record PixabayFlowClip(
    long Id, string FilePath, string SourcePage, string SearchQuery, string Tags);

public sealed record PixabayFootageScene(
    int SceneNumber, string NarrationExcerpt, string SearchQuery, string Mood);

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
    // A six-second shot per unique clip, up to the largest 15-minute YouTube Flow preset.
    public const int MaxClipsPerVideo = 150;
    public const int MaxQueriesPerVideo = 30;
    public const int ClipsPerMinute = 10;

    public static int TargetClipCount(TimeSpan narrationDuration)
        => Math.Clamp((int)Math.Ceiling(narrationDuration.TotalSeconds / 6d), 1, MaxClipsPerVideo);

    public static int SuggestedQueryCount(int targetClips)
        => Math.Clamp((int)Math.Ceiling(targetClips / 5d), 6, MaxQueriesPerVideo);
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
        Action<string>? log, CancellationToken ct, int targetClipCount = 0,
        Action<int, int>? onClipReady = null,
        IReadOnlyList<PixabayFootageScene>? scenePlan = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Enter your own Pixabay API key in YouTube Flow.");
        var queries = requestedQueries
            .Select(x => Regex.Replace(x?.Trim() ?? "", @"\s+", " "))
            .Where(x => x.Length > 1)
            .Select(x => x.Length > 100 ? x[..100] : x)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxQueriesPerVideo).ToList();
        if (queries.Count == 0)
            throw new InvalidOperationException("No footage searches were supplied.");
        if (scenePlan is not null &&
            (scenePlan.Count != queries.Count ||
             scenePlan.Where((scene, i) =>
                 !scene.SearchQuery.Equals(queries[i], StringComparison.OrdinalIgnoreCase)).Any()))
            throw new InvalidDataException("Scene plan must have exactly one distinct search per narration segment, in matching order.");

        var target = targetClipCount <= 0
            ? queries.Count // Preserve compatibility for older callers/tests.
            : Math.Clamp(targetClipCount, 1, MaxClipsPerVideo);
        RateLimitedDuringLastAcquire = false;
        var usedIds = new HashSet<long>();
        var clips = new List<PixabayFlowClip>(target);
        var cacheDir = Path.Combine(_dataDirectory, "Pixabay", "ApiCache");
        var videoDir = Path.Combine(_dataDirectory, "Pixabay", "LicensedVideos");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(videoDir);
        PixabayRateLimitException? rateLimit = null;

        // One request fetches dozens of relevant results. Download only selected
        // clips for this user-created project, not every returned search hit.
        var candidates = new List<(long Id, string Url, string Page, string Tags, double Score)>[queries.Count];
        var lookupOrder = Enumerable.Range(0, queries.Count)
            .OrderByDescending(i => IsFreshCache(queries[i], apiKey.Trim(), cacheDir)).ToList();
        foreach (var index in lookupOrder)
        {
            ct.ThrowIfCancellationRequested();
            var query = queries[index];
            candidates[index] = [];
            JsonDocument response;
            try
            {
                if (rateLimit is not null && !IsFreshCache(query, apiKey.Trim(), cacheDir))
                    continue; // Do not keep making API requests after 429.
                response = await QueryAsync(query, apiKey.Trim(), cacheDir, log, ct);
            }
            catch (PixabayRateLimitException ex)
            {
                rateLimit = ex;
                RateLimitedDuringLastAcquire = true;
                log?.Invoke("Pixabay API quota reached; skipping uncached searches. " + ex.Message);
                continue;
            }
            using (response)
            {
                if (!response.RootElement.TryGetProperty("hits", out var hits) ||
                    hits.ValueKind != JsonValueKind.Array) continue;

                var matches = candidates[index];
                foreach (var hit in hits.EnumerateArray())
                {
                    if (!hit.TryGetProperty("id", out var node) ||
                        !node.TryGetInt64(out var id) ||
                        !hit.TryGetProperty("videos", out var versions) ||
                        !TrySelectVideo(versions, out var url, out var width, out var height))
                        continue;
                    var sourcePage = ReadString(hit, "pageURL");
                    var tags = ReadString(hit, "tags");
                    var targetRatio = ratio == "9:16" ? 9d / 16 : 16d / 9;
                    var aspectPenalty = Math.Abs(Math.Log((double)width / height / targetRatio));
                    var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var matchingTerms = terms.Count(term =>
                        tags.Contains(term, StringComparison.OrdinalIgnoreCase));
                    var mood = scenePlan is null ? "neutral" : scenePlan[index].Mood;
                    if (scenePlan is not null && !IsScriptVisualMatch(query, tags, mood))
                        continue; // Never substitute bright/unrelated media for a dark scene.
                    matches.Add((id, url, sourcePage, tags,
                        matchingTerms * 10d + MoodBonus(tags, mood) - aspectPenalty * 3d));
                }
                candidates[index] = matches.OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Id).ToList();
            }
        }

        // Fair round-robin allocation across narration-ordered queries.
        // A single popular search cannot monopolize all 150 slots.
        var positions = new int[queries.Count];
        var sceneCounts = new int[queries.Count];
        var sceneTargets = Enumerable.Range(0, queries.Count)
            .Select(i => (int)Math.Ceiling((i + 1d) * target / queries.Count) -
                         (int)Math.Ceiling(i * target / (double)queries.Count)).ToArray();
        var failedDownloads = new HashSet<long>();
        bool selectedAny;
        bool mediaRateLimited = false;
        do
        {
            selectedAny = false;
            for (var i = 0; i < queries.Count && clips.Count < target && !mediaRateLimited; i++)
            {
                if (scenePlan is not null && sceneCounts[i] >= sceneTargets[i]) continue;
                var matches = candidates[i];
                if (matches is null) continue;
                while (positions[i] < matches.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    var candidate = matches[positions[i]++];
                    if (usedIds.Contains(candidate.Id) || failedDownloads.Contains(candidate.Id))
                        continue;
                    var destination = Path.Combine(videoDir, candidate.Id + "-medium.mp4");
                    try
                    {
                        if (!File.Exists(destination) || new FileInfo(destination).Length < 4096)
                            await DownloadAsync(candidate.Url, destination, ct);
                        usedIds.Add(candidate.Id);
                        clips.Add(new PixabayFlowClip(candidate.Id, destination,
                            candidate.Page, queries[i], candidate.Tags));
                        selectedAny = true;
                        sceneCounts[i]++;
                        onClipReady?.Invoke(clips.Count, target);
                        if (clips.Count <= 10 || clips.Count % 10 == 0 || clips.Count == target)
                            log?.Invoke($"Pixabay: {clips.Count}/{target} distinct licensed clips ready.");
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (PixabayRateLimitException ex)
                    {
                        rateLimit = ex;
                        RateLimitedDuringLastAcquire = true;
                        mediaRateLimited = true;
                        log?.Invoke("Pixabay clip CDN is rate-limited; stopping download requests: " + ex.Message);
                        break;
                    }
                    catch (Exception ex)
                    {
                        failedDownloads.Add(candidate.Id);
                        log?.Invoke($"Pixabay: could not download clip {candidate.Id}: {ex.Message}");
                    }
                }
            }
        } while (selectedAny && clips.Count < target && !mediaRateLimited);

        if (rateLimit is not null)
        {
            log?.Invoke($"Pixabay rate limit: {clips.Count}/{target} cached/available clips. Saving the project for a quota-respecting retry; no incomplete MP4 will be labeled finished.");
            throw new PixabayRateLimitException(rateLimit.RetryAfter);
        }
        if (scenePlan is not null)
        {
            var missing = Enumerable.Range(0, queries.Count)
                .Where(i => sceneCounts[i] < sceneTargets[i]).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"Pixabay footage does not match {missing.Count} narration scene(s) ({clips.Count}/{target} matching clips ready). " +
                    "The project is saved, not rendered with unrelated stock. Refine the scene searches or retry when suitable footage is available.");
        }
        if (clips.Count < target)
            throw new InvalidOperationException(
                $"Pixabay provided {clips.Count}/{target} distinct clips across {queries.Count} searches. The job remains resumable; expand/change the footage searches or use more licensed media rather than repeating unrelated clips.");
        // Render in narration order, keeping each search's varied shot sequence.
        return clips.OrderBy(c => queries.FindIndex(q =>
            q.Equals(c.SearchQuery, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private static readonly HashSet<string> NonVisualSearchWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "the", "for", "with", "from", "into", "that", "this", "over",
        "scene", "stock", "video", "footage", "cinematic", "shot", "film",
        "scary", "creepy", "eerie", "ominous", "dark", "spooky", "horror",
        "somber", "tense", "upbeat", "happy", "bright", "calm", "moody"
    };
    private static readonly HashSet<string> OminousSignals = new(StringComparer.OrdinalIgnoreCase)
    {
        "dark", "night", "nighttime", "shadow", "shadows", "haunted",
        "horror", "scary", "spooky", "eerie", "creepy", "fog", "mist",
        "storm", "cemetery", "graveyard", "ghost", "abandoned", "ominous",
        "gloom", "gloomy", "silhouette", "candlelight", "moonlight",
        "midnight", "mysterious", "suspense", "terror", "demon"
    };
    private static readonly HashSet<string> BrightSignals = new(StringComparer.OrdinalIgnoreCase)
    {
        "sunny", "sunshine", "daylight", "cheerful", "smiling", "smile",
        "party", "wedding", "holiday", "celebration", "vacation",
        "beach", "rainbow", "happy", "joyful", "summer", "colorful"
    };

    private static HashSet<string> Words(string value)
        => Regex.Matches(value.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(m => m.Value).Where(w => w.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsScriptVisualMatch(string query, string tags, string mood)
    {
        var tagWords = Words(tags);
        var subjectWords = Words(query).Where(w => !NonVisualSearchWords.Contains(w)).ToArray();
        // No reliable metadata evidence of the requested object/action: skip it.
        if (subjectWords.Length == 0 ||
            !subjectWords.Any(w => tagWords.Contains(w)))
            return false;

        if (mood.Equals("ominous", StringComparison.OrdinalIgnoreCase))
            return !BrightSignals.Overlaps(tagWords) && OminousSignals.Overlaps(tagWords);
        if (mood.Equals("upbeat", StringComparison.OrdinalIgnoreCase))
            return !OminousSignals.Overlaps(tagWords) && BrightSignals.Overlaps(tagWords);
        if (mood.Equals("tense", StringComparison.OrdinalIgnoreCase) ||
            mood.Equals("somber", StringComparison.OrdinalIgnoreCase))
            return !BrightSignals.Overlaps(tagWords);
        return true;
    }

    private static int MoodBonus(string tags, string mood)
    {
        var words = Words(tags);
        return mood.Equals("ominous", StringComparison.OrdinalIgnoreCase)
            ? words.Count(OminousSignals.Contains) * 6
            : mood.Equals("upbeat", StringComparison.OrdinalIgnoreCase)
                ? words.Count(BrightSignals.Contains) * 6 : 0;
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
            "&per_page=50&video_type=all&safesearch=true";
        // Repeated HTTP 429 is possible even after a window resets. Respect
        // each provider reset time; cap the retries/wait so the UI never hangs
        // indefinitely and never flood the service with rapid requests.
        const int maxResetRetries = 3;
        var waited = TimeSpan.Zero;
        for (var attempt = 0; attempt <= maxResetRetries; attempt++)
        {
            using var result = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (result.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var reset = RateReset(result);
                if (attempt < maxResetRetries && reset is { } wait &&
                    wait <= _maxAutoWait &&
                    waited + wait + TimeSpan.FromSeconds(1) <= TimeSpan.FromMinutes(3))
                {
                    var delay = wait + TimeSpan.FromSeconds(1);
                    waited += delay;
                    log?.Invoke($"Pixabay HTTP 429: waiting {Math.Ceiling(delay.TotalSeconds)} seconds for the next permitted quota window (retry {attempt + 1}/{maxResetRetries}).");
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
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new PixabayRateLimitException(RateReset(response));
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
