using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZeroVideo.App.Services;

// Select real, metadata-grounded chapters; never guess timestamps.
public sealed class YouTubeFlowClipService(ProcessRunner runner)
{
    private static string Q(string s) => "\"" + s.Replace("\"", "") + "\"";
    private static string S(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double N(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : 0;
    private static string F(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
    private static HashSet<string> Words(string s) => Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}]+")
        .Select(m => m.Value).Where(w => w.Length > 2 && !new[]{"the","and","with","for","from","video","footage","how","what"}.Contains(w)).ToHashSet();
    // Require named franchise identity in the actual video title, never in
    // tags/description alone. Reject commentary and unrelated derivative formats.
    private static readonly string[] FranchisePatterns = [
        @"\bmickey mouse\b", @"\bdonald duck\b", @"\bminnie mouse\b",
        @"\bgoofy\b", @"\bpluto\b", @"\bpeppa pig\b", @"\bspongebob\b",
        @"\blooney tunes\b", @"\btom (?:and|&) jerry\b", @"\bsimpsons\b",
        @"\bpok[eé]mon\b", @"\bben 10\b", @"\bpowerpuff girls\b",
        @"\bscooby[ -]doo\b", @"\bgarfield\b", @"\bbugs bunny\b",
        @"\bdora the explorer\b", @"\bsteamboat willie\b"
    ];
    private static string[] Anchors(string text) => FranchisePatterns
        .Where(p => Regex.IsMatch(text, p, RegexOptions.IgnoreCase)).ToArray();
    private static readonly HashSet<string> GenericVisualWords = new([
        "cartoon","cartoons","animated","animation","movie","movies","film","films",
        "classic","vintage","old","original","collection","episode","episodes",
        "character","characters","smiling","smile","dark","calm","history","banned",
        "forgotten","television","disney","clip","clips","scene","scenes","compilation",
        "full","official","short","shorts","documentary","channel","era"
    ], StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> Specific(string s) => Words(s)
        .Where(w => !GenericVisualWords.Contains(w)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public static bool IsTopicMatch(string documentaryTitle, PixabayFootageScene scene,
        string videoTitle, string chapterTitle = "")
    {
        if (string.IsNullOrWhiteSpace(videoTitle)) return false;
        var sceneAnchors = Anchors(scene.NarrationExcerpt + " " + scene.SearchQuery);
        var titleAnchors = Anchors(documentaryTitle);
        var required = sceneAnchors.Length > 0 ? sceneAnchors : titleAnchors;
        if (required.Length > 0 && !required.Any(p =>
            Regex.IsMatch(videoTitle, p, RegexOptions.IgnoreCase))) return false;
        if (Regex.IsMatch(videoTitle, @"\b(reaction|reacts|review|explained|essay|gameplay|walkthrough|unboxing|cosplay|fan[ -]?made|parody|mashup|remix|ai[ -]generated|drawing tutorial|toy review|theme park|costume|karaoke)\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(scene.NarrationExcerpt, @"\b(reaction|review|gameplay|cosplay|parody|theme park|costume|toy)\b", RegexOptions.IgnoreCase))
            return false;
        var grounded = Specific(scene.SearchQuery);
        grounded.IntersectWith(Specific(scene.NarrationExcerpt));
        var metadata = Specific(videoTitle + " " + chapterTitle);
        var matched = grounded.Count(metadata.Contains);
        if (required.Length > 0)
        {
            // A scene naming an episode/action must match more than the franchise
            // name when there is another concrete grounded search term.
            var anchorWords = Specific(string.Join(" ", required));
            var extras = grounded.Where(w => !anchorWords.Contains(w)).ToArray();
            return extras.Length == 0 || extras.Any(metadata.Contains);
        }
        var topic = Specific(documentaryTitle);
        return grounded.Count > 0 && matched >= Math.Min(2, grounded.Count)
            && (topic.Count == 0 || topic.Any(metadata.Contains));
    }
    public async Task<IReadOnlyList<PixabayFlowClip>> AcquireAsync(
        PixabayFootageScene scene, int maximum, string folder, string ffmpeg, string documentaryTitle,
        Action<string>? log, CancellationToken ct)
    {
        var downloader = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        if (!File.Exists(downloader)) downloader = "yt-dlp";
        var query = Regex.Replace(scene.SearchQuery, @"[^\p{L}\p{N}\s-]", " ").Trim();
        var terms = Words(query);
        var anchors = Anchors(scene.NarrationExcerpt + " " + scene.SearchQuery);
        if (anchors.Length == 0) anchors = Anchors(documentaryTitle);
        var identity = anchors.Length == 0 ? "" : Regex.Match(scene.NarrationExcerpt + " " + scene.SearchQuery + " " + documentaryTitle, anchors[0], RegexOptions.IgnoreCase).Value;
        if (identity.Length > 0 && !query.Contains(identity, StringComparison.OrdinalIgnoreCase)) query = identity + " " + query;
        if (terms.Count < 2) return [];
        var cache = Path.Combine(folder, "youtube-clips");
        Directory.CreateDirectory(cache);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("v2-topic-gate\n" + documentaryTitle + "\n" + scene.SearchQuery + "\n" + scene.NarrationExcerpt)))[..20];
        var manifest = Path.Combine(cache, key + ".json");
        var result = new List<PixabayFlowClip>();
        if (File.Exists(manifest))
        {
            try {
                var saved = JsonSerializer.Deserialize<List<PixabayFlowClip>>(await File.ReadAllTextAsync(manifest, ct)) ?? [];
                result.AddRange(saved.Where(c => File.Exists(c.FilePath) && new FileInfo(c.FilePath).Length > 4096).Take(maximum));
                if (result.Count >= maximum) return result;
            } catch (JsonException) { }
        }
        var search = await runner.RunAsync(downloader, "--flat-playlist --dump-single-json --skip-download --no-warnings --socket-timeout 15 --retries 1 " + Q("ytsearch10:" + query), ct: ct);
        if (search.ExitCode != 0) { log?.Invoke("YouTube search unavailable; trying next source."); return result; }
        using var searchDoc = JsonDocument.Parse(search.StdOut);
        if (!searchDoc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return result;
        foreach (var entry in entries.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (result.Count >= maximum) break;
            var id = S(entry, "id");
            if (!Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$")) continue;
            var url = "https://www.youtube.com/watch?v=" + id;
            var info = await runner.RunAsync(downloader, "--no-playlist --dump-single-json --skip-download --no-warnings --socket-timeout 15 --retries 1 " + Q(url), ct: ct);
            if (info.ExitCode != 0) continue;
            using var doc = JsonDocument.Parse(info.StdOut);
            var video = doc.RootElement;
            var channel = S(video, "channel");
            if (channel.Length == 0) channel = S(video, "uploader");
            if (channel.Length == 0 || N(video, "duration") <= 0) continue;
            var title = S(video, "title");
            var titleScore = Words(title).Intersect(terms).Count();
            if (!IsTopicMatch(documentaryTitle, scene, title) && !video.TryGetProperty("chapters", out _)) continue;
            var candidates = new List<(double Start, double End, int Score)>();
            if (video.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array)
            {
                foreach (var chapter in chapters.EnumerateArray())
                {
                    var score = Words(S(chapter, "title")).Intersect(terms).Count();
                    var start = N(chapter, "start_time");
                    var end = Math.Min(N(chapter, "end_time"), N(video, "duration"));
                    if (score >= 2 && start >= 0 && end - start >= 4 &&
                        IsTopicMatch(documentaryTitle, scene, title, S(chapter, "title")))
                        candidates.Add((start, Math.Min(end, start + 12), score));
                }
            }
            else if (titleScore == terms.Count && IsTopicMatch(documentaryTitle, scene, title) && N(video, "duration") is >= 4 and <= 20)
                candidates.Add((0, Math.Min(12, N(video, "duration")), titleScore));
            foreach (var cut in candidates.OrderByDescending(c => c.Score))
            {
                if (result.Count >= maximum) break;
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id + ":" + F(cut.Start) + ":" + F(cut.End)));
                var clipId = BitConverter.ToInt64(hash, 0) & 0x1FFFFFFFFFFFFFFF;
                clipId |= 0x2000000000000000;
                if (result.Any(c => c.Id == clipId)) continue;
                var file = Path.Combine(cache, id + "-" + F(cut.Start) + "-" + F(cut.End) + ".mp4");
                if (!File.Exists(file) || new FileInfo(file).Length <= 4096)
                {
                    var tmp = file + ".download.mp4";
                    var args = "--no-playlist --no-warnings --socket-timeout 20 --retries 1 --force-overwrites -f " +
                        Q("bv*[ext=mp4][height<=1080]/b[ext=mp4][height<=1080]") +
                        " --ffmpeg-location " + Q(ffmpeg) + " --download-sections " + Q("*" + F(cut.Start) + "-" + F(cut.End)) +
                        " --force-keyframes-at-cuts --merge-output-format mp4 -o " + Q(tmp) + " " + Q(url);
                    var downloaded = await runner.RunAsync(downloader, args, ct: ct);
                    if (downloaded.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length <= 4096)
                    { if (File.Exists(tmp)) File.Delete(tmp); continue; }
                    File.Move(tmp, file, true);
                }
                result.Add(new PixabayFlowClip(clipId, file, url + "&t=" + (int)cut.Start,
                    scene.SearchQuery, title + " | source range " + F(cut.Start) + "–" + F(cut.End) + " seconds",
                    false, channel, "Source permission not verified", "", "YouTube"));
                await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(result), ct);
                log?.Invoke($"YouTube scene {scene.SceneNumber}: {channel}, {F(cut.Start)}–{F(cut.End)}s.");
            }
        }
        return result;
    }
}
