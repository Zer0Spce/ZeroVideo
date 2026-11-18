using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Game-specific imagery from official publisher-supplied Steam store screenshots.
/// No AI generation, stock footage, community screenshots or unverified websites.
/// Images retain their attribution and source URL. Official does not imply free-to-reuse:
/// any publisher's media policy or applicable editorial exception remains relevant.
/// </summary>
public sealed class OfficialGamingMediaService
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = TimeSpan.FromSeconds(35) };

    public sealed record OfficialAsset(string LocalPath, string Url, string GameTitle,
        string StoreUrl, string Source, string RightsNote);
    public sealed record OfficialMediaResult(string Credit, IReadOnlyList<OfficialAsset> Assets);

    private static bool SteamHost(Uri uri) => uri.Scheme == "https" &&
        (uri.Host == "store.steampowered.com" ||
         uri.Host == "shared.akamai.steamstatic.com" ||
         uri.Host == "shared.fastly.steamstatic.com" ||
         uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host == "steamcdn-a.akamaihd.net");

    // First-party/platform editorial and media hosts. These are sourcing rules,
    // not copyright licenses. Never follow arbitrary links or private addresses.
    private static readonly string[] PlatformHosts =
    [
        "blog.playstation.com", "news.xbox.com", "www.nintendo.com",
        "www.rockstargames.com", "www.ea.com", "www.ubisoft.com",
        "www.bandainamcoent.com", "www.square-enix-games.com",
        "www.capcom-games.com", "www.epicgames.com", "store.epicgames.com",
        "store.steampowered.com", "www.gog.com", "bethesda.net",
        "www.activision.com", "www.callofduty.com", "www.riotgames.com",
        "www.blizzard.com", "www.playstation.com", "www.xbox.com"
    ];

    private static bool PublisherHost(Uri uri) => uri.Scheme == "https" &&
        PlatformHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    private static bool IsPublicHttps(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        !uri.IsLoopback && !Uri.CheckHostName(uri.Host).Equals(UriHostNameType.Unknown) &&
        !IPAddress.TryParse(uri.Host, out _);

    private static async Task<string> DownloadPublisherHtmlAsync(Uri uri, CancellationToken ct)
    {
        if (!PublisherHost(uri)) throw new InvalidDataException("Not an approved publisher URL.");
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 850_000)
            return "";
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.Contains("html", StringComparison.OrdinalIgnoreCase)) return "";
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var block = new byte[32 * 1024];
        int count;
        while ((count = await stream.ReadAsync(block, ct)) > 0)
        {
            if (memory.Length + count > 850_000) return "";
            await memory.WriteAsync(block.AsMemory(0, count), ct);
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    private static IEnumerable<Uri> ExtractPublisherImages(string html, Uri page)
    {
        // OpenGraph/Twitter cards and first-party article illustrations.
        foreach (Match meta in Regex.Matches(html,
                     @"<meta\b[^>]{0,1200}>", RegexOptions.IgnoreCase))
        {
            var tag = meta.Value;
            if (!Regex.IsMatch(tag, @"(?:og:image|twitter:image)", RegexOptions.IgnoreCase))
                continue;
            var value = Regex.Match(tag, @"content\s*=\s*[""'](?<v>[^""']+)[""']",
                RegexOptions.IgnoreCase).Groups["v"].Value;
            value = WebUtility.HtmlDecode(value);
            if (Uri.TryCreate(page, value, out var image) && IsPublicHttps(image))
                yield return image;
        }
        foreach (Match match in Regex.Matches(html,
                     @"<img\b[^>]{0,1800}>", RegexOptions.IgnoreCase).Cast<Match>().Take(80))
        {
            var tag = match.Value;
            var value = Regex.Match(tag,
                @"(?:data-src|src)\s*=\s*[""'](?<v>[^""']+)[""']",
                RegexOptions.IgnoreCase).Groups["v"].Value;
            value = WebUtility.HtmlDecode(value);
            if (Uri.TryCreate(page, value, out var image) && IsPublicHttps(image) &&
                !Regex.IsMatch(image.AbsolutePath,
                    @"(avatar|logo|icon|pixel|sprite|tracking|placeholder)",
                    RegexOptions.IgnoreCase))
                yield return image;
        }
    }

    private static async Task<byte[]> DownloadPublisherImageAsync(
        Uri image, Uri origin, CancellationToken ct)
    {
        // Official publisher pages sometimes serve images through their CDN,
        // often on a different domain. Only take addresses literally found in
        // first-party HTML; reject redirects, local hosts, nonimages and large responses.
        if (!IsPublicHttps(image)) throw new InvalidDataException("Invalid image URL.");
        using var response = await Http.GetAsync(image, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase) &&
            !type.StartsWith("image/png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Official page linked non-JPEG/PNG content.");
        if (response.Content.Headers.ContentLength > 12 * 1024 * 1024)
            throw new InvalidDataException("Official image too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var block = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(block, ct)) > 0)
        {
            if (memory.Length + count > 12 * 1024 * 1024)
                throw new InvalidDataException("Official image too large.");
            await memory.WriteAsync(block.AsMemory(0, count), ct);
        }
        return memory.ToArray();
    }

    private static readonly string[] JournalismHosts =
    [
        "www.pcgamer.com", "www.ign.com", "www.gamespot.com",
        "www.gematsu.com", "www.eurogamer.net", "www.videogameschronicle.com"
    ];

    private static bool JournalismHost(Uri uri) => uri.Scheme == "https" &&
        JournalismHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase));

    private static async Task<string> DownloadNewsHtmlAsync(Uri uri, CancellationToken ct)
    {
        if (!JournalismHost(uri)) return "";
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1_000_000)
            return "";
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.Contains("html", StringComparison.OrdinalIgnoreCase)) return "";
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var block = new byte[32768];
        int n;
        while ((n = await stream.ReadAsync(block, ct)) > 0)
        {
            if (memory.Length + n > 1_000_000) return "";
            await memory.WriteAsync(block.AsMemory(0, n), ct);
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    // Follow only directly attributed official URLs quoted in the original
    // gaming-news article. Never use arbitrary search images.
    private static async Task<List<Uri>> LinkedOfficialPagesAsync(
        VideoProject project, Action<string>? log, CancellationToken ct)
    {
        var results = new List<Uri>();
        foreach (var source in project.Sources.Take(2))
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var article) ||
                !JournalismHost(article)) continue;
            try
            {
                var html = await DownloadNewsHtmlAsync(article, ct);
                foreach (Match match in Regex.Matches(html,
                             @"<a\b[^>]{0,1500}href\s*=\s*[""'](?<u>[^""']+)[""']",
                             RegexOptions.IgnoreCase).Cast<Match>().Take(350))
                {
                    var link = WebUtility.HtmlDecode(match.Groups["u"].Value);
                    if (!Uri.TryCreate(article, link, out var target) || !PublisherHost(target))
                        continue;
                    if (results.All(x => x != target)) results.Add(target);
                    if (results.Count >= 8) break;
                }
                log?.Invoke($"Gaming official source discovery: {results.Count} publisher links from {article.Host}.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke("Original article source links unavailable: " + ex.Message); }
        }
        return results;
    }

    private static IEnumerable<Uri> PublisherPages(VideoProject project)
    {
        var urls = project.Sources;
        foreach (var url in urls)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && PublisherHost(uri))
                yield return uri;
        }
    }

    private static string Normalize(string value) =>
        Regex.Replace(WebUtility.HtmlDecode(value).ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static IEnumerable<string> Queries(VideoProject project, string focus)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(focus) && seen.Add(focus.Trim()))
            yield return focus.Trim();

        // First scene visual queries usually name the exact game. Limit query
        // size to avoid searching Steam for narrative descriptions.
        foreach (var scene in project.Chapters.SelectMany(x => x.Scenes))
        {
            var words = Regex.Matches(scene.VisualQuery ?? "", @"[A-Za-z0-9]+")
                .Select(x => x.Value)
                .Where(x => x.Length >= 2).Take(5).ToArray();
            for (var count = Math.Min(3, words.Length); count >= 2; count--)
            {
                for (var i = 0; i + count <= words.Length; i++)
                {
                    var q = string.Join(" ", words.Skip(i).Take(count));
                    if (seen.Add(q)) yield return q;
                }
            }
        }
        foreach (var candidate in Regex.Split(project.Title, @"[,:—\-!?\(\)]")
                     .Where(x => x.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is >= 2 and <= 5))
            if (seen.Add(candidate.Trim())) yield return candidate.Trim();
    }

    private static async Task<byte[]> GetSmallAsync(Uri uri, int maxBytes, CancellationToken ct)
    {
        if (!SteamHost(uri)) throw new InvalidDataException("Unexpected official-media host.");
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode(); // no redirects to arbitrary hosts
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("Official image exceeds download size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException("Official image exceeds download size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        return buffer.ToArray();
    }

    private static bool IsJpeg(byte[] bytes) =>
        bytes.Length > 2000 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
    private static bool IsPng(byte[] bytes) =>
        bytes.Length > 2000 && bytes[0] == 137 && bytes[1] == 80 &&
        bytes[2] == 78 && bytes[3] == 71;

    public async Task<OfficialMediaResult> AcquireAsync(VideoProject project, string focus,
        string articleSummary, Action<string>? log, CancellationToken ct)
    {
        var context = Normalize(project.Title + " " + project.Topic + " " +
            articleSummary + " " + focus + " " +
            string.Join(" ", project.Chapters.SelectMany(x => x.Scenes)
                .Select(x => x.Narration + " " + x.VisualQuery)));
        var candidates = Queries(project, focus).Take(15).ToList();
        var examined = new HashSet<int>();
        var images = new List<(Uri Uri, string Game, string Store)>();
        // Priority 1: images genuinely hosted/linked by the official publisher
        // or platform article, when the news report itself comes from one.
        var publisherImages = new List<(Uri Image, Uri Source)>();
        var linkedPublishers = await LinkedOfficialPagesAsync(project, log, ct);
        foreach (var sourcePage in PublisherPages(project).Concat(linkedPublishers).Distinct().Take(8))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var html = await DownloadPublisherHtmlAsync(sourcePage, ct);
                if (html.Length < 100) continue;
                foreach (var img in ExtractPublisherImages(html, sourcePage).Take(24))
                {
                    if (publisherImages.Any(x => x.Image == img)) continue;
                    publisherImages.Add((img, sourcePage));
                    if (publisherImages.Count >= 12) break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke("Official publisher imagery unavailable: " + ex.Message); }
        }
        // Priority 2: the publisher's game screenshots on Steam. This
        // fallback only matches actual game titles mentioned in the narration.
        foreach (var term in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (images.Count >= 8) break;
            try
            {
                var searchUri = new Uri("https://store.steampowered.com/api/storesearch/?term=" +
                    Uri.EscapeDataString(term) + "&l=english&cc=us");
                using var search = JsonDocument.Parse(
                    await GetSmallAsync(searchUri, 512 * 1024, ct));
                if (!search.RootElement.TryGetProperty("items", out var list) ||
                    list.ValueKind != JsonValueKind.Array) continue;

                foreach (var item in list.EnumerateArray().Take(5))
                {
                    if (!item.TryGetProperty("id", out var app) ||
                        !app.TryGetInt32(out var appid) || !examined.Add(appid))
                        continue;
                    var game = item.GetProperty("name").GetString() ?? "";
                    var normalizedName = Normalize(game);
                    if (normalizedName.Length < 5 ||
                        !context.Contains(normalizedName, StringComparison.Ordinal))
                        continue;
                    var detailUri = new Uri("https://store.steampowered.com/api/appdetails?appids=" +
                        appid.ToString(CultureInfo.InvariantCulture) + "&l=english&cc=us");
                    using var details = JsonDocument.Parse(
                        await GetSmallAsync(detailUri, 1024 * 1024, ct));
                    if (!details.RootElement.TryGetProperty(appid.ToString(CultureInfo.InvariantCulture),
                        out var record) || !record.TryGetProperty("success", out var success) ||
                        !success.GetBoolean() || !record.TryGetProperty("data", out var data) ||
                        !data.TryGetProperty("screenshots", out var screenshots) ||
                        screenshots.ValueKind != JsonValueKind.Array)
                        continue;
                    var canonicalName = data.TryGetProperty("name", out var title)
                        ? title.GetString() ?? game : game;
                    // Verify that the exact store title really occurs in the
                    // story/script, not a coincidental two-word Steam result.
                    if (!context.Contains(Normalize(canonicalName), StringComparison.Ordinal))
                        continue;
                    foreach (var screenshot in screenshots.EnumerateArray().Take(10))
                    {
                        var url = screenshot.TryGetProperty("path_full", out var full)
                            ? full.GetString() : null;
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var imageUri) ||
                            !SteamHost(imageUri)) continue;
                        images.Add((imageUri, canonicalName,
                            "https://store.steampowered.com/app/" + appid));
                        if (images.Count >= 8) break;
                    }
                    if (images.Count >= 8) break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke("Gaming official-media search skipped: " + ex.Message); }
        }
        // Priority 3: the source article's own editorial/game imagery.
        // This is not described as a first-party official screenshot; label
        // its actual publication in the credit manifest and on-screen source.
        // Prefer this over generating an invented picture for news that does
        // not have a Steam listing or a linked publisher newsroom.
        var editorialImages = new List<(Uri Image, Uri Source)>();
        if (publisherImages.Count == 0 && images.Count == 0)
        {
            foreach (var rawSource in project.Sources.Take(2))
            {
                if (!Uri.TryCreate(rawSource, UriKind.Absolute, out var story) ||
                    !JournalismHost(story)) continue;
                try
                {
                    var html = await DownloadNewsHtmlAsync(story, ct);
                    foreach (var candidate in ExtractPublisherImages(html, story).Take(12))
                    {
                        if (editorialImages.All(x => x.Image != candidate))
                            editorialImages.Add((candidate, story));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log?.Invoke("Editorial article images unavailable: " + ex.Message); }
            }
        }
        if (images.Count == 0 && publisherImages.Count == 0 && editorialImages.Count == 0)
            throw new InvalidDataException(
                "No matched first-party publisher/newsroom or Steam screenshots found. " +
                "Gaming News will NOT generate AI pictures, use random stock, or publish an empty backdrop.");

        var mediaDir = Path.Combine(project.ProjectDirectory, "media");
        Directory.CreateDirectory(mediaDir);
        var downloaded = new List<OfficialAsset>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (imageUri, sourcePage) in publisherImages)
        {
            ct.ThrowIfCancellationRequested();
            if (downloaded.Count >= 8) break;
            if (!unique.Add(imageUri.ToString())) continue;
            try
            {
                var bytes = await DownloadPublisherImageAsync(imageUri, sourcePage, ct);
                if (!IsJpeg(bytes) && !IsPng(bytes)) continue;
                var ext = IsJpeg(bytes) ? ".jpg" : ".png";
                var file = Path.Combine(mediaDir, "official-" +
                    (downloaded.Count + 1).ToString("00") + ext);
                await File.WriteAllBytesAsync(file, bytes, ct);
                downloaded.Add(new OfficialAsset(file, imageUri.ToString(), project.Title,
                    sourcePage.ToString(), sourcePage.Host + " official article image",
                    "Platform or publisher owns or controls media; credit is not a license."));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke("First-party image skipped: " + ex.Message); }
        }
        // A story's lead image is often a real publisher press screenshot.
        // Keep its attribution exact instead of pretending it is official.
        if (downloaded.Count == 0)
        foreach (var (imageUri, story) in editorialImages)
        {
            ct.ThrowIfCancellationRequested();
            if (downloaded.Count >= 8) break;
            if (!unique.Add(imageUri.ToString())) continue;
            try
            {
                var bytes = await DownloadPublisherImageAsync(imageUri, story, ct);
                if (!IsJpeg(bytes) && !IsPng(bytes)) continue;
                var file = Path.Combine(mediaDir, "official-" +
                    (downloaded.Count + 1).ToString("00") +
                    (IsJpeg(bytes) ? ".jpg" : ".png"));
                await File.WriteAllBytesAsync(file, bytes, ct);
                downloaded.Add(new OfficialAsset(file, imageUri.ToString(), project.Title,
                    story.ToString(), story.Host + " editorial article image",
                    "Image sourced from credited article; license/press usage terms may apply."));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke("Editorial image skipped: " + ex.Message); }
        }
        // Steam is a fallback, and never used for a mismatched game title.
        foreach (var (uri, game, store) in images)
        {
            if (downloaded.Count >= 8) break;
            if (!unique.Add(uri.ToString())) continue;
            try
            {
                var bytes = await GetSmallAsync(uri, 12 * 1024 * 1024, ct);
                if (!IsJpeg(bytes) && !IsPng(bytes)) continue;
                var extension = IsJpeg(bytes) ? ".jpg" : ".png";
                var file = Path.Combine(mediaDir, "official-" + (downloaded.Count + 1).ToString("00") + extension);
                await File.WriteAllBytesAsync(file, bytes, ct);
                downloaded.Add(new OfficialAsset(file, uri.ToString(), game, store,
                    "Publisher-supplied Steam store screenshot",
                    "Publisher owns or controls media. Source credit does not grant permission; " +
                    "republication depends on relevant publisher permissions or applicable editorial use."));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke("Official screenshot download skipped: " + ex.Message); }
        }
        if (downloaded.Count == 0)
            throw new InvalidDataException("No valid original official media could be downloaded.");

        var chapter = project.Chapters.Single();
        for (var i = 0; i < chapter.Scenes.Count; i++)
        {
            // Prefer distinct real game screenshots, then cycle only if the
            // official publisher exposes fewer than eight images.
            var photo = downloaded[i % downloaded.Count];
            chapter.Scenes[i].MediaPath = photo.LocalPath;
            chapter.Scenes[i].MediaPaths = [];
        }
        var sourceNames = downloaded
            .Select(x => x.Source.Contains("Steam", StringComparison.OrdinalIgnoreCase)
                ? "Steam / " + x.GameTitle : x.Source.Replace(" official article image", ""))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3);
        var label = "Source: " + string.Join(" / ", sourceNames);
        var manifest = Path.Combine(project.ProjectDirectory, "official-media-credits.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new
        {
            project = project.Title, credit = label,
            legalNote = "Attribution is not a license. Confirm publisher's use rules or applicable news/editorial exception.",
            assets = downloaded
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        log?.Invoke($"Gaming News: {downloaded.Count} publisher-supplied official screenshots. " +
            "No ComfyUI/AI images will run. Manifest: " + manifest);
        return new OfficialMediaResult(label, downloaded);
    }

    public async Task BurnSmallCreditAsync(string video, string credit,
        ZeroVideoSettings settings, ProcessRunner runner, Action<string>? log, CancellationToken ct)
    {
        var text = Regex.Replace(credit, @"[^A-Za-z0-9 ./_-]", "").Trim();
        if (text.Length > 68) text = text[..68];
        if (text.Length == 0) throw new InvalidDataException("Official image credit missing.");
        var tmp = Path.Combine(Path.GetDirectoryName(video)!, "official-credit-render.mp4");
        var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf")
            .Replace('\\', '/').Replace(":", "\\:");
        var filter = "drawtext=fontfile='" + font + "':text='" + text +
                     "':fontsize=24:fontcolor=white@0.88:box=1:boxcolor=black@0.48:" +
                     "boxborderw=8:x=24:y=32";
        var args = "-hide_banner -loglevel error -y -i \"" + video +
            "\" -vf \"" + filter + "\" -map 0:v:0 -map 0:a:0 " +
            "-c:v libx264 -preset veryfast -crf 20 -c:a copy -movflags +faststart \"" + tmp + "\"";
        var result = await runner.RunAsync(settings.FfmpegPath, args, ct: ct);
        if (result.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length < 8192)
            throw new InvalidOperationException("Could not burn official media credit: " + result.StdErr);
        File.Move(tmp, video, true);
        log?.Invoke("Small on-screen official source credit burned into MP4.");
    }
    /// <summary>
    /// Only Gaming News uses this final MP4 timing pass. Keep a visible first
    /// frame before speech, hold the last frame after the final word, and delay
    /// existing captions so they remain aligned with the narration.
    /// </summary>
    public async Task AddNarrationBreathingRoomAsync(
        string video, ZeroVideoSettings settings, ProcessRunner runner,
        Action<string>? log, CancellationToken ct)
    {
        const double leadSeconds = 1.5;
        const double tailSeconds = 1.5;
        if (!File.Exists(video))
            throw new FileNotFoundException("Gaming News MP4 missing.", video);

        var output = Path.Combine(Path.GetDirectoryName(video)!,
            "gaming-news-padded-" + Guid.NewGuid().ToString("N") + ".mp4");
        var args = "-hide_banner -loglevel error -y -i \"" + video +
            "\" -filter_complex \"[0:v]tpad=start_mode=clone:stop_mode=clone:" +
            "start_duration=1.5:stop_duration=1.5,format=yuv420p[v];" +
            "[0:a]adelay=1500:all=1,apad=pad_dur=1.5[a]\"" +
            " -map \"[v]\" -map \"[a]\" -c:v libx264 -preset veryfast -crf 20" +
            " -c:a aac -b:a 192k -movflags +faststart -shortest \"" + output + "\"";
        try
        {
            var result = await runner.RunAsync(settings.FfmpegPath, args, ct: ct);
            if (result.ExitCode != 0 || !File.Exists(output) ||
                new FileInfo(output).Length < 8192)
                throw new InvalidOperationException(
                    "Could not add Gaming News narration padding: " + result.StdErr);

            // Retain the SRT for editors and keep it synchronized after the
            // 1.5 second silent video lead-in. The burnt-in captions themselves
            // are shifted together with the original first frame by tpad.
            var srt = Path.ChangeExtension(video, ".srt");
            if (File.Exists(srt))
            {
                var original = await File.ReadAllTextAsync(srt, ct);
                var shifted = System.Text.RegularExpressions.Regex.Replace(
                    original,
                    @"(?<start>\d{2}:\d{2}:\d{2},\d{3})\s*-->\s*(?<end>\d{2}:\d{2}:\d{2},\d{3})",
                    m =>
                    {
                        static string Add(string stamp)
                        {
                            var value = TimeSpan.ParseExact(stamp, @"hh\:mm\:ss\,fff",
                                CultureInfo.InvariantCulture).Add(TimeSpan.FromMilliseconds(1500));
                            return value.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
                        }
                        return Add(m.Groups["start"].Value) + " --> " +
                            Add(m.Groups["end"].Value);
                    });
                await File.WriteAllTextAsync(srt, shifted, ct);
            }
            File.Move(output, video, true);
            log?.Invoke("Gaming News timing: 1.5s visual lead-in before voiceover " +
                "and 1.5s last-frame hold after voiceover. Captions remain aligned.");
        }
        finally
        {
            if (File.Exists(output))
                try { File.Delete(output); } catch { }
        }
    }

}
