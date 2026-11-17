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
            var overlap = terms.Count(term => term.Length >= 3 &&
                Regex.IsMatch(searchable, @"\b" + Regex.Escape(term) + @"\b",
                    RegexOptions.IgnoreCase));
            if (overlap < Math.Min(2, terms.Length) ||
                !WikimediaCommonsImageService.IsMoodCompatible(searchable, scene.Mood) ||
                PixabayVideoService.IsChromaKeyStockMetadata(searchable))
                continue;
            var url = SelectVideoUrl(info);
            if (url is null) continue;
            var extension = url.EndsWith(".ogv", StringComparison.OrdinalIgnoreCase) ||
                            url.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                ? ".ogv" : ".webm";
            var local = Path.Combine(videoDir, pageId + extension);
            try
            {
                if (!File.Exists(local) || new FileInfo(local).Length < 4096)
                    await DownloadBoundedAsync(url, local, ct);
                if (chromaScreenCheck is not null &&
                    await chromaScreenCheck(local, ct))
                {
                    log?.Invoke($"Wikimedia video {pageId} excluded: chroma screen detected.");
                    continue;
                }
                _selected.Add(pageId);
                results.Add(new PixabayFlowClip(-1_000_000_000L - pageId,
                    local, "https://commons.wikimedia.org/wiki/" +
                           Uri.EscapeDataString(title.Replace(' ', '_')),
                    scene.SearchQuery, searchable, false,
                    author, license, licenseUrl, "Wikimedia Commons"));
                log?.Invoke($"Wikimedia video: scene {scene.SceneNumber}, '{title}' ({license}).");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log?.Invoke($"Wikimedia video {pageId} skipped: {ex.Message}");
            }
        }
        return results;
    }

    public static string? SelectVideoUrl(JsonElement info)
    {
        // Prefer Wikimedia's compressed WebM derivatives to downloading
        // potentially multi-gigabyte original videos.
        if (info.TryGetProperty("derivatives", out var derivatives) &&
            derivatives.ValueKind == JsonValueKind.Array)
        {
            var matches = derivatives.EnumerateArray()
                .Select(v => new
                {
                    Url = Read(v, "src"),
                    Type = Read(v, "type"),
                    Width = Int(v, "width")
                })
                .Where(x => (x.Type.StartsWith("video/webm",
                              StringComparison.OrdinalIgnoreCase) ||
                             x.Url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) &&
                            IsAllowedVideoUrl(x.Url))
                .OrderBy(x => x.Width > 0 && x.Width <= 1280 ? 0 : 1)
                .ThenBy(x => Math.Abs(x.Width - 854))
                .ToList();
            if (matches.Count > 0) return matches[0].Url;
        }
        var size = Long(info, "size");
        var url = Read(info, "url");
        return size is > 0 and <= MaxBytes && IsAllowedVideoUrl(url) ? url : null;
    }

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
