using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZeroVideo.App.Services;

/// <summary>
/// Named screencap galleries are research/review sources, not "free stock".
/// Never scrape or download copyrighted episode stills automatically.
/// The user may supply a specific, rights-reviewed still for a narration scene
/// with a human-written rights-basis file.
/// </summary>
public static class YouTubeFlowScreencapReviewService
{
    public static readonly IReadOnlyDictionary<string, string> GalleryUrls =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [YouTubeFlowSourceRouter.SpongeBob] = "https://thesbcommunity.com/captures/",
            [YouTubeFlowSourceRouter.AnimationScreencaps] = "https://animationscreencaps.com/",
            [YouTubeFlowSourceRouter.Intanibase] = "https://www.intanibase.com/iad_screenshots/index",
            [YouTubeFlowSourceRouter.Bulbagarden] =
                "https://archives.bulbagarden.net/wiki/Category:Pok%C3%A9mon_animated_series_screenshots"
        };

    public static async Task WriteReviewQueueAsync(
        string folder, string videoTitle,
        IReadOnlyList<PixabayFootageScene> scenes,
        IReadOnlyList<string> enabledSources, CancellationToken ct)
    {
        var sources = enabledSources.Where(YouTubeFlowSourceRouter.IsReviewOnly)
            .Distinct().ToArray();
        if (sources.Length == 0) return;
        var target = Path.Combine(folder, "approved-screencaps");
        Directory.CreateDirectory(target);
        var queue = new
        {
            title = videoTitle,
            notice = "Cartoon screencaps are usually copyrighted; enabling a gallery " +
                "does NOT authorize automated downloading or reuse. Review copyright, " +
                "permission or a jurisdiction-specific fair-use analysis before import.",
            approval_instructions =
                "To provide an approved still for a scene, save a PNG/JPG as " +
                "scene-01-{source-id}.png in approved-screencaps, and add " +
                "scene-01-{source-id}.json with " +
                "{\"approvedForUse\":true,\"sourceUrl\":\"https://...\", " +
                "\"usageBasis\":\"Your documented rights justification\", " +
                "\"credit\":\"Creator or rights holder\"}. Files lacking approval are ignored.",
            galleries = sources.Select(id => new { id, url = GalleryUrls[id] }).ToArray(),
            scenes = scenes.Select(scene => new
            {
                scene.SceneNumber, scene.NarrationExcerpt, scene.SearchQuery, scene.Mood,
                suggested_sources = sources
            })
        };
        await File.WriteAllTextAsync(Path.Combine(folder, "screencap-review.json"),
            JsonSerializer.Serialize(queue, new JsonSerializerOptions { WriteIndented = true }), ct);
        var readme = new StringBuilder(
            "CARTOON EPISODE SCREENSHOTS — MANUAL COPYRIGHT REVIEW REQUIRED\n\n");
        readme.AppendLine("ZeroVideo will not scrape, bulk-download, or presume episode " +
            "screenshots are in the public domain. Select only frames for which " +
            "you have permission or a reviewed legal basis. The user is " +
            "responsible for reuse rights. Attribution alone does not grant " +
            "rights to copyrighted images.\n");
        foreach (var id in sources)
            readme.AppendLine($"{id}: {GalleryUrls[id]}");
        readme.AppendLine("\nReview each scene in screencap-review.json.");
        readme.AppendLine("Example: scene-09-spongebob-captures.png + " +
            "scene-09-spongebob-captures.json (approvedForUse, sourceUrl, usageBasis, credit)");
        await File.WriteAllTextAsync(Path.Combine(target, "README.txt"),
            readme.ToString(), ct);
    }

    public static IReadOnlyList<PixabayFlowClip> FindApprovedImages(
        string folder, PixabayFootageScene scene, string source)
    {
        if (!YouTubeFlowSourceRouter.IsReviewOnly(source)) return [];
        var import = Path.Combine(folder, "approved-screencaps");
        if (!Directory.Exists(import)) return [];
        var stem = $"scene-{scene.SceneNumber:00}-{source}";
        var metadataPath = Path.Combine(import, stem + ".json");
        if (!File.Exists(metadataPath)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("approvedForUse", out var approved) ||
                approved.ValueKind != JsonValueKind.True) return [];
            static string Read(JsonElement v, string key) =>
                v.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()?.Trim() ?? "" : "";
            var sourceUrl = Read(root, "sourceUrl");
            var usageBasis = Read(root, "usageBasis");
            var credit = Read(root, "credit");
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || usageBasis.Length < 12 ||
                string.IsNullOrWhiteSpace(credit)) return [];
            // A chosen gallery is an evidence location, not permission.
            // Require the source URL to match the explicit selected gallery.
            var expected = new Uri(GalleryUrls[source]).Host;
            if (!uri.Host.Equals(expected, StringComparison.OrdinalIgnoreCase)) return [];
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg" })
            {
                var path = Path.Combine(import, stem + extension);
                if (!File.Exists(path) || new FileInfo(path).Length < 4096) continue;
                var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(
                    source + "|" + scene.SceneNumber + "|" + sourceUrl));
                var id = BitConverter.ToInt64(idBytes, 0) & long.MaxValue;
                // Other providers can use positive IDs; avoid collisions.
                return [new PixabayFlowClip(-2_000_000_000L - (id % 1_000_000_000L),
                    path, sourceUrl, scene.SearchQuery, scene.NarrationExcerpt,
                    true, credit, "User-reviewed screenshot: " + usageBasis,
                    "", source)];
            }
        }
        catch (Exception) { /* Invalid or unfinished approval: ignore. */ }
        return [];
    }
}
