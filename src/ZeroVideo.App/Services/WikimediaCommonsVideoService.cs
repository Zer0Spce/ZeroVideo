using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZeroVideo.App.Services;

/// <summary>
/// On-demand, scene-specific Wikimedia Commons video selection. Uses the
/// supported MediaWiki videoinfo API, local 24-hour response caching and
/// conservative commercial-use licenses. Never harvests an entire collection.
/// </summary>
public sealed class WikimediaCommonsVideoService
{
    private const long MaxBytes = 80L * 1024 * 1024;
    private static readonly HttpClient Shared = CreateClient();
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly HashSet<long> _selected = [];
    private int _queries;

    public WikimediaCommonsVideoService(HttpClient? client = null, string? dataDirectory = null)
    {
        _http = client ?? Shared;
        _base = Path.Combine(dataDirectory ?? AppPaths.DataDirectory, "WikimediaCommons");
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ZeroVideo/0.1 (+https://github.com/Zer0Spce/ZeroVideo)");
        return http;
    }

    public async Task<IReadOnlyList<PixabayFlowClip>> FindVideosAsync(
        PixabayFootageScene scene, int maximum,
        Func<string, CancellationToken, Task<bool>>? chromaScreenCheck,
        Action<string>? log, CancellationToken ct)
    {
        var results = new List<PixabayFlowClip>();
        var subject = WikimediaCommonsImageService.SubjectSearch(scene.SearchQuery);
        if (maximum < 1 || subject.Length < 3) return results;
        var cacheDir = Path.Combine(_base, "VideoApiCache");
        var videoDir = Path.Combine(_base, "LicensedVideos");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(videoDir);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "wikimedia-videos-v1:" + subject.ToLowerInvariant())));
        var cache = Path.Combine(cacheDir, hash + ".json");
        string json;
        if (File.Exists(cache) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromHours(24))
        {
            json = await File.ReadAllTextAsync(cache, ct);
        }
        else
        {
            if (_queries >= 14) return results; // Bounded, user-initiated lookups.
            _queries++;
            var parameters = new Dictionary<string, string>
            {
                ["action"] = "query",
                ["generator"] = "search",
                ["gsrsearch"] = subject + " filetype:video",
                ["gsrnamespace"] = "6",
                ["gsrlimit"] = "8",
                ["prop"] = "videoinfo",
                ["viprop"] = "url|mime|size|extmetadata|derivatives",
                ["viextmetadatafilter"] = "LicenseShortName|LicenseUrl|Artist|ImageDescription|Restrictions",
                ["format"] = "json",
                ["formatversion"] = "2",
                ["maxlag"] = "5"
            };
            var url = "https://commons.wikimedia.org/w/api.php?" +
                string.Join("&", parameters.Select(p =>
                    Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
            try
            {
                using var response = await _http.GetAsync(url, ct);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or
                    HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    log?.Invoke($"Wikimedia video search temporarily unavailable for '{subject}'; respecting server limits.");
                    return results;
                }
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(ct);
                await File.WriteAllTextAsync(cache, json, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log?.Invoke($"Wikimedia video search for scene {scene.SceneNumber} skipped: {ex.Message}");
                return results;
            }
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Array) return results;
        var terms = subject.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var page in pages.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (results.Count >= Math.Min(maximum, 2)) break;
            if (!page.TryGetProperty("pageid", out var idElement) ||
                !idElement.TryGetInt64(out var pageId) ||
                _selected.Contains(pageId) ||
                !page.TryGetProperty("videoinfo", out var infos) ||
                infos.ValueKind != JsonValueKind.Array || infos.GetArrayLength() == 0)
                continue;
            var info = infos[0];
            var mime = Read(info, "mime");
            if (!AllowedMime(mime)) continue;
            var title = Read(page, "title");
            var metadata = info.TryGetProperty("extmetadata", out var ext)
                ? ext : default;
            var license = Ext(metadata, "LicenseShortName");
            var licenseUrl = Ext(metadata, "LicenseUrl");
            var author = PlainText(Ext(metadata, "Artist"));
            var description = PlainText(Ext(metadata, "ImageDescription"));
            var restrictions = PlainText(Ext(metadata, "Restrictions"));
            if (restrictions.Length > 0 ||
                !WikimediaCommonsImageService.IsCommerciallyReusableLicense(
                    license, licenseUrl, author)) continue;
            var searchable = title + " " + description;
            if (!IsRelevantToNarration(scene, title, description) ||
                !WikimediaCommonsImageService.IsMoodCompatible(searchable, scene.Mood) ||
                PixabayVideoService.IsChromaKeyStockMetadata(searchable))
            {
                log?.Invoke($"Wikimedia scene {scene.SceneNumber}: rejected unrelated or mood-incompatible video '{title}'.");
                continue;
            }

            // Do not fetch the same 80 MB source repeatedly. Try Commons'
            // smaller transcodes in order, verifying the HTTP Content-Length
            // before streaming any large payload. All formats remain capped.
            var videoUrls = SelectVideoUrls(info);
            if (videoUrls.Count == 0) continue;
            var oversized = 0;
            var downloaded = false;
            foreach (var url in videoUrls)
            {
                ct.ThrowIfCancellationRequested();
                var extension = url.EndsWith(".ogv", StringComparison.OrdinalIgnoreCase) ||
                                url.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                    ? ".ogv" : ".webm";
                var urlId = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(url)))[..12];
                var local = Path.Combine(videoDir, $"{pageId}-{urlId}{extension}");
                try
                {
                    if (!File.Exists(local) || new FileInfo(local).Length < 4096 ||
                        new FileInfo(local).Length > MaxBytes)
                        await DownloadBoundedAsync(url, local, ct);
                    if (chromaScreenCheck is not null &&
                        await chromaScreenCheck(local, ct))
                    {
                        log?.Invoke($"Wikimedia video {pageId} excluded: chroma screen detected.");
                        break;
                    }
                    _selected.Add(pageId);
                    results.Add(new PixabayFlowClip(-1_000_000_000L - pageId,
                        local, "https://commons.wikimedia.org/wiki/" +
                               Uri.EscapeDataString(title.Replace(' ', '_')),
                        scene.SearchQuery, searchable, false,
                        author, license, licenseUrl, "Wikimedia Commons"));
                    log?.Invoke($"Wikimedia video: scene {scene.SceneNumber}, '{title}' ({license}).");
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (InvalidDataException ex) when (ex.Message.Contains("80 MB", StringComparison.Ordinal))
                {
                    oversized++;
                    // Large candidates are skipped; the next smaller Commons
                    // derivative is tried automatically.
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Wikimedia video {pageId}: candidate unavailable ({ex.Message}); trying another version.");
                }
            }
            if (!downloaded && oversized > 0)
                log?.Invoke($"Wikimedia video {pageId}: {oversized} oversized version(s); " +
                    "no usable version under 80 MB. Skipping this file, then trying other media sources.");
        }
        return results;
    }

    // Relevance is checked against BOTH the search phrase and the real narration.
    // Commons titles such as unrelated politicians' interviews must not pass
    // simply because they contain generic words such as "house" or "people".
    private static readonly HashSet<string> StopWords = new(
        new[] { "the", "and", "for", "this", "that", "with", "from", "into",
            "over", "then", "there", "were", "they", "them", "their", "when",
            "what", "which", "where", "while", "would", "about", "could",
            "more", "once", "just", "some", "been", "have", "having", "also",
            "video", "film", "scene", "stock", "history", "historical", "story",
            "really", "people", "person", "shows", "show", "today", "years",
            "time", "many", "made", "used", "still", "back", "very", "even" },
        StringComparer.OrdinalIgnoreCase);

    public static bool IsRelevantToNarration(
        PixabayFootageScene scene, string title, string description)
    {
        var subject = WikimediaCommonsImageService.SubjectSearch(scene.SearchQuery);
        var subjectWords = SubjectWords(subject);
        var searchable = title + " " + description;
        var mediaWords = SubjectWords(searchable);
        if (subjectWords.Length == 0 ||
            subjectWords.Count(mediaWords.Contains) < Math.Min(2, subjectWords.Length))
            return false;

        // The actual spoken content is more trustworthy than a generated search
        // prompt. Require at least one distinctive narration term in Commons
        // metadata, avoiding completely unrelated historical B-roll.
        var narration = SubjectWords(scene.NarrationExcerpt);
        if (narration.Length > 0 &&
            !narration.Any(mediaWords.Contains)) return false;

        // Avoid passing off named historical speakers as footage of a different
        // event. Interview or speech footage is only appropriate when the named
        // person is actually mentioned in narration.
        var titleBody = Regex.Replace(title, @"^File:", "", RegexOptions.IgnoreCase);
        var speaker = Regex.Match(titleBody,
            @"^(?<name>(?:(?:[A-Z]\.\s*)?[A-Z][a-z]+\s+){1,4}[A-Z][a-z]+)\s+" +
            @"(?:Discusses|Recalls|Speaks|Talks|Explains|Testifies|Interviewed)\b");
        if (speaker.Success && !scene.NarrationExcerpt.Contains(
                speaker.Groups["name"].Value, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static string[] SubjectWords(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(m => m.Value)
            .Where(w => w.Length >= 4 && !StopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Available WebM derivatives ordered from reasonable resolution
    /// to smaller bandwidth, with the original only when its size is known to
    /// fit the per-file limit. No oversized file is silently accepted.</summary>
    public static IReadOnlyList<string> SelectVideoUrls(JsonElement info)
    {
        var choices = new List<(string Url, int Width, long Bytes)>();
        if (info.TryGetProperty("derivatives", out var derivatives) &&
            derivatives.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in derivatives.EnumerateArray())
            {
                var url = Read(item, "src");
                var kind = Read(item, "type");
                if (!(kind.StartsWith("video/webm", StringComparison.OrdinalIgnoreCase) ||
                      url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) ||
                    !IsAllowedVideoUrl(url)) continue;
                var size = Long(item, "size");
                if (size > MaxBytes) continue;
                choices.Add((url, Int(item, "width"), size));
            }
        }
        // Try ~480p first, then 360p and 240p. Avoid 1080p downloads merely
        // for a short B-roll excerpt. A large first transcode automatically
        // falls through to the next candidate.
        var sorted = choices.OrderBy(x =>
                x.Width is >= 720 and <= 1024 ? 0 :
                x.Width is >= 450 and < 720 ? 1 :
                x.Width is > 0 and < 450 ? 2 : 3)
            .ThenBy(x => Math.Abs(x.Width - 854))
            .Select(x => x.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        var originalSize = Long(info, "size");
        var originalUrl = Read(info, "url");
        if (originalSize is > 0 and <= MaxBytes &&
            IsAllowedVideoUrl(originalUrl) &&
            !sorted.Contains(originalUrl, StringComparer.OrdinalIgnoreCase))
            sorted.Add(originalUrl);
        return sorted;
    }

    public static string? SelectVideoUrl(JsonElement info) =>
        SelectVideoUrls(info).FirstOrDefault();

    public static bool IsAllowedVideoUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttps &&
           uri.Host.Equals("upload.wikimedia.org",
               StringComparison.OrdinalIgnoreCase) &&
           Regex.IsMatch(uri.AbsolutePath, @"\.(webm|ogv|ogg)$",
               RegexOptions.IgnoreCase);

    private static bool AllowedMime(string mime)
        => mime is "video/webm" or "video/ogg" or "application/ogg";

    private async Task DownloadBoundedAsync(string url, string destination, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long size && size > MaxBytes)
            throw new InvalidDataException("Wikimedia video exceeds the 80 MB per-file download limit.");
        var tmp = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(tmp, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int received;
                while ((received = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += received;
                    if (total > MaxBytes)
                        throw new InvalidDataException("Wikimedia video download exceeded 80 MB.");
                    await output.WriteAsync(buffer.AsMemory(0, received), ct);
                }
            }
            if (new FileInfo(tmp).Length < 4096)
                throw new InvalidDataException("Incomplete Wikimedia video download.");
            File.Move(tmp, destination, true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    private static string Read(JsonElement obj, string property)
        => obj.ValueKind == JsonValueKind.Object &&
           obj.TryGetProperty(property, out var v) &&
           v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static long Long(JsonElement obj, string property)
        => obj.ValueKind == JsonValueKind.Object &&
           obj.TryGetProperty(property, out var v) &&
           v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    private static int Int(JsonElement obj, string property)
        => (int)Math.Clamp(Long(obj, property), 0, int.MaxValue);
    private static string Ext(JsonElement obj, string property)
        => obj.ValueKind == JsonValueKind.Object &&
           obj.TryGetProperty(property, out var v) ? Read(v, "value") : "";
    private static string PlainText(string html)
        => Regex.Replace(WebUtility.HtmlDecode(
            Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();
}
