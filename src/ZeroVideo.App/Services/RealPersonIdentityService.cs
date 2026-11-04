using System.Text.RegularExpressions;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Match only user-approved identities actually depicted in the shot.
/// This never creates portraits from narration or interprets place names
/// as people. In particular, evidence and landscape cutaways are excluded.
/// </summary>
public static class RealPersonIdentityService
{
    private static readonly Regex HumanVisual = new(
        @"\b(portrait|face|facial|close.?up|headshot|person|man|woman|people|" +
        @"figure|human|detective|investigator|suspect|killer|victim|witness|" +
        @"doctor|journalist|reporter|host|actor|beard|hair|eyes|mouth|" +
        @"standing|walking|sitting|speaking|holding|wearing)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant);

    private static readonly Regex NoHuman = new(
        @"\b(no (?:visible )?(?:people|person|humans|characters|face)|" +
        @"without (?:any )?(?:people|person|face|human)|" +
        @"unoccupied|deserted|empty (?:room|house|building|trailer|street)|" +
        @"evidence.?only|object.?only|landscape.?only|architecture.?only)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant);

    public static bool HasVisiblePerson(ProjectScene scene)
    {
        var visual = (scene.ImagePrompt ?? "") + " " +
                     (scene.VisualQuery ?? "");
        return !NoHuman.IsMatch(visual) && HumanVisual.IsMatch(visual);
    }

    private static bool Mentions(string text, string name) =>
        Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" +
            Regex.Escape(name) + @"(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static RealPersonIdentity? Match(
        VideoProject project, ProjectScene scene)
    {
        if (!project.Options.EnableRealPersonIdentityLock)
            return null;
        var people = (project.RealPeople ?? []).Where(p =>
            p.Enabled && !string.IsNullOrWhiteSpace(p.Name)).ToList();
        var visual = (scene.ImagePrompt ?? "") + " " +
                     (scene.VisualQuery ?? "");
        // Visual explicitly naming a person takes priority over narration.
        // A named subject in the actual visual prompt is strong evidence
        // even if the prompt says "Zak Bagans in a hallway" without "man".
        // Explicit empty/evidence-only instructions still override it.
        if (NoHuman.IsMatch(visual)) return null;
        var inPicture = people.Where(p => Mentions(visual, p.Name.Trim())).ToList();
        if (inPicture.Count == 1) return inPicture[0];
        if (inPicture.Count > 1 || !HumanVisual.IsMatch(visual)) return null;
        var narrated = people.Where(p =>
            Mentions(scene.Narration ?? "", p.Name.Trim())).ToList();
        return narrated.Count == 1 ? narrated[0] : null;
    }

    public static IReadOnlyList<string> ApprovedReferences(
        RealPersonIdentity identity)
    {
        return (identity.ReferencePaths ?? [])
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                (Path.GetExtension(path).Equals(".jpg",StringComparison.OrdinalIgnoreCase) ||
                 Path.GetExtension(path).Equals(".jpeg",StringComparison.OrdinalIgnoreCase) ||
                 Path.GetExtension(path).Equals(".png",StringComparison.OrdinalIgnoreCase)) &&
                File.Exists(path) && new FileInfo(path).Length is >= 1024 and <= 25_000_000)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3).ToList();
    }

    public static string ReferenceFolder(VideoProject project) =>
        Path.Combine(project.ProjectDirectory, "RealPersonReferences");
}
