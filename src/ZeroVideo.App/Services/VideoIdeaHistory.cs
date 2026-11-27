using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZeroVideo.App.Services;

/// <summary>Local, cross-tab topic history. No external account or API required.</summary>
public sealed class VideoIdeaHistory
{
    public sealed record Entry(string Niche, string Title, string Hook);
    private readonly string _path;
    private readonly List<Entry> _entries = [];
    private static readonly HashSet<string> Filler = new(
        ("a an the of in on at for to by from with and or but is are was were be been being " +
         "how why what when where who which this that these those it its they their you your " +
         "did do does has have had can could would should will about into out over under " +
         "more new fresh ideas idea topic topics videos video shocking secret secrets " +
         "truth untold hidden surprising revealed reveal really ever finally actually").Split(' '),
        StringComparer.OrdinalIgnoreCase);

    public VideoIdeaHistory(string directory)
    {
        _path = Path.Combine(directory, "video-idea-history-v1.json");
        if (File.Exists(_path))
            _entries.AddRange(JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path)) ?? []);
        // Migrate prior successful structured responses captured by ZeroVideo.
        // This also recovers ideas shown by older versions, when available.
        var old = Path.Combine(directory, "chatgpt-json");
        if (Directory.Exists(old))
        {
            foreach (var file in Directory.EnumerateFiles(old, "*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc).Take(1200))
            {
                try
                {
                    if (new FileInfo(file).Length > 500_000) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (Read(root, "response_type") != "zerovideo_ideas_v1") continue;
                    var niche = Read(root, "niche");
                    if (!root.TryGetProperty("ideas", out var items) ||
                        items.ValueKind != JsonValueKind.Array) continue;
                    foreach (var item in items.EnumerateArray())
                    {
                        var title = Read(item, "title");
                        var hook = Read(item, "hook");
                        if (title.Length > 0 && hook.Length > 0) Add(new(niche, title, hook));
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static string Read(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";

    private static string Canon(string s)
    {
        s = s.ToLowerInvariant();
        return s.Length > 4 && s.EndsWith("s") && !s.EndsWith("ss") ? s[..^1] : s;
    }
    private static HashSet<string> Words(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(m => Canon(m.Value)).Where(w => w.Length > 1 && !Filler.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static string Key(string s) =>
        string.Join(" ", Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => m.Value));

    public static bool RelatedNiche(string a, string b)
    {
        if (Key(a) == Key(b)) return true;
        var x = Words(a); var y = Words(b);
        if (x.Count == 0 || y.Count == 0) return false;
        return x.Intersect(y).Count() >= Math.Max(1, Math.Min(x.Count, y.Count) - 1);
    }

    private static bool Similar(HashSet<string> a, HashSet<string> b, int minimum, double fraction)
    {
        var common = a.Intersect(b).Count();
        return common >= minimum && common >= Math.Min(a.Count, b.Count) * fraction;
    }

    public static bool IsDuplicate(Entry a, Entry b)
    {
        if (Key(a.Title) == Key(b.Title)) return true;
        var ta = Words(a.Title); var tb = Words(b.Title);
        if (ta.SetEquals(tb) && ta.Count > 0) return true;
        if (Similar(ta, tb, 3, .72)) return true;
        if (!RelatedNiche(a.Niche, b.Niche)) return false;
        return Similar(ta, tb, 2, .48) && Similar(Words(a.Hook), Words(b.Hook), 4, .52);
    }

    public IReadOnlyList<Entry> ForNiche(string niche) =>
        _entries.Where(e => RelatedNiche(e.Niche, niche)).TakeLast(250).ToArray();
    public bool Contains(Entry candidate) => _entries.Any(e => IsDuplicate(e, candidate));

    public void Add(Entry entry)
    {
        // Keep original title and hook as prompt context; retain all history
        // rather than evicting older titles after a few batches.
        if (!_entries.Any(e => Key(e.Title) == Key(entry.Title)))
            _entries.Add(entry);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
        File.Move(temp, _path, true);
    }
}
