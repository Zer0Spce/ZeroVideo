using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Windows.Media.Imaging;
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
    private static readonly HttpClient Http = CreateMediaClient();

    private static HttpClient CreateMediaClient()
    {
        var http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = TimeSpan.FromSeconds(35) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "ZeroVideo/0.1 (+https://github.com/Zer0Spce/ZeroVideo)");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html, application/json, application/rss+xml, image/*");
        return http;
    }

    public sealed record OfficialAsset(string LocalPath, string Url, string GameTitle,
        string StoreUrl, string Source, string RightsNote);
    public sealed record OfficialMediaResult(string Credit, IReadOnlyList<OfficialAsset> Assets);

    private sealed class MediaUse
    {
        public string Fingerprint { get; set; } = "";
        public string Url { get; set; } = "";
        public DateTime UsedUtc { get; set; }
        public string VideoId { get; set; } = "";
    }

    private static string MediaHistoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeroVideo", "gaming-news-media-history.json");

    private static List<MediaUse> ReadMediaHistory(Action<string>? log)
    {
        try
        {
            if (!File.Exists(MediaHistoryPath)) return [];
            return JsonSerializer.Deserialize<List<MediaUse>>(
                File.ReadAllText(MediaHistoryPath)) ?? [];
        }
        catch (Exception ex)
        {
            log?.Invoke("Gaming News media history could not be read: " + ex.Message);
            return [];
        }
    }

    private static async Task SaveMediaHistoryAsync(
        List<MediaUse> entries, Action<string>? log, CancellationToken ct)
    {
        try
        {
            var path = MediaHistoryPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var now = DateTime.UtcNow;
            var recent = entries.Where(x => x.UsedUtc > now.AddDays(-14))
                .TakeLast(2500).ToList();
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(recent), ct);
            File.Move(temp, path, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke("Gaming News media use history unavailable: " + ex.Message);
        }
    }

    private static string MediaSourceHost(OfficialAsset asset)
    {
        if (Uri.TryCreate(asset.StoreUrl, UriKind.Absolute, out var src))
            return src.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        return asset.Source;
    }

    private static bool IsVideoAsset(OfficialAsset asset) =>
        new[] { ".mp4", ".webm", ".mov", ".mkv", ".avi" }.Contains(
            Path.GetExtension(asset.LocalPath), StringComparer.OrdinalIgnoreCase);

    private static List<OfficialAsset> SelectDiverseShots(
        VideoProject project, IReadOnlyList<OfficialAsset> assets,
        IReadOnlyList<MediaUse> history, Action<string>? log)
    {
        const int target = 24;
        var seedBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            project.Id + "|" + project.Title + "|" + string.Join("|", project.Sources)));
        var rng = new Random(BitConverter.ToInt32(seedBytes, 0));
        var recentlyUsed = history.TakeLast(300)
            .GroupBy(x => x.Fingerprint)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var keyed = assets.Select(a => new
        {
            Asset = a,
            Fingerprint = MediaFingerprint(a.LocalPath),
            Host = MediaSourceHost(a)
        }).ToList();
        var pool = keyed.GroupBy(x => x.Fingerprint).Select(x => x.First()).ToList();
        var hosts = pool.Select(x => x.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // One real publisher does not satisfy a multi-source news workflow.
        // Never falsely describe 24 reordered Rockstar images as diverse.
        if (hosts.Count < 2)
            throw new InvalidDataException(
                "Only one usable source website (" + (hosts.Count == 1 ? hosts[0] : "none") +
                ") supplied unique high-resolution images. Gaming News requires " +
                "at least two genuine source sites; it will not publish another " +
                "Rockstar-only slideshow. Check the GameSpot/Gematsu gallery diagnostics.");
        var goalHosts = Math.Min(4, hosts.Count);
        var maximumPerHost = hosts.Count >= 3 ? 12 : 15;
        var storyKeywords = Regex.Matches(project.Title.ToLowerInvariant(), @"[a-z]{4,}")
            .Select(x => x.Value)
            .Where(x => !new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "grand", "theft", "auto", "game", "gaming", "news",
                "guide", "every", "about", "with", "from", "your",
                "what", "this", "that", "actually", "works", "latest"
            }.Contains(x))
            .Distinct().Take(8).ToArray();
        var chosen = new List<OfficialAsset>();
        var chosenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perSource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shotHosts = new List<string>();
        int videoShots = 0;

        for (var index = 0; index < target; index++)
        {
            var preferVideo = index % 5 == 2 || index % 7 == 4;
            var uniqueFirst = chosenHashes.Count < pool.Count;
            var choices = pool.Where(x => !uniqueFirst || !chosenHashes.Contains(x.Fingerprint))
                .ToList();
            if (choices.Count == 0) choices = pool.ToList();
            if (hosts.Count >= 2)
            {
                var balanced = choices.Where(x =>
                    perSource.GetValueOrDefault(x.Host) < maximumPerHost).ToList();
                if (balanced.Count > 0) choices = balanced;
            }
            // Video and screenshot mix when genuine footage exists; never
            // substitute unrelated videos merely to meet a quota.
            if (preferVideo && choices.Any(x => IsVideoAsset(x.Asset)))
                choices = choices.Where(x => IsVideoAsset(x.Asset)).ToList();
            else if (!preferVideo && choices.Any(x => !IsVideoAsset(x.Asset)))
                choices = choices.Where(x => !IsVideoAsset(x.Asset)).ToList();

            var missingHosts = hosts.Count >= 2 ? hosts
                .Where(h => !perSource.ContainsKey(h)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scored = choices.Select(x =>
            {
                var used = perSource.GetValueOrDefault(x.Host);
                var consecutive = shotHosts.Count > 0 &&
                    shotHosts[^1].Equals(x.Host, StringComparison.OrdinalIgnoreCase);
                var repeatedTwice = shotHosts.Count >= 2 && consecutive &&
                    shotHosts[^2].Equals(x.Host, StringComparison.OrdinalIgnoreCase);
                var recent = recentlyUsed.GetValueOrDefault(x.Fingerprint);
                var metadata = (x.Asset.Url + " " + x.Asset.StoreUrl).ToLowerInvariant();
                var topicBonus = Math.Min(3.0,
                    storyKeywords.Count(t => metadata.Contains(t, StringComparison.Ordinal)) * 1.2);
                var share = (used + 1.0) / (index + 1.0);
                var score = rng.NextDouble() * 2.4
                    + topicBonus
                    + (missingHosts.Contains(x.Host) ? 5.0 : 0.0)
                    + (chosenHashes.Contains(x.Fingerprint) ? -4.0 : 3.0)
                    - recent * 2.8
                    - used * 0.85
                    - (share > .35 && hosts.Count >= 3 ? 7.0 : 0.0)
                    - (consecutive ? 5.0 : 0.0)
                    - (repeatedTwice ? 40.0 : 0.0);
                return new { Candidate = x, Score = score };
            }).OrderByDescending(x => x.Score).ToList();
            if (scored.Count == 0) break;
            var pick = scored[0].Candidate;
            chosen.Add(pick.Asset);
            chosenHashes.Add(pick.Fingerprint);
            perSource[pick.Host] = perSource.GetValueOrDefault(pick.Host) + 1;
            shotHosts.Add(pick.Host);
            if (IsVideoAsset(pick.Asset)) videoShots++;
        }

        if (perSource.Count < 2)
            throw new InvalidDataException(
                "Diversified shot selection ended with one source. " +
                "No new video was published; inspect the media-source diagnostics.");
        log?.Invoke("Gaming News diversified selection: " +
            chosen.Count + " shots, " + perSource.Count + " source sites (" +
            string.Join(", ", perSource.Select(x => x.Key + "=" + x.Value)) +
            "), " + videoShots + " video beats; " +
            "recent assets deprioritized across batches.");
        if (perSource.Count < goalHosts)
            log?.Invoke("Gaming News: some source sites had no usable unique assets; " +
                "did not fabricate sources to meet diversity target.");
        return chosen;
    }

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
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 3_000_000)
            return "";
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.Contains("html", StringComparison.OrdinalIgnoreCase)) return "";
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var block = new byte[32 * 1024];
        int count;
        while ((count = await stream.ReadAsync(block, ct)) > 0)
        {
            if (memory.Length + count > 3_000_000) return "";
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

    private static IEnumerable<(Uri Url, string Alt)> ExtractDescribedArticleImages(
        string html, Uri article)
    {
        // Journalism sites often use CDN hashes, so GTA VI does not occur in
        // the URL. Accept first-party article <img> whose own alt/title
        // identifies GTA VI, but never infer identity merely from page URL.
        foreach (Match match in Regex.Matches(html,
                     @"<img\b[^>]{0,2500}>", RegexOptions.IgnoreCase).Cast<Match>().Take(150))
        {
            var tag = match.Value;
            var alt = Regex.Match(tag,
                @"(?:alt|title)\s*=\s*[""'](?<v>[^""']+)[""']",
                RegexOptions.IgnoreCase).Groups["v"].Value;
            alt = WebUtility.HtmlDecode(alt);
            if (alt.Length < 4) continue;
            var src = Regex.Match(tag,
                @"(?:data-src|src)\s*=\s*[""'](?<v>[^""']+)[""']",
                RegexOptions.IgnoreCase).Groups["v"].Value;
            if (!Uri.TryCreate(article, WebUtility.HtmlDecode(src), out var image) ||
                !IsPublicHttps(image) || RejectGenericGraphic(image, alt))
                continue;
            yield return (image, alt);
        }
    }

    private static IEnumerable<Uri> ExtractLinkedGalleryImages(string html, Uri page)
    {
        // Rockstar and other publisher galleries often put the full-res JPG
        // in an <a href>, not in <img src>; the original extractor missed all
        // these actual screenshots and returned only thumbnails/branding.
        foreach (Match match in Regex.Matches(html,
            @"<a\b[^>]{0,1800}href\s*=\s*[""'](?<url>[^""']+)[""']",
            RegexOptions.IgnoreCase).Cast<Match>().Take(400))
        {
            var raw = WebUtility.HtmlDecode(match.Groups["url"].Value);
            if (!Uri.TryCreate(page, raw, out var image) ||
                !IsPublicHttps(image) ||
                !Regex.IsMatch(image.AbsolutePath, @"(?i)\.(?:jpg|jpeg|png)$"))
                continue;
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
        "www.gematsu.com", "www.eurogamer.net", "www.videogameschronicle.com",
        "www.polygon.com", "www.theverge.com", "kotaku.com",
        "www.destructoid.com", "www.rockpapershotgun.com",
        "www.gamesradar.com", "www.pushsquare.com", "www.nintendolife.com",
        "www.purexbox.com", "www.digitaltrends.com", "www.techradar.com",
        "www.gamespress.com", "gamespress.com"
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
        if (IsGtaSixStory(project.Title, ""))
        {
            // Exact publisher-hosted GTA VI galleries. These already contain
            // dozens of actual screenshots, including Jason and Lucia.
            yield return new Uri("https://www.rockstargames.com/VI/media/screenshots");
            yield return new Uri("https://www.rockstargames.com/VI/media");
            yield return new Uri("https://www.rockstargames.com/VI/launch-gallery");
        }
        var urls = project.Sources;
        foreach (var url in urls)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && PublisherHost(uri))
                yield return uri;
        }
    }

    // Search outside the original article. Bing's public RSS results are
    // discovery only; selected pages MUST be from an allowlisted first-party
    // publisher/platform host and are still checked for real media.
    private static async Task<IReadOnlyList<Uri>> DiscoverIndependentPublisherPagesAsync(
        VideoProject project, string focus, Action<string>? log, CancellationToken ct)
    {
        var pages = new List<Uri>();
        var subject = CoreGameSubject(project, focus);
        if (subject.Length > 120) subject = subject[..120];
        if (subject.Length < 4) return pages;
        var searches = new[]
        {
            "\"" + subject + "\" official screenshots game publisher",
            "\"" + subject + "\" gameplay trailer press kit official"
        };
        foreach (var query in searches)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var uri = new Uri("https://www.bing.com/search?format=rss&q=" +
                    Uri.EscapeDataString(query));
                using var response = await Http.GetAsync(
                    uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode ||
                    response.Content.Headers.ContentLength > 400_000) continue;
                var xml = await response.Content.ReadAsStringAsync(ct);
                if (xml.Length > 400_000) continue;
                var document = XDocument.Parse(xml, LoadOptions.None);
                foreach (var item in document.Descendants()
                             .Where(x => x.Name.LocalName == "item").Take(20))
                {
                    var link = item.Elements().FirstOrDefault(
                        x => x.Name.LocalName == "link")?.Value ?? "";
                    if (!Uri.TryCreate(link, UriKind.Absolute, out var page) ||
                        !PublisherHost(page)) continue;
                    if (pages.All(x => x != page)) pages.Add(page);
                    if (pages.Count >= 8) break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log?.Invoke("Independent publisher search unavailable: " + ex.Message);
            }
        }
        log?.Invoke($"Independent web search: {pages.Count} official publisher/platform pages.");
        return pages;
    }

    // Broader public search across relevant gaming publishers and journalism
    // sites. An RSS hit is only a candidate page: never use generic search
    // result thumbnails or unverified third-party embeds as footage.
    private static async Task<IReadOnlyList<Uri>> DiscoverGamingNewsMediaPagesAsync(
        VideoProject project, string focus, Action<string>? log, CancellationToken ct)
    {
        var pages = new List<Uri>();
        var subject = CoreGameSubject(project, focus);
        if (subject.Length > 110) subject = subject[..110];
        if (subject.Length < 4) return pages;
        var searches = new[]
        {
            "\"" + subject + "\" site:ign.com gameplay screenshots",
            "\"" + subject + "\" site:gamespot.com game footage",
            "\"" + subject + "\" site:gamespress.com press kit screenshots trailer",
            "\"" + subject + "\" site:gematsu.com screenshots trailers",
            "\"" + subject + "\" gaming news screenshots"
        };
        foreach (var query in searches)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await Http.GetAsync(
                    new Uri("https://www.bing.com/search?format=rss&q=" +
                        Uri.EscapeDataString(query)),
                    HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode ||
                    response.Content.Headers.ContentLength > 400_000) continue;
                var xml = await response.Content.ReadAsStringAsync(ct);
                if (xml.Length > 400_000) continue;
                var document = XDocument.Parse(xml, LoadOptions.None);
                foreach (var item in document.Descendants()
                    .Where(x => x.Name.LocalName == "item").Take(30))
                {
                    var link = item.Elements().FirstOrDefault(
                        x => x.Name.LocalName == "link")?.Value ?? "";
                    var title = item.Elements().FirstOrDefault(
                        x => x.Name.LocalName == "title")?.Value ?? "";
                    var summary = item.Elements().FirstOrDefault(
                        x => x.Name.LocalName == "description")?.Value ?? "";
                    if (!Uri.TryCreate(link, UriKind.Absolute, out var page) ||
                        !JournalismHost(page) || pages.Contains(page)) continue;
                    // Search by the core game's full identity, not the
                    // article headline. For GTA VI accept its edition aliases,
                    // reject GTA V and unrelated games; subject boosters only
                    // rank the results rather than restricting the entire search.
                    var evidence = title + " " + summary + " " + page.AbsolutePath;
                    if (IsGtaSixStory(project.Title, focus))
                    {
                        if (!IsGtaSix(evidence) || IsGtaFive(evidence)) continue;
                    }
                    else if (!Normalize(evidence).Contains(
                        Normalize(subject), StringComparison.OrdinalIgnoreCase))
                        continue;
                    pages.Add(page);
                    if (pages.Count >= 10) break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log?.Invoke("Independent news media search unavailable: " + ex.Message);
            }
        }
        log?.Invoke($"Gaming media web search: {pages.Count} additional gaming journalism pages.");
        return pages;
    }

    private sealed record VideoLead(string SourceName, string Channel, string Url,
        string Title, bool VerifiedGameMatch);

    // YouTube channels are discovery/catalog leads, not downloadable MP4
    // endpoints. Record exact candidate URLs and titles. Video downloads
    // require a platform-compliant authorized source.
    private static readonly (string Name, string Channel)[] VideoChannels =
    [
        ("IGN YouTube", "IGN"),
        ("GameSpot YouTube", "GameSpot"),
        ("PlayStation YouTube", "PlayStation"),
        ("Nintendo YouTube", "NintendoAmerica")
    ];

    private static async Task<IReadOnlyList<VideoLead>> DiscoverVideoChannelLeadsAsync(
        VideoProject project, string focus, Action<string>? log, CancellationToken ct)
    {
        var subject = CoreGameSubject(project, focus);
        if (subject.Length > 115) subject = subject[..115];
        var leads = new List<VideoLead>();
        foreach (var (sourceName, channel) in VideoChannels)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var query = "\"" + subject + "\" site:youtube.com " + channel +
                    " official gameplay trailer";
                var uri = new Uri("https://www.bing.com/search?format=rss&q=" +
                    Uri.EscapeDataString(query));
                using var response = await Http.GetAsync(
                    uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode ||
                    response.Content.Headers.ContentLength > 350_000) continue;
                var xml = await response.Content.ReadAsStringAsync(ct);
                if (xml.Length > 350_000) continue;
                var document = XDocument.Parse(xml, LoadOptions.None);
                foreach (var item in document.Descendants()
                    .Where(x => x.Name.LocalName == "item").Take(15))
                {
                    var link = item.Elements().FirstOrDefault(
                        x => x.Name.LocalName == "link")?.Value ?? "";
                    var title = item.Elements().FirstOrDefault(
                        x => x.Name.LocalName == "title")?.Value ?? "";
                    if (!Uri.TryCreate(link, UriKind.Absolute, out var candidate) ||
                        candidate.Scheme != "https" ||
                        !(candidate.Host == "www.youtube.com" ||
                          candidate.Host == "youtube.com") ||
                        !(candidate.AbsolutePath.StartsWith("/watch", StringComparison.Ordinal) ||
                          candidate.AbsolutePath.StartsWith("/shorts/", StringComparison.Ordinal)) ||
                        !title.Contains(subject, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // A search result mentioning GTA VI is not evidence of
                    // what a thumbnail actually shows. Treat as a lead only.
                    if (IsGtaSixStory(project.Title, focus) && IsGtaFive(title))
                        continue;
                    if (leads.All(x => x.Url != candidate.ToString()))
                        leads.Add(new VideoLead(sourceName, channel,
                            candidate.ToString(), title, true));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke(sourceName +
                " search unavailable: " + ex.Message); }
        }
        log?.Invoke($"YouTube source discovery: {leads.Count} matching IGN, GameSpot, PlayStation and Nintendo video links. " +
            "References only; no unlicensed download attempted.");
        return leads;
    }

    private static string CoreGameSubject(VideoProject project, string focus)
    {
        // Match the actual named game rather than an editorial article headline.
        // For GTA VI Romance Guide, the stable search entity is GTA VI; Jason
        // and Lucia are secondary search refinements, never alternate games.
        if (IsGtaSixStory(project.Title, focus))
            return "Grand Theft Auto VI";
        if (!string.IsNullOrWhiteSpace(focus) &&
            !focus.Trim().Equals("all gaming news", StringComparison.OrdinalIgnoreCase))
            return focus.Trim();
        var named = project.Chapters.SelectMany(x => x.Scenes)
            .Select(x => x.VisualQuery?.Trim() ?? "")
            .FirstOrDefault(x => x.Length is >= 4 and <= 65 &&
                !Regex.IsMatch(x, @"(?i)\b(news|latest|screenshot|background|image|cinematic|gameplay|trailer)\b"));
        return named ?? project.Title.Split(':', ',', '—')[0].Trim();
    }

    private static IReadOnlyList<string> CoreMediaQueries(VideoProject project, string focus)
    {
        if (IsGtaSixStory(project.Title, focus))
            return [
                "Grand Theft Auto VI",
                "GTA 6",
                "GTA VI Jason Lucia",
                "GTA 6 official screenshots",
                "GTA VI Lucia Jason",
                "Grand Theft Auto VI trailer"
            ];
        var core = CoreGameSubject(project, focus);
        return [core, core + " screenshots", core + " official gameplay",
            core + " trailer", core + " press media"];
    }

    private static string GamingMediaSubject(VideoProject project, string focus)
    {
        if (IsGtaSixStory(project.Title, focus))
            return "Grand Theft Auto VI";
        if (!string.IsNullOrWhiteSpace(focus)) return focus.Trim();
        // Avoid searching Commons for a whole headline and mistakenly using
        // a generic gaming stock video. Query a short, exact subject only.
        foreach (var scene in project.Chapters.SelectMany(x => x.Scenes))
        {
            var term = (scene.VisualQuery ?? "").Trim();
            var pieces = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length is >= 2 and <= 4 &&
                !Regex.IsMatch(term, @"\b(gaming|news|breaking|video|background|footage|trailer)\b",
                    RegexOptions.IgnoreCase))
                return term;
        }
        return "";
    }

    private static bool WikimediaStrictMatch(PixabayFlowClip file, string term)
    {
        var normalized = Normalize(term);
        if (normalized.Length < 5) return false;
        var indexed = Normalize(file.Tags + " " + file.SourcePage);
        return indexed.Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(List<OfficialAsset> Photos, List<OfficialAsset> Videos)>
        AcquireCommonsMediaAsync(VideoProject project, string focus,
            string mediaDir, Action<string>? log, CancellationToken ct)
    {
        var photos = new List<OfficialAsset>();
        var videos = new List<OfficialAsset>();
        var term = GamingMediaSubject(project, focus);
        if (term.Length < 5) return (photos, videos);
        var scene = new PixabayFootageScene(1, term, term, "neutral");
        try
        {
            var images = await new WikimediaCommonsImageService()
                .FindImagesAsync(scene, 3, log, ct);
            foreach (var image in images.Where(x => WikimediaStrictMatch(x, term)))
            {
                var ext = Path.GetExtension(image.FilePath);
                var destination = Path.Combine(mediaDir, "commons-photo-" +
                    (photos.Count + 1).ToString("00") + ext);
                File.Copy(image.FilePath, destination, true);
                photos.Add(new OfficialAsset(destination, image.SourcePage, term,
                    image.SourcePage, "Wikimedia Commons / " + image.Attribution,
                    image.License + " " + image.LicenseUrl));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log?.Invoke("Wikimedia image source skipped: " + ex.Message); }

        try
        {
            var clips = await new WikimediaCommonsVideoService()
                .FindVideosAsync(scene, 2, null, log, ct);
            foreach (var clip in clips.Where(x => WikimediaStrictMatch(x, term)))
            {
                var ext = Path.GetExtension(clip.FilePath);
                if (!ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                    continue; // Preserve compatible input formats for existing FFmpeg renderer.
                var destination = Path.Combine(mediaDir, "commons-video-" +
                    (videos.Count + 1).ToString("00") + ext);
                File.Copy(clip.FilePath, destination, true);
                videos.Add(new OfficialAsset(destination, clip.SourcePage, term,
                    clip.SourcePage, "Wikimedia Commons / " + clip.Attribution,
                    clip.License + " " + clip.LicenseUrl));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log?.Invoke("Wikimedia video source skipped: " + ex.Message); }

        log?.Invoke($"Independent Wikimedia Commons for '{term}': " +
            $"{photos.Count} relevant photos, {videos.Count} video clips.");
        return (photos, videos);
    }

    private static string Normalize(string value) =>
        Regex.Replace(WebUtility.HtmlDecode(value).ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    // A title-prefix or generic "grand theft auto" match is insufficient:
    // GTA V media must never be selected for GTA VI. Require a complete
    // game identifier and reject conflicting edition identifiers.
    internal static bool IsGtaSix(string value) => Regex.IsMatch(value,
        @"(?i)\b(?:gta[\s_-]*(?:vi|6)|grand[\s_-]+theft[\s_-]+auto[\s_-]*(?:vi|6))\b");
    internal static bool IsGtaFive(string value) => Regex.IsMatch(value,
        @"(?i)\b(?:gta[\s_-]*(?:v|5)|grand[\s_-]+theft[\s_-]+auto[\s_-]*(?:v|5))\b");

    private static bool IsGtaSixStory(string title, string focus)
        => IsGtaSix(title) || IsGtaSix(focus);

    private static bool RejectGenericGraphic(Uri asset, string metadata)
    {
        var path = Uri.UnescapeDataString(asset.AbsolutePath);
        var name = Path.GetFileNameWithoutExtension(path);
        var combined = name + " " + metadata;
        // Branding, web controls, banners, spinner/loading graphics and
        // tiny icons are not game screenshots.
        return Regex.IsMatch(combined,
            @"(?i)(?:^|[^a-z0-9])(logo|icon|sprite|placeholder|spinner|loading|avatar|default|badge|button|whitebar|loadingbar|progress|skeleton|thumb_default|generic)(?:$|[^a-z0-9])");
    }

    private static bool ExactGameEvidence(
        string title, string focus, Uri sourcePage, Uri image, string metadata)
    {
        if (!IsGtaSixStory(title, focus)) return true;
        var source = Uri.UnescapeDataString(sourcePage.AbsoluteUri);
        var asset = Uri.UnescapeDataString(image.AbsoluteUri);
        var evidence = source + " " + asset + " " + metadata;
        // GTA 5 and GTA 6 are different games. If the asset or its media
        // label explicitly identifies GTA V, reject regardless of article.
        if (IsGtaFive(asset + " " + metadata)) return false;
        if (RejectGenericGraphic(image, metadata)) return false;
        var rockstarGallery = sourcePage.Host == "www.rockstargames.com" &&
            sourcePage.AbsolutePath.StartsWith("/VI/media", StringComparison.OrdinalIgnoreCase) &&
            image.Host == "www.rockstargames.com" &&
            image.AbsolutePath.StartsWith("/VI/_next/static/media/", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(image.AbsolutePath, @"(?i)\.(?:jpe?g|png)$");
        // A GTA VI-scoped publisher gallery explicitly identifies the game,
        // even when its individual filename is Jason_Duval_01.jpg.
        // Ordinary news article pages are NOT trustworthy game identity.
        return rockstarGallery || IsGtaSix(asset + " " + metadata);
    }

    private static bool MatchStoreGame(string expectedStory, string focus, string game)
    {
        if (IsGtaSixStory(expectedStory, focus))
            return IsGtaSix(game) && !IsGtaFive(game);
        var gameWords = Normalize(game);
        var story = Normalize(expectedStory + " " + focus);
        return gameWords.Length > 5 &&
            Regex.IsMatch(story, @"(?i)(?<![a-z0-9])" +
                Regex.Escape(gameWords) + @"(?![a-z0-9])");
    }

    private static IEnumerable<string> Queries(VideoProject project, string focus)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in CoreMediaQueries(project, focus))
            if (seen.Add(query)) yield return query;

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

    // Browser element screenshots can be only 180-400px wide even if the
    // publisher hosts a true 1920px image. Never call them full-resolution.
    // Validate the decoded original raster before selecting it for 1080p output.
    private static bool HasFullResolution(string path, Action<string>? log)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
            var shortest = Math.Min(frame.PixelWidth, frame.PixelHeight);
            if (longest >= 1400 && shortest >= 700) return true;
            log?.Invoke($"Gaming quality: skipping {Path.GetFileName(path)} " +
                $"({frame.PixelWidth}x{frame.PixelHeight}); too small for sharp 1080p.");
        }
        catch (Exception ex)
        {
            log?.Invoke("Gaming quality: unreadable image " +
                Path.GetFileName(path) + ": " + ex.Message);
        }
        return false;
    }

    private static bool IsJpeg(byte[] bytes) =>
        bytes.Length > 2000 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
    private static bool IsPng(byte[] bytes) =>
        bytes.Length > 2000 && bytes[0] == 137 && bytes[1] == 80 &&
        bytes[2] == 78 && bytes[3] == 71;
    private static string MediaFingerprint(string file)
    {
        // Compare file contents, not just URLs: multiple news websites often
        // serve the same picture under different CDN links and filenames.
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsMp4(byte[] bytes) =>
        bytes.Length > 4096 && bytes[4] == (byte)'f' &&
        bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p';

    // Videos come from the verified Steam game's publisher-provided trailers
    // and from direct MP4 sources embedded in genuine official news pages.
    // Do not follow redirects to unrelated hosts.
    private static IEnumerable<Uri> ExtractOfficialVideoUrls(string html, Uri page)
    {
        foreach (Match m in Regex.Matches(html,
            @"<(?:video|source|meta)\b[^>]{0,2400}>",
            RegexOptions.IgnoreCase).Cast<Match>().Take(90))
        {
            var tag = m.Value;
            if (tag.StartsWith("<meta", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(tag, @"(?:og:video|twitter:player:stream)",
                    RegexOptions.IgnoreCase)) continue;
            var match = Regex.Match(tag,
                @"(?:src|content|data-src)\s*=\s*[""'](?<u>[^""']+)[""']",
                RegexOptions.IgnoreCase);
            var value = WebUtility.HtmlDecode(match.Groups["u"].Value);
            if (!Uri.TryCreate(page, value, out var uri) ||
                !IsPublicHttps(uri) || !uri.AbsolutePath.EndsWith(".mp4",
                    StringComparison.OrdinalIgnoreCase)) continue;
            yield return uri;
        }
    }

    private static async Task<byte[]> GetVideoAsync(Uri uri, CancellationToken ct)
    {
        if (!IsPublicHttps(uri)) throw new InvalidDataException("Invalid video URL.");
        const int maxSize = 120 * 1024 * 1024;
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxSize)
            throw new InvalidDataException("Gameplay clip too large.");
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Length > 0 && !type.Contains("mp4", StringComparison.OrdinalIgnoreCase) &&
            !type.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Not an MP4 video.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        var block = new byte[64 * 1024];
        int length;
        while ((length = await stream.ReadAsync(block, ct)) > 0)
        {
            if (memory.Length + length > maxSize)
                throw new InvalidDataException("Gameplay video exceeds safe download cap.");
            await memory.WriteAsync(block.AsMemory(0, length), ct);
        }
        return memory.ToArray();
    }

    public async Task<OfficialMediaResult> AcquireAsync(VideoProject project, string focus,
        string articleSummary, BrowserSession browser, ZeroVideoSettings settings,
        Action<string>? log, CancellationToken ct)
    {
        var context = Normalize(project.Title + " " + project.Topic + " " +
            articleSummary + " " + focus + " " +
            string.Join(" ", project.Chapters.SelectMany(x => x.Scenes)
                .Select(x => x.Narration + " " + x.VisualQuery)));
        var candidates = Queries(project, focus).Take(15).ToList();
        var examined = new HashSet<int>();
        var images = new List<(Uri Uri, string Game, string Store)>();
        var movies = new List<(Uri Uri, string Game, string Page, string Source)>();
        var movieSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Priority 1: images genuinely hosted/linked by the official publisher
        // or platform article, when the news report itself comes from one.
        var publisherImages = new List<(Uri Image, Uri Source)>();
        var discoveredPerPublisher = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var linkedPublishers = await LinkedOfficialPagesAsync(project, log, ct);
        var independentPublishers = await DiscoverIndependentPublisherPagesAsync(
            project, focus, log, ct);
        var independentNewsPages = await DiscoverGamingNewsMediaPagesAsync(
            project, focus, log, ct);
        var youtubeLeads = await DiscoverVideoChannelLeadsAsync(
            project, focus, log, ct);
        foreach (var sourcePage in PublisherPages(project)
                     .Concat(linkedPublishers).Concat(independentPublishers)
                     .Distinct().Take(14))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var html = await DownloadPublisherHtmlAsync(sourcePage, ct);
                if (html.Length < 100) continue;
                foreach (var video in ExtractOfficialVideoUrls(html, sourcePage).Take(5))
                {
                    if (!ExactGameEvidence(project.Title, focus, sourcePage, video, ""))
                    {
                        log?.Invoke("Rejected publisher video without exact game evidence: " + video);
                        continue;
                    }
                    if (movieSeen.Add(video.ToString()))
                        movies.Add((video, project.Title, sourcePage.ToString(),
                            sourcePage.Host + " official video"));
                }
                foreach (var img in ExtractLinkedGalleryImages(html, sourcePage)
                    .Concat(ExtractPublisherImages(html, sourcePage)).Distinct().Take(90))
                {
                    if (publisherImages.Any(x => x.Image == img)) continue;
                    if (!ExactGameEvidence(project.Title, focus, sourcePage, img, ""))
                    {
                        log?.Invoke("Rejected publisher image without exact game evidence: " + img);
                        continue;
                    }
                    if (discoveredPerPublisher.GetValueOrDefault(sourcePage.Host) >= 14)
                        break;
                    publisherImages.Add((img, sourcePage));
                    discoveredPerPublisher[sourcePage.Host] =
                        discoveredPerPublisher.GetValueOrDefault(sourcePage.Host) + 1;
                    if (publisherImages.Count >= 80) break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke("Official publisher imagery unavailable: " + ex.Message); }
        }
        // Priority 2: media from multiple relevant gaming journalism pages.
        // Candidate pages are discovered independently from the news article.
        // Keep original page attribution for every downloaded asset.
        // This is not described as a first-party official screenshot; label
        // its actual publication in the credit manifest and on-screen source.
        // Prefer this over generating an invented picture for news that does
        // not have a Steam listing or a linked publisher newsroom.
        var editorialImages = new List<(Uri Image, Uri Source)>();
        // Always inspect independent journalism sources, even when Rockstar
        // already supplied enough images. The original early-exit caused
        // entire GTA VI batches to look like the same Rockstar slideshow.
        {
            foreach (var story in project.Sources
                .Select(x => Uri.TryCreate(x, UriKind.Absolute, out var u) ? u : null)
                .Where(x => x is not null && JournalismHost(x))
                .Concat(independentNewsPages).Distinct().Take(12))
            {
                if (story is null) continue;
                try
                {
                    var html = await DownloadNewsHtmlAsync(story, ct);
                    foreach (var clip in ExtractOfficialVideoUrls(html, story).Take(3))
                    {
                        if (!ExactGameEvidence(project.Title, focus, story, clip, "")) continue;
                        if (movieSeen.Add(clip.ToString()))
                            movies.Add((clip, project.Title, story.ToString(),
                                story.Host + " news article video"));
                    }
                    var described = ExtractDescribedArticleImages(html, story)
                        .Where(x => ExactGameEvidence(project.Title, focus, story,
                            x.Url, x.Alt)).Select(x => x.Url);
                    foreach (var candidate in described.Concat(
                        ExtractPublisherImages(html, story).Where(x =>
                            ExactGameEvidence(project.Title, focus, story, x, "")))
                        .Distinct().Take(32))
                    {
                        if (editorialImages.All(x => x.Image != candidate))
                            editorialImages.Add((candidate, story));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log?.Invoke("Editorial article images unavailable: " + ex.Message); }
            }
        }
        log?.Invoke($"Gaming media candidates: publisher images={publisherImages.Count}, " +
            $"independent article images={editorialImages.Count}, " +
            $"non-Steam videos={movies.Count}. Steam reserved for LAST fallback.");

        var mediaDir = Path.Combine(project.ProjectDirectory, "media");
        Directory.CreateDirectory(mediaDir);
        var downloaded = new List<OfficialAsset>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var publisherCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (imageUri, sourcePage) in publisherImages)
        {
            ct.ThrowIfCancellationRequested();
            if (downloaded.Count >= 16) break;
            var publisherKey = sourcePage.Host;
            if (publisherCounts.GetValueOrDefault(publisherKey) >= 9) continue;
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
                publisherCounts[publisherKey] = publisherCounts.GetValueOrDefault(publisherKey) + 1;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke("First-party image skipped: " + ex.Message); }
        }
        // A story's lead image is often a real publisher press screenshot.
        // Keep its attribution exact instead of pretending it is official.
        var journalismCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (imageUri, story) in editorialImages)
        {
            ct.ThrowIfCancellationRequested();
            if (downloaded.Count >= 34) break;
            if (journalismCounts.GetValueOrDefault(story.Host) >= 7) continue;
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
                journalismCounts[story.Host] = journalismCounts.GetValueOrDefault(story.Host) + 1;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke("Editorial image skipped: " + ex.Message); }
        }
        // Save actual playable clips, not synthetic motion or AI animation.
        // A publisher gameplay trailer is trimmed and reframed by the existing
        // FFmpeg Shorts renderer. Its original audio is discarded so narration
        // and selected background music retain their levels.
        var videos = new List<OfficialAsset>();
        foreach (var (uri, game, store, source) in movies.Take(14))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bytes = await GetVideoAsync(uri, ct);
                if (!IsMp4(bytes)) continue;
                var file = Path.Combine(mediaDir, "official-video-" +
                    (videos.Count + 1).ToString("00") + ".mp4");
                await File.WriteAllBytesAsync(file, bytes, ct);
                videos.Add(new OfficialAsset(file, uri.ToString(), game, store,
                    source, "Source attribution recorded; observe publisher/editorial use terms."));
                if (videos.Count >= 6) break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke("Official gameplay video skipped: " + ex.Message); }
        }
        // If the official gallery relies on client-side rendering, do not
        // fall back to a single PC Gamer photo. Read genuine GTA VI gallery
        // images through ZeroVideo's existing Edge browser instead.
        if (IsGtaSixStory(project.Title, focus) && downloaded.Count < 32)
        {
            var gallery = await CaptureRockstarGalleryAsync(
                browser, settings, mediaDir, log, ct);
            foreach (var image in gallery)
                if (downloaded.Count < 38 && unique.Add(image.Url))
                    downloaded.Add(image);
        }
        // Do not accept a full Rockstar pool as "diversified". Independently
        // collect GTA VI screenshots from GameSpot and Gematsu galleries.
        if (IsGtaSixStory(project.Title, focus))
        {
            var independentGalleries = await CaptureIndependentGtaMediaAsync(
                browser, settings, mediaDir, log, ct);
            foreach (var image in independentGalleries)
                if (unique.Add(image.Url)) downloaded.Add(image);
        }
        // Independent archive source (not the original PC Gamer article).
        // Reuse ZeroVideo's existing licensed Commons image/video adapters.
        // This also works when an official game's website blocks direct downloads.
        var commons = await AcquireCommonsMediaAsync(
            project, focus, mediaDir, log, ct);
        foreach (var photo in commons.Photos.Take(Math.Max(0, 40 - downloaded.Count)))
            downloaded.Add(photo);
        foreach (var clip in commons.Videos.Take(Math.Max(0, 6 - videos.Count)))
            videos.Add(clip);
        if (downloaded.Count == 0 && videos.Count == 0)
        {
            log?.Invoke("Direct downloads unavailable. Trying the original article " +
                "in Edge for a genuine article/game screenshot (no AI).");
            var articleScreenshot = await CaptureSourceArticleAsync(
                project, browser, settings, mediaDir, log, ct);
            if (articleScreenshot is not null) downloaded.Add(articleScreenshot);
        }
        // LAST RESORT: only query Steam after publisher websites, gaming
        // news sites, Wikimedia Commons and the original article have been
        // tried. Never let Steam crowd out more relevant primary media.
        if (downloaded.Count + videos.Count < 12)
        {
            log?.Invoke("Gaming media: primary sources insufficient; trying Steam LAST.");
            // Exact-game Steam search is the final fallback.
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
                        !MatchStoreGame(project.Title, focus, game))
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
                    if (!MatchStoreGame(project.Title, focus, canonicalName))
                    {
                        log?.Invoke("Rejected different game edition: " + canonicalName);
                        continue;
                    }
                    if (data.TryGetProperty("movies", out var trailers) &&
                        trailers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var trailer in trailers.EnumerateArray().Take(4))
                        {
                            if (!trailer.TryGetProperty("mp4", out var mp4) ||
                                !mp4.TryGetProperty("max", out var source)) continue;
                            var url = source.GetString();
                            if (!Uri.TryCreate(url, UriKind.Absolute, out var trailerUri) ||
                                !SteamHost(trailerUri) ||
                                !trailerUri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (movieSeen.Add(trailerUri.ToString()))
                                movies.Add((trailerUri, canonicalName,
                                    "https://store.steampowered.com/app/" + appid,
                                    "Publisher gameplay trailer / Steam"));
                        }
                    }
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
        // Steam is a fallback, and never used for a mismatched game title.
        foreach (var (uri, game, store) in images)
        {
            if (downloaded.Count >= 40) break;
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
            // Trailer assets are retrieved only when non-Steam sources
            // supplied fewer than four usable media items.
            foreach (var (uri, game, store, source) in movies
                .Where(x => x.Source.Contains("Steam", StringComparison.OrdinalIgnoreCase))
                .Take(3 - Math.Min(3, videos.Count)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var bytes = await GetVideoAsync(uri, ct);
                    if (!IsMp4(bytes)) continue;
                    var file = Path.Combine(mediaDir, "official-video-" +
                        (videos.Count + 1).ToString("00") + ".mp4");
                    await File.WriteAllBytesAsync(file, bytes, ct);
                    videos.Add(new OfficialAsset(file, uri.ToString(), game, store,
                        source, "Publisher gameplay video from Steam; source recorded."));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log?.Invoke("Last-resort Steam trailer unavailable: " + ex.Message); }
            }
        }
        if (downloaded.Count == 0 && videos.Count == 0)
            throw new InvalidDataException(
                "All official, article, Steam and Edge media options failed. " +
                "Source article may block images or require a login. " +
                "Skipped without any generated imagery; see source diagnostics.");

        // 24 visual shots, not 24 narrated chapters: three real-media shots
        // per each of eight spoken news beats. xfade transitions are controlled
        // by this Gaming News project's Crossfade option only.
        // Refuse one-image static videos; do not manufacture visual variety.
        downloaded = downloaded.Where(x => HasFullResolution(x.LocalPath, log)).ToList();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueAssets = downloaded.Concat(videos)
            .Where(x => seenFiles.Add(MediaFingerprint(x.LocalPath)))
            .ToList();
        downloaded = uniqueAssets.Where(x =>
            !new[] { ".mp4", ".webm", ".mov", ".mkv", ".avi" }
                .Contains(Path.GetExtension(x.LocalPath), StringComparer.OrdinalIgnoreCase))
            .ToList();
        videos = uniqueAssets.Where(x =>
            new[] { ".mp4", ".webm", ".mov", ".mkv", ".avi" }
                .Contains(Path.GetExtension(x.LocalPath), StringComparer.OrdinalIgnoreCase))
            .ToList();
        var requiredDistinct = IsGtaSixStory(project.Title, focus) ? 20 : 12;
        if (uniqueAssets.Count < requiredDistinct)
            throw new InvalidDataException(
                $"Gaming News found only {uniqueAssets.Count}/{requiredDistinct} distinct matching game assets. " +
                "A 24-shot video needs enough real screenshots/clips, not repeats of one image. " +
                "Try again when the official gallery or news media is accessible; " +
                "no GTA V or unrelated visual replacements will be published.");

        var history = ReadMediaHistory(log);
        var selectedShots = SelectDiverseShots(project, uniqueAssets, history, log);
        if (selectedShots.Count != 24)
            throw new InvalidDataException("Gaming News could not select 24 authentic shots.");
        var chapter = project.Chapters.Single();
        for (var i = 0; i < chapter.Scenes.Count; i++)
        {
            var photos = selectedShots.Skip(i * 3).Take(3).ToList();
            chapter.Scenes[i].MediaPath = photos[0].LocalPath;
            chapter.Scenes[i].MediaPaths = photos.Skip(1)
                .Select(x => x.LocalPath).ToList();
        }
        var shotSequence = selectedShots.Select(x => x.LocalPath).ToList();
        log?.Invoke("Gaming News: 24 shuffled visual shots from multiple " +
            "sites where available, with smooth crossfades.");

        if (videos.Count > 0)
            log?.Invoke($"Gaming media mix: {downloaded.Count} real screenshots + " +
                $"{videos.Count} real gameplay trailer clips across 8 visual beats.");
        else
            log?.Invoke("No downloadable official MP4 clips were available; " +
                "rendering authentic screenshots only (never AI imagery).");
        var sourceNames = selectedShots.Select(MediaSourceHost)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var label = "Media: " + string.Join(" / ", sourceNames.Take(3)) +
            (sourceNames.Count > 3 ? " +" + (sourceNames.Count - 3) : "");
        var manifest = Path.Combine(project.ProjectDirectory, "official-media-credits.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new
        {
            project = project.Title, credit = label,
            legalNote = "Attribution is not a license. Confirm publisher's use rules or applicable news/editorial exception.",
            assets = downloaded.Concat(videos).ToList(),
            selectedShotOrder = selectedShots.Select(x => new
            {
                x.Url, x.Source, sourceHost = MediaSourceHost(x),
                fingerprint = MediaFingerprint(x.LocalPath)
            }).ToList(),
            youtubeReferenceLeads = youtubeLeads,
            providerPriority = new[]
            {
                "Official publisher / game website",
                "IGN, GameSpot, Games Press, Gematsu",
                "Other relevant gaming publications",
                "Wikimedia Commons",
                "Original article screenshot",
                "Steam (last resort)"
            },
            youtubeVideoSources = new[]
            {
                "IGN YouTube", "GameSpot YouTube",
                "PlayStation YouTube", "Nintendo YouTube"
            },
            rumorPolicy = "Reddit GamingLeaksAndRumours is unverified: never treat rumors as established fact."
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        log?.Invoke($"Gaming News: {downloaded.Count} screenshots and " +
            $"{videos.Count} real video clip(s); no ComfyUI/AI images. Manifest: " + manifest);
        // Persist the selected visuals across batch videos so the next video
        // prefers fresh photos, even when the article covers the same game.
        foreach (var used in selectedShots.DistinctBy(x => MediaFingerprint(x.LocalPath)))
            history.Add(new MediaUse
            {
                Fingerprint = MediaFingerprint(used.LocalPath),
                Url = used.Url,
                VideoId = project.Id,
                UsedUtc = DateTime.UtcNow
            });
        await SaveMediaHistoryAsync(history, log, ct);
        return new OfficialMediaResult(label, downloaded.Concat(videos).ToList());
    }

    // Known GTA VI-specific galleries on independent publishers. These are
    // separate source websites, not mirrors counted as new artwork.
    private static readonly (string Publisher, string Url)[] GtaSixThirdPartyGalleries =
    [
        ("GameSpot", "https://www.gamespot.com/gallery/gta-6-screenshots-images/2900-6525/"),
        ("Gematsu", "https://www.gematsu.com/2026/11/grand-theft-auto-vi-launch-screenshots"),
        ("Gematsu", "https://www.gematsu.com/gallery/grand-theft-auto-vi/november-5-2026"),
        ("Gematsu", "https://www.gematsu.com/gallery/grand-theft-auto-vi/june-24-2026")
    ];

    private static async Task<List<OfficialAsset>> CaptureIndependentGtaMediaAsync(
        BrowserSession browser, ZeroVideoSettings settings, string mediaDir,
        Action<string>? log, CancellationToken ct)
    {
        var found = new List<OfficialAsset>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (publisher, url) in GtaSixThirdPartyGalleries)
        {
            ct.ThrowIfCancellationRequested();
            Microsoft.Playwright.IPage? page = null;
            var countForSite = found.Count(x => x.Source.Equals(publisher, StringComparison.OrdinalIgnoreCase));
            if (countForSite >= 10) continue;
            try
            {
                await browser.EnsureStartedAsync(settings);
                page = await browser.Context.NewPageAsync();
                await page.GotoAsync(url, new Microsoft.Playwright.PageGotoOptions
                {
                    WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded,
                    Timeout = 25000
                });
                var imageNodes = page.Locator("article img, main img, figure img, .gallery img");
                var count = Math.Min(await imageNodes.CountAsync(), 260);
                for (var i = 0; i < count && countForSite < 10; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var node = imageNodes.Nth(i);
                        var alt = await node.GetAttributeAsync("alt") ?? "";
                        var src = await node.GetAttributeAsync("src") ?? "";
                        var lazy = await node.GetAttributeAsync("data-src") ?? "";
                        var srcset = await node.GetAttributeAsync("srcset") ?? "";
                        var urls = new List<string>();
                        if (!string.IsNullOrWhiteSpace(srcset))
                        {
                            urls.AddRange(srcset.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(x => x.Trim().Split(' ')[0])
                                .Reverse());
                        }
                        urls.Add(lazy);
                        urls.Add(src);
                        foreach (var value in urls)
                        {
                            if (string.IsNullOrWhiteSpace(value) ||
                                !Uri.TryCreate(new Uri(url), WebUtility.HtmlDecode(value),
                                    out var uri) || !IsPublicHttps(uri) ||
                                RejectGenericGraphic(uri, alt) ||
                                IsGtaFive(alt + " " + uri.AbsolutePath) ||
                                !visited.Add(uri.ToString()))
                                continue;
                            // A dedicated, exact-game screenshot gallery and
                            // an in-article image are stronger evidence than
                            // an arbitrary search thumbnail, but never accept
                            // tiny graphics or sidebar avatars.
                            var fileBase = Path.Combine(mediaDir,
                                "independent-gta6-" + (found.Count + 1).ToString("000"));
                            try
                            {
                                var bytes = await DownloadPublisherImageAsync(
                                    uri, new Uri(url), ct);
                                if (!IsJpeg(bytes) && !IsPng(bytes)) continue;
                                var file = fileBase + (IsJpeg(bytes) ? ".jpg" : ".png");
                                await File.WriteAllBytesAsync(file, bytes, ct);
                                if (!HasFullResolution(file, log))
                                {
                                    File.Delete(file);
                                    continue;
                                }
                                found.Add(new OfficialAsset(file, uri.ToString(),
                                    "Grand Theft Auto VI", url, publisher,
                                    "Source gallery shows GTA VI images; provenance recorded."));
                                countForSite++;
                                break;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                log?.Invoke(publisher + " gallery image skipped: " + ex.Message);
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        log?.Invoke(publisher + " image node skipped: " + ex.Message);
                    }
                }
                log?.Invoke($"Gaming News {publisher} verified gallery: {countForSite} full-resolution images.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log?.Invoke(publisher + " gallery browsing unavailable: " + ex.Message);
            }
            finally
            {
                if (page is not null)
                    try { await page.CloseAsync(); } catch { }
            }
        }
        return found;
    }

    private static async Task<List<OfficialAsset>> CaptureRockstarGalleryAsync(
        BrowserSession browser, ZeroVideoSettings settings, string mediaDir,
        Action<string>? log, CancellationToken ct)
    {
        var result = new List<OfficialAsset>();
        Microsoft.Playwright.IPage? page = null;
        const string source = "https://www.rockstargames.com/VI/media/screenshots";
        try
        {
            await browser.EnsureStartedAsync(settings);
            page = await browser.Context.NewPageAsync();
            await page.GotoAsync(source, new Microsoft.Playwright.PageGotoOptions
            {
                WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded,
                Timeout = 30000
            });
            var gallery = page.Locator("img");
            var count = Math.Min(await gallery.CountAsync(), 200);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count && result.Count < 24; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var image = gallery.Nth(i);
                    var path = await image.GetAttributeAsync("src") ?? "";
                    var alt = await image.GetAttributeAsync("alt") ?? "";
                    var combined = path + " " + alt;
                    if (!path.Contains("/VI/", StringComparison.OrdinalIgnoreCase) ||
                        !(path.Contains("media", StringComparison.OrdinalIgnoreCase) ||
                          IsGtaSix(combined)) ||
                        IsGtaFive(combined) ||
                        !seen.Add(path))
                        continue;
                    var box = await image.BoundingBoxAsync();
                    if (box is null || box.Width < 180 || box.Height < 110) continue;
                    var filename = Path.Combine(mediaDir,
                        "rockstar-gallery-" + (result.Count + 1).ToString("00") + ".png");
                    var imageUrl = Uri.TryCreate(new Uri(source), path, out var resolved)
                        ? resolved.ToString() : source;
                    var saved = false;
                    if (resolved is not null && IsPublicHttps(resolved))
                    {
                        try
                        {
                            var fullImage = await DownloadPublisherImageAsync(
                                resolved, new Uri(source), ct);
                            if (IsPng(fullImage) || IsJpeg(fullImage))
                            {
                                filename = Path.ChangeExtension(filename,
                                    IsJpeg(fullImage) ? ".jpg" : ".png");
                                await File.WriteAllBytesAsync(filename, fullImage, ct);
                                saved = HasFullResolution(filename, log);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            log?.Invoke("Rockstar original-res download unavailable: " +
                                ex.Message);
                        }
                    }
                    if (!saved)
                    {
                        // Never replace the original with a tiny on-screen
                        // thumbnail: this caused the user's pixelated video.
                        if (File.Exists(filename)) File.Delete(filename);
                        continue;
                    }
                    result.Add(new OfficialAsset(filename, imageUrl,
                        "Grand Theft Auto VI", source, "Rockstar Games / GTA VI official gallery",
                        "Official game screenshot captured from publisher's public media page."));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log?.Invoke("Rockstar gallery photo skipped: " + ex.Message);
                }
            }
            log?.Invoke($"Rockstar browser gallery recovered {result.Count} genuine GTA VI screenshots.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke("Rockstar browser gallery unavailable: " + ex.Message);
        }
        finally
        {
            if (page is not null)
                try { await page.CloseAsync(); } catch { }
        }
        return result;
    }

    private static async Task<OfficialAsset?> CaptureSourceArticleAsync(
        VideoProject project, BrowserSession browser, ZeroVideoSettings settings,
        string mediaDir, Action<string>? log, CancellationToken ct)
    {
        // The screenshot is genuinely from the cited original report, not
        // an invented image. Prefer an actual displayed photo, then the
        // headline/article view. Never capture login/blocked/error pages.
        foreach (var raw in project.Sources.Take(2))
        {
            ct.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var article) ||
                !(JournalismHost(article) || PublisherHost(article))) continue;
            Microsoft.Playwright.IPage? page = null;
            try
            {
                await browser.EnsureStartedAsync(settings);
                page = await browser.Context.NewPageAsync();
                await page.GotoAsync(article.ToString(),
                    new Microsoft.Playwright.PageGotoOptions
                    {
                        WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded,
                        Timeout = 25000
                    });
                await page.WaitForTimeoutAsync(1100);
                var pageTitle = await page.TitleAsync();
                var body = await page.Locator("body").InnerTextAsync(
                    new Microsoft.Playwright.LocatorInnerTextOptions { Timeout = 5000 });
                if (body.Length < 150 ||
                    System.Text.RegularExpressions.Regex.IsMatch(
                        (pageTitle + " " + body[..Math.Min(body.Length, 1200)]),
                        @"\b(access denied|403 forbidden|captcha|checking your browser|verify you are human|404 not found)\b",
                        RegexOptions.IgnoreCase))
                    continue;
                // Prefer the genuine article photo if loaded; omit tiny icons,
                // profile photos and decorative SVGs.
                var candidates = page.Locator(
                    "article img, main img, img[src*='image'], img[src*='media']");
                var n = Math.Min(await candidates.CountAsync(), 22);
                for (var i = 0; i < n; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var node = candidates.Nth(i);
                        if (IsGtaSixStory(project.Title, ""))
                        {
                            var src = await node.GetAttributeAsync("src") ?? "";
                            var alt = await node.GetAttributeAsync("alt") ?? "";
                            var title = await node.GetAttributeAsync("title") ?? "";
                            if (!IsGtaSix(src + " " + alt + " " + title) ||
                                IsGtaFive(src + " " + alt + " " + title))
                                continue;
                        }
                        var bounds = await node.BoundingBoxAsync();
                        if (bounds is null || bounds.Width < 500 || bounds.Height < 220)
                            continue;
                        var file = Path.Combine(mediaDir, "article-real-image.png");
                        await node.ScreenshotAsync(new Microsoft.Playwright.LocatorScreenshotOptions
                        {
                            Path = file,
                            Timeout = 8000
                        });
                        if (File.Exists(file) && new FileInfo(file).Length > 12000)
                        {
                            log?.Invoke("Real news-article image captured from " + article.Host);
                            return new OfficialAsset(file, article.ToString(), project.Title,
                                article.ToString(), article.Host + " article photo",
                                "Actual source article image; attribution does not establish permission.");
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        log?.Invoke("Article image candidate failed: " + ex.Message);
                    }
                }
                // An article-page capture is allowed for general industry
                // news. For GTA VI, a full-page screenshot can contain GTA V
                // artwork or unrelated thumbnails, so skip that fallback.
                if (IsGtaSixStory(project.Title, "")) continue;
                var screenshot = Path.Combine(mediaDir, "article-page.png");
                await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
                {
                    Path = screenshot, FullPage = false, Timeout = 10000
                });
                if (!File.Exists(screenshot) ||
                    new FileInfo(screenshot).Length < 15000) continue;
                log?.Invoke("Using actual article page screenshot from " + article.Host);
                return new OfficialAsset(screenshot, article.ToString(), project.Title,
                    article.ToString(), article.Host + " original report",
                    "Actual article screenshot; use according to applicable editorial rights.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log?.Invoke("Edge article screenshot fallback failed for " +
                    article.Host + ": " + ex.Message);
            }
            finally
            {
                if (page is not null)
                    try { await page.CloseAsync(); } catch { }
            }
        }
        return null;
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
            "-c:v libx264 -preset medium -crf 16 -pix_fmt yuv420p " +
            "-c:a copy -movflags +faststart \"" + tmp + "\"";
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
            " -map \"[v]\" -map \"[a]\" -c:v libx264 -preset medium -crf 16" +
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
