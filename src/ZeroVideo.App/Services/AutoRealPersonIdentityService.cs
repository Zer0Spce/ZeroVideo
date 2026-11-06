using System.Text.RegularExpressions;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Per-video temporary celebrity/real-person *illustration* guides.
/// All destructive operations are limited to paths explicitly recorded as
/// temporary outputs inside this project's dedicated identity directory.
/// Approved/manual source photographs are never deleted.
/// </summary>
public static class AutoRealPersonIdentityService
{
    private static readonly Regex LeadingName = new(
        @"^(?<name>[A-Za-z][A-Za-z.'-]*(?:\s+[A-Za-z][A-Za-z.'-]*){1,2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> BadFirstWords = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "The", "A", "An", "How", "Why", "What", "When", "Where",
        "Inside", "Top", "Best", "True", "Horror", "Ghost", "Serial",
        "Mystery", "Paranormal", "Haunted", "Unsolved", "America",
        "Killer", "Crime", "Celebrity", "Unknown", "Detective",
        "Missing", "Famous", "Real", "Dark"
    };
    private static readonly HashSet<string> BadNameWords = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Story", "Stories", "Biography", "Documentary", "Case",
        "Cases", "Investigation", "Mystery", "Murder", "Murders",
        "History", "House", "Haunting", "Killer", "Crime", "Horror",
        "Ghost", "Evidence", "Video", "Episode", "Explained",
        "Trailer", "Footage", "Secrets", "Files", "Theory",
        "Attack", "Night", "New", "York", "River", "California"
    };

    /// <summary>
    /// Conservative headline parsing, not a claim that a person exists.
    /// Only simple two/three-token headline subjects qualify; named cases
    /// such as "The Zodiac Killer" and lists are deliberately skipped.
    /// </summary>
    public static string? DetectMainPerson(VideoProject project)
    {
        foreach (var raw in new[] { project.Title, project.Topic })
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                raw.StartsWith("Untitled ZeroVideo", StringComparison.OrdinalIgnoreCase))
                continue;
            var first = Regex.Split(raw.Trim(),
                @"\s+[—–-]\s+|:\s+|\s+\|\s+")[0].Trim();
            first = Regex.Replace(first,
                @"\s+(story|biography|documentary)$", "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
            var match = LeadingName.Match(first);
            if (!match.Success) continue;
            var tokens = match.Groups["name"].Value.Split(' ',
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length is < 2 or > 3 ||
                BadFirstWords.Contains(tokens[0]) ||
                tokens.Any(BadNameWords.Contains))
                continue;
            return string.Join(" ", tokens.Select(token =>
                char.ToUpperInvariant(token[0]) + token[1..]));
        }
        return null;
    }

    private static string SafeSlug(string value)
    {
        var slug = Regex.Replace(value, @"[^A-Za-z0-9_-]", "_");
        return slug.Length == 0 ? "Unknown" : slug[..Math.Min(slug.Length, 80)];
    }

    public static string TemporaryDirectory(VideoProject project)
    {
        if (string.IsNullOrWhiteSpace(project.ProjectDirectory))
            throw new InvalidOperationException(
                "Save the project before generating temporary identity portraits.");
        return Path.Combine(project.ProjectDirectory, "RealPersonReferences",
            ".ZeroVideoTemporary", SafeSlug(project.Id));
    }

    public static bool IsSafeTemporaryPath(VideoProject project, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var root = Path.GetFullPath(TemporaryDirectory(project)) +
                Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            // References belong in the project-specific temp directory, and
            // must carry the marker written by the ChatGPT image saver.
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("AI-APPROXIMATE-",
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetExtension(full).Equals(".png",
                    StringComparison.OrdinalIgnoreCase))
                return false;
            var projectRoot = Path.GetFullPath(project.ProjectDirectory);
            if (!root.StartsWith(projectRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                return false;
            // Do not follow directory symlinks/junctions, even for a path
            // that appears lexically beneath our project root.
            for (var dir = Path.GetDirectoryName(full); dir is not null;
                 dir = Path.GetDirectoryName(dir))
            {
                if (Directory.Exists(dir) &&
                    (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    return false;
                if (string.Equals(dir, projectRoot,
                    StringComparison.OrdinalIgnoreCase))
                    break;
            }
            if (File.Exists(full) &&
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                return false;
            return true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public static RealPersonIdentity GetOrCreate(
        VideoProject project, string name)
    {
        project.RealPeople ??= [];
        var profile = project.RealPeople.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (profile is not null) return profile;
        profile = new RealPersonIdentity
        {
            Name = name,
            AutoCreatedForVideo = true
        };
        project.RealPeople.Add(profile);
        return profile;
    }

    public static void AttachTemporary(
        VideoProject project, RealPersonIdentity profile, string path)
    {
        if (!IsSafeTemporaryPath(project, path) || !File.Exists(path) ||
            new FileInfo(path).Length < 1024)
            throw new InvalidDataException(
                "Auto identity reference must be a saved, nonempty PNG in " +
                "this project's dedicated temporary directory.");
        profile.ReferencePaths ??= [];
        profile.AiApproximateReferencePaths ??= [];
        profile.AutoTemporaryReferencePaths ??= [];
        if (profile.ReferencePaths.Count >= 3 &&
            !profile.ReferencePaths.Contains(path,
                StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "This real-person profile already has three references.");
        if (!profile.ReferencePaths.Contains(path,
            StringComparer.OrdinalIgnoreCase))
            profile.ReferencePaths.Add(path);
        if (!profile.AiApproximateReferencePaths.Contains(path,
            StringComparer.OrdinalIgnoreCase))
            profile.AiApproximateReferencePaths.Add(path);
        if (!profile.AutoTemporaryReferencePaths.Contains(path,
            StringComparer.OrdinalIgnoreCase))
            profile.AutoTemporaryReferencePaths.Add(path);
    }

    public static int ValidTemporaryCount(
        VideoProject project, RealPersonIdentity profile)
    {
        return (profile.AutoTemporaryReferencePaths ?? [])
            .Where(p => IsSafeTemporaryPath(project, p) &&
                File.Exists(p) && new FileInfo(p).Length > 1024)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    public sealed record CleanupReport(int Deleted, int Failed,
        int Detached, string Detail);

    /// <summary>
    /// Detach and remove ONLY explicitly tracked auto-temporary files.
    /// This may run after a confirmed finished MP4, or when the subject
    /// changed inside the same project. Failures retain their tracking
    /// records so resume can retry cleanup safely.
    /// </summary>
    public static CleanupReport Cleanup(
        VideoProject project, Action<string>? log = null)
    {
        var deleted = 0;
        var failed = 0;
        var detached = 0;
        var messages = new List<string>();
        foreach (var profile in (project.RealPeople ?? []).ToList())
        {
            profile.ReferencePaths ??= [];
            profile.AiApproximateReferencePaths ??= [];
            profile.AutoTemporaryReferencePaths ??= [];
            foreach (var path in profile.AutoTemporaryReferencePaths.ToList())
            {
                if (!IsSafeTemporaryPath(project, path))
                {
                    failed++;
                    messages.Add("Unsafe temporary reference path was NOT deleted: " +
                        path);
                    continue;
                }
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        deleted++;
                    }
                    profile.ReferencePaths.RemoveAll(p =>
                        string.Equals(p,path,StringComparison.OrdinalIgnoreCase));
                    profile.AiApproximateReferencePaths.RemoveAll(p =>
                        string.Equals(p,path,StringComparison.OrdinalIgnoreCase));
                    profile.AutoTemporaryReferencePaths.RemoveAll(p =>
                        string.Equals(p,path,StringComparison.OrdinalIgnoreCase));
                    detached++;
                }
                catch (Exception error) when (error is IOException or
                    UnauthorizedAccessException)
                {
                    failed++;
                    messages.Add("Unable to delete " + Path.GetFileName(path) +
                        ": " + error.Message);
                }
            }
            if (profile.AutoCreatedForVideo &&
                profile.AutoTemporaryReferencePaths.Count == 0 &&
                profile.ReferencePaths.Count == 0)
                project.RealPeople.Remove(profile);
        }
        if ((project.RealPeople ?? []).All(p =>
            p.AutoTemporaryReferencePaths?.Count == 0))
        {
            if (project.AutoIdentityEnabledByVideo)
                project.Options.EnableRealPersonIdentityLock = false;
            project.AutoIdentityEnabledByVideo = false;
            project.ActiveAutoIdentityPerson = "";
        }
        var detail = "Temporary identity cleanup: " + deleted +
            " auto portrait file(s) deleted, " + detached +
            " reference(s) detached, " + failed + " issue(s). " +
            "Approved/manual photos, video, scene PNGs and backups untouched.";
        log?.Invoke(detail);
        foreach (var message in messages) log?.Invoke(message);
        return new CleanupReport(deleted,failed,detached,detail);
    }
}
