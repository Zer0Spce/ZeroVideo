using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZeroVideo.App.Services;

/// <summary>
/// A small, user-requested Wikimedia Commons image fallback for documentary
/// scenes. Only clearly free commercial-use licenses are accepted. Per-file
/// credits are saved for the video's publishing description.
/// </summary>
public sealed class WikimediaCommonsImageService
{
    private const int MaxBytes = 25 * 1024 * 1024;
    private static readonly HttpClient Shared = MakeClient();
    private readonly HttpClient _http;
    private readonly string _storage;
    private readonly HashSet<long> _used = [];
    private int _requests;

    public WikimediaCommonsImageService(HttpClient? http = null, string? dataDirectory = null)
    {
        _http = http ?? Shared;
        _storage = Path.Combine(dataDirectory ?? AppPaths.DataDirectory, "WikimediaCommons");
    }

    private static HttpClient MakeClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        // Wikimedia requires an identifiable user-agent with a contact URL.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ZeroVideo/0.1 (+https://github.com/Zer0Spce/ZeroVideo)");
        return client;
    }

    private static readonly HashSet<string> MoodWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "dark", "scary", "creepy", "ominous", "haunted", "foggy", "shadowy",
        "cinematic", "dramatic", "somber", "upbeat", "tense", "calm",
        "night", "nighttime", "spooky", "eerie", "mysterious", "stock"
    };

    public static string SubjectSearch(string query)
    {
        var terms = Regex.Matches(query, @"[\p{L}\p{N}]+")
            .Select(m => m.Value).Where(w => !MoodWords.Contains(w)).Take(5);
        return string.Join(" ", terms);
    }

    public static bool IsCommerciallyReusableLicense(
        string shortName, string licenseUrl, string artist)
    {
        var name = Regex.Replace(shortName.Trim().ToLowerInvariant(), @"\s+", " ");
        if (name is "cc0" or "cc0 1.0" or "public domain" or "public domain mark" or "pd")
            return true;
        // ShareAlike and non-commercial licenses are intentionally excluded;
        // the app cannot ensure a customer's final video meets their conditions.
        if (!Regex.IsMatch(name, @"^cc by( (2\.0|2\.5|3\.0|4\.0))?$"))
            return false;
        return !string.IsNullOrWhiteSpace(artist) &&
            licenseUrl.StartsWith("https://creativecommons.org/licenses/by/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<PixabayFlowClip>> FindImagesAsync(
        PixabayFootageScene scene, int maxImages, Action<string>? log, CancellationToken ct)
    {
        var matches = new List<PixabayFlowClip>();
        var subject = SubjectSearch(scene.SearchQuery);
        if (maxImages <= 0 || subject.Length < 3 || _requests >= 24) return matches;
        var cacheDir = Path.Combine(_storage, "ApiCache");
        var filesDir = Path.Combine(_storage, "Files");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(filesDir);
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(subject.ToLowerInvariant())));
        var cache = Path.Combine(cacheDir, hash + ".json");
        var json = "";
        if (File.Exists(cache) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromHours(24))
        {
            json = await File.ReadAllTextAsync(cache, ct);
        }
        else
        {
            _requests++;
            var args = new Dictionary<string, string> {
                ["action"] = "query", ["generator"] = "search",
                ["gsrsearch"] = subject,
                ["gsrnamespace"] = "6", ["gsrlimit"] = "8",
                ["prop"] = "imageinfo", ["iiprop"] = "url|extmetadata|mime",
                ["iiurlwidth"] = "1920", ["format"] = "json", ["formatversion"] = "2",
                ["maxlag"] = "5"
            };
            var url = "https://commons.wikimedia.org/w/api.php?" +
                string.Join("&", args.Select(x =>
                    Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                log?.Invoke($"Wikimedia Commons temporarily limited request for '{subject}'. Skipping fallback without repeated requests.");
                return matches;
            }
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync(ct);
            await File.WriteAllTextAsync(cache, json, ct);
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var root) ||
            !root.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Array) return matches;
        var terms = SubjectSearch(scene.SearchQuery).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var item in pages.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (matches.Count >= Math.Min(maxImages, 3)) break;
            if (!item.TryGetProperty("pageid", out var pageIdNode) ||
                !pageIdNode.TryGetInt64(out var pageId) || !_used.Add(pageId))
                continue;
            if (!item.TryGetProperty("imageinfo", out var imageInfo) ||
                imageInfo.ValueKind != JsonValueKind.Array ||
                imageInfo.GetArrayLength() == 0) continue;
            var info = imageInfo[0];
            var mime = GetString(info, "mime");
            if (mime is not ("image/jpeg" or "image/png" or "image/webp"))
                continue;
            var title = GetString(item, "title");
            var meta = info.TryGetProperty("extmetadata", out var md) ? md : default;
            var license = Meta(meta, "LicenseShortName");
            var licenseUrl = Meta(meta, "LicenseUrl");
            var artist = PlainText(Meta(meta, "Artist"));
            var description = PlainText(Meta(meta, "ImageDescription"));
            var restrictions = PlainText(Meta(meta, "Restrictions"));
            if (!string.IsNullOrWhiteSpace(restrictions) ||
                !IsCommerciallyReusableLicense(license, licenseUrl, artist))
                continue;
            var searchable = title + " " + description;
            // Two subject matches are a safer minimum for documentary
            // relevance than accepting any one generic term (e.g. "TV").
            var overlap = terms.Count(t => t.Length >= 3 &&
                Regex.IsMatch(searchable, @"\b" + Regex.Escape(t) + @"\b",
                    RegexOptions.IgnoreCase));
            if (overlap < Math.Min(2, terms.Length) ||
                !IsMoodCompatible(searchable, scene.Mood))
                continue;
            var url = GetString(info, "thumburl");
            if (!IsWikimediaImageUrl(url)) url = GetString(info, "url");
            if (!IsWikimediaImageUrl(url)) continue;
            var extension = mime switch {
                "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg"
            };
            var file = Path.Combine(filesDir, pageId + extension);
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length < 1024)
                    await DownloadImageAsync(url, file, ct);
                var source = "https://commons.wikimedia.org/wiki/" +
                    Uri.EscapeDataString(title.Replace(' ', '_'));
                var credit = string.IsNullOrWhiteSpace(artist) ? "Wikimedia Commons"
                    : artist;
                matches.Add(new PixabayFlowClip(-pageId, file, source,
                    scene.SearchQuery, searchable, true, credit, license, licenseUrl));
                log?.Invoke($"Wikimedia Commons: free-licensed image for scene {scene.SceneNumber}: {title} ({license}).");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log?.Invoke($"Wikimedia Commons image skipped for scene {scene.SceneNumber}: {ex.Message}");
            }
        }
        return matches;
    }

    public static bool IsMoodCompatible(string searchable, string mood)
    {
        if (mood.Equals("ominous", StringComparison.OrdinalIgnoreCase) ||
            mood.Equals("tense", StringComparison.OrdinalIgnoreCase) ||
            mood.Equals("somber", StringComparison.OrdinalIgnoreCase))
            return !Regex.IsMatch(searchable,
                @"\b(sunny|cheerful|party|vacation|holiday|celebration|smiling|happy|joyful|colorful|bright)\b",
                RegexOptions.IgnoreCase);
        if (mood.Equals("upbeat", StringComparison.OrdinalIgnoreCase))
            return !Regex.IsMatch(searchable,
                @"\b(horror|corpse|funeral|death|nightmare|haunted|ghoulish|terrifying)\b",
                RegexOptions.IgnoreCase);
        return true;
    }

    private async Task DownloadImageAsync(string url, string file, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > MaxBytes)
            throw new InvalidDataException("Wikimedia image is too large.");
        var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(tmp, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024];
                var total = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += n;
                    if (total > MaxBytes) throw new InvalidDataException("Wikimedia image exceeded 25 MB.");
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            if (new FileInfo(tmp).Length < 1024)
                throw new InvalidDataException("Incomplete Wikimedia image response.");
            File.Move(tmp, file, true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    private static string GetString(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object &&
           obj.TryGetProperty(key, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Meta(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object &&
           obj.TryGetProperty(key, out var entry) ? GetString(entry, "value") : "";

    private static string PlainText(string value)
    {
        var cleaned = Regex.Replace(value, "<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(cleaned), @"\s+", " ").Trim();
    }

    private static bool IsWikimediaImageUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttps &&
           (uri.Host.Equals("upload.wikimedia.org", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".wikimedia.org", StringComparison.OrdinalIgnoreCase)) &&
           Regex.IsMatch(uri.AbsolutePath, @"\.(png|jpg|jpeg|webp)$",
               RegexOptions.IgnoreCase);
}
