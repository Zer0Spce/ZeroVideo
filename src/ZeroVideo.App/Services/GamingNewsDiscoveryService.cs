using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ZeroVideo.App.Services;

public sealed record GamingNewsTopic(string Title, string Url, string Publisher, DateTimeOffset PublishedAt, string Summary);

/// <summary>Manual, one-shot RSS discovery. No timer or recurring background worker.</summary>
public sealed class GamingNewsDiscoveryService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private static readonly (string Name, string Url)[] Feeds =
    [
        ("IGN", "https://feeds.ign.com/ign/games-all"),
        ("GameSpot", "https://www.gamespot.com/feeds/news/"),
        ("PC Gamer", "https://www.pcgamer.com/rss/"),
        ("Gematsu", "https://www.gematsu.com/feed"),
        ("PlayStation Blog", "https://blog.playstation.com/feed/"),
        ("Xbox Wire", "https://news.xbox.com/en-us/feed/"),
        ("Eurogamer", "https://www.eurogamer.net/feed"),
        ("Video Games Chronicle", "https://www.videogameschronicle.com/feed/"),
        ("Nintendo Life", "https://www.nintendolife.com/feeds/latest"),
        ("Push Square", "https://www.pushsquare.com/feeds/latest"),
        ("Rock Paper Shotgun", "https://www.rockpapershotgun.com/feed"),
        ("Destructoid", "https://www.destructoid.com/feed/"),
        ("Polygon", "https://www.polygon.com/rss/index.xml"),
        ("Kotaku", "https://kotaku.com/rss")
    ];
    private static async Task LogUnverifiedRedditLeadsAsync(
        Action<string>? log, CancellationToken ct)
    {
        // Reddit is research/rumor context only, never an automatically
        // verified fact or source for a confidently narrated news story.
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://www.reddit.com/r/GamingLeaksAndRumours/new/.rss?limit=10");
            request.Headers.UserAgent.ParseAdd(
                "ZeroVideo/0.1 (+https://github.com/Zer0Spce/ZeroVideo)");
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length > 500_000) return;
            var document = XDocument.Parse(body, LoadOptions.None);
            var count = document.Descendants()
                .Count(x => x.Name.LocalName == "entry");
            log?.Invoke($"GamingLeaksAndRumours: {count} unverified rumor leads seen; " +
                "none used as confirmed news, assets or scheduled videos.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke("Reddit rumor-lead discovery unavailable: " + ex.Message);
        }
    }

    private static IEnumerable<(string Name, string Url)> NewsSearchFeeds(string focus)
    {
        // Google News aggregates independent publications worldwide, so
        // discovery is no longer restricted to a hardcoded RSS outlet list.
        // These are ordinary public search feeds, no API or credentials.
        var queries = new List<string>
        {
            "video game news when:3d",
            "gaming industry news when:3d",
            "new game announcements when:3d",
            "PC gaming console gaming news when:3d",
            "video game trailers updates when:3d"
        };
        if (!string.IsNullOrWhiteSpace(focus) &&
            !focus.Equals("all gaming news", StringComparison.OrdinalIgnoreCase))
        {
            var topic = Regex.Replace(focus.Trim(), @"\s+", " ");
            if (topic.Length > 75) topic = topic[..75];
            queries.InsertRange(0, [
                topic + " video game news when:7d",
                topic + " game updates when:7d",
                topic + " gaming announcement when:7d"
            ]);
        }
        foreach (var query in queries)
        {
            yield return ("Google News: " + query,
                "https://news.google.com/rss/search?q=" +
                Uri.EscapeDataString(query) + "&hl=en-US&gl=US&ceid=US:en");
        }
    }

    private static string NormalizedHeadline(string headline)
    {
        // Google News appends " - Publisher" and the same story may appear
        // from a direct publisher feed. Normalize before deduplication.
        var normalized = Regex.Replace(headline,
            @"\s+[-–—]\s+[^-–—]{2,75}$", "");
        return Regex.Replace(normalized.ToLowerInvariant(),
            @"[^a-z0-9]+", " ").Trim();
    }

    private static string Field(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";

    public async Task<List<GamingNewsTopic>> DiscoverAsync(
        int target, Action<string>? log, CancellationToken ct, string focus = "")
    {
        var topics = new List<GamingNewsTopic>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (publisher, feed) in NewsSearchFeeds(focus).Concat(Feeds))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await Http.GetAsync(feed, ct);
                response.EnsureSuccessStatusCode();
                var xml = await response.Content.ReadAsStringAsync(ct);
                if (xml.Length > 4_000_000) throw new InvalidDataException("RSS response too large.");
                var document = XDocument.Parse(xml, LoadOptions.None);
                var accepted = 0;
                foreach (var item in document.Descendants().Where(x =>
                             x.Name.LocalName is "item" or "entry").Take(120))
                {
                    var title = Regex.Replace(Field(item, "title"), @"\s+", " ").Trim();
                    var sourceName = Field(item, "source");
                    var isGoogleNews = publisher.StartsWith("Google News:", StringComparison.Ordinal);
                    if (isGoogleNews && sourceName.Length > 0 &&
                        title.EndsWith(" - " + sourceName, StringComparison.OrdinalIgnoreCase))
                        title = title[..^(sourceName.Length + 3)].Trim();
                    var link = Field(item, "link");
                    if (string.IsNullOrWhiteSpace(link))
                        link = item.Elements().FirstOrDefault(x => x.Name.LocalName == "link")
                            ?.Attribute("href")?.Value ?? "";
                    if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
                        uri.Scheme != Uri.UriSchemeHttps || title.Length < 12)
                        continue;
                    var key = NormalizedHeadline(title);
                    if (!seenUrls.Add(uri.GetLeftPart(UriPartial.Path)) || !seenTitles.Add(key))
                        continue;
                    var publishedText = Field(item, "pubDate");
                    if (publishedText.Length == 0) publishedText = Field(item, "published");
                    if (publishedText.Length == 0) publishedText = Field(item, "updated");
                    if (!DateTimeOffset.TryParse(publishedText, out var when))
                        continue;
                    // Do not present archival news as breaking news.
                    if (when > DateTimeOffset.UtcNow.AddHours(2) ||
                        when < DateTimeOffset.UtcNow.AddDays(-7)) continue;
                    // RSS description is supporting context, not permission to copy
                    // the article. Strip markup, cap length, and retain the source URL.
                    var description = Field(item, "description");
                    if (description.Length == 0) description = Field(item, "summary");
                    if (description.Length == 0) description = Field(item, "encoded");
                    description = Regex.Replace(description, "<[^>]+>", " ");
                    description = System.Net.WebUtility.HtmlDecode(description);
                    description = Regex.Replace(description, @"\s+", " ").Trim();
                    if (description.Length > 750) description = description[..750];
                    // Google News is a discovery index. Preserve the
                    // publication shown in its <source> field, not Google as
                    // the claimed original reporter.
                    var originalPublisher = isGoogleNews && sourceName.Length > 0
                        ? sourceName : publisher;
                    topics.Add(new GamingNewsTopic(
                        title, uri.ToString(), originalPublisher, when, description));
                    accepted++;
                }
                log?.Invoke($"Gaming News: {publisher}: {accepted} recent stories.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.Invoke($"Gaming News: {publisher} unavailable: {ex.Message}"); }
        }
        await LogUnverifiedRedditLeadsAsync(log, ct);
        // Rank recent stories across publishers rather than allowing one
        // high-volume outlet to fill all 48 news slots.
        var recent = topics.OrderByDescending(x => x.PublishedAt).ToList();
        var publishers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<GamingNewsTopic>();
        foreach (var story in recent)
        {
            if (selected.Count >= Math.Clamp(target, 1, 96)) break;
            var count = publishers.GetValueOrDefault(story.Publisher);
            if (count >= Math.Max(5, target / 6)) continue;
            selected.Add(story);
            publishers[story.Publisher] = count + 1;
        }
        log?.Invoke("Gaming News worldwide discovery: " + topics.Count +
            " recent stories; " + selected.Count + " selected across " +
            publishers.Count + " publishers.");
        return selected;
    }
}
