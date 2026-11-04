using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Identity-only guidance for explicitly visible recurring people. Never
/// treat an entire generated background/vehicle/building as a character.
/// </summary>
public static class CharacterConsistencyService
{
    private static readonly Regex PersonNames = new(
        @"(?<![\p{L}])[A-Z][a-z]{2,}(?:\s+[A-Z][a-z]{2,}){1,2}(?![\p{L}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] Roles =
    [
        "detective", "investigator", "journalist", "reporter", "scientist",
        "police officer", "lawyer", "doctor", "historian", "narrator", "host"
    ];

    private static readonly HashSet<string> Rejected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Comic Book", "Cinematic Comic", "Editorial Cartoon", "Dark Night",
        "New York", "Los Angeles", "Chapter One", "Chapter Two", "Scene One",
        "The Detective", "The Investigator", "The Police", "The Man", "The Woman",
        "The Story", "United States", "Black And", "Long Form", "True Crime",
        "Vintage Comic", "Modern Comic", "Detective Comic", "Neo Noir"
    };

    // False-positive proper names include places, objects and scene titles.
    // They should never become recurring visual character anchors.
    private static readonly HashSet<string> NonHumanWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Trailer", "Caravan", "Vehicle", "House", "Cabin", "Building",
        "Apartment", "Room", "Street", "Road", "Highway", "Map", "Court",
        "Courthouse", "Hospital", "School", "Station", "Forest", "Desert",
        "Mountain", "River", "Lake", "Beach", "Island", "Valley", "City",
        "County", "State", "Country", "California", "Texas", "Nevada",
        "America", "England", "Evidence", "Letter", "Newspaper", "Photo",
        "Photograph", "Video", "Comic", "Scene", "Chapter", "Story",
        "Documentary", "Night", "Morning", "Afternoon", "Evening"
    };

    private static readonly Regex HumanVisualCue = new(
        @"\b(person|people|human|man|woman|men|women|boy|girl|child|teen|" +
        @"adult|face|facial|portrait|headshot|figure|character|silhouette|" +
        @"detective|investigator|reporter|officer|doctor|nurse|lawyer|" +
        @"suspect|killer|murderer|victim|witness|driver|actor|actress|" +
        @"standing|sitting|walking|speaking|wearing|holding|looking)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);

    private static readonly Regex NoHumanVisualCue = new(
        @"\b(no (?:visible )?(?:people|person|humans|characters)|" +
        @"without (?:any )?(?:people|person|humans|characters)|" +
        @"unoccupied|deserted|empty (?:house|room|trailer|vehicle|street)|" +
        @"exterior-only|landscape-only|architecture-only)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);

    private const string LegacyAppearancePrefix =
        "Keep the same recognizable face, hair, apparent age, wardrobe, " +
        "accessories and distinctive features in every scene. ";

    private static string VisualText(ProjectScene scene) =>
        (scene.ImagePrompt ?? "") + " " + (scene.VisualQuery ?? "");

    private static bool HasVisibleHuman(ProjectScene scene)
    {
        var visual = VisualText(scene);
        return HumanVisualCue.IsMatch(visual) &&
            !NoHumanVisualCue.IsMatch(visual);
    }

    private static bool Mentions(string text, string name) =>
        Regex.IsMatch(text, @"(?<!\w)" + Regex.Escape(name) + @"(?!\w)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string IdentityDescription(ProjectCharacter character)
    {
        var appearance = character.Appearance?.Trim() ?? "";
        // Earlier auto-detection appended the complete first scene prompt to
        // the saved appearance, accidentally forcing its original composition.
        if (appearance.StartsWith(LegacyAppearancePrefix,
            StringComparison.OrdinalIgnoreCase))
            return LegacyAppearancePrefix.TrimEnd();
        return appearance;
    }

    public static IReadOnlyList<ProjectCharacter> EnsureProfiles(VideoProject project)
    {
        project.Characters ??= [];
        if (!project.Options.EnableCharacterConsistency) return project.Characters;

        var appearances = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        foreach (var scene in project.Chapters.SelectMany(chapter => chapter.Scenes))
        {
            if (!HasVisibleHuman(scene)) continue;
            var narration = scene.Narration ?? "";
            var visual = VisualText(scene);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match hit in PersonNames.Matches(narration))
            {
                var candidate = hit.Value.Trim();
                if (candidate.Length > 45 || Rejected.Contains(candidate) ||
                    candidate.StartsWith("The ", StringComparison.Ordinal) ||
                    candidate.StartsWith("One ", StringComparison.Ordinal) ||
                    candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Any(NonHumanWords.Contains))
                    continue;

                // A person should be the visible subject, not a name on an
                // evidence document or only a figure referenced in narration.
                if (Mentions(visual, candidate))
                    names.Add(candidate);
                else if (HumanVisualCue.IsMatch(visual) &&
                         HumanVisualCue.IsMatch(narration))
                    names.Add(candidate);
            }

            foreach (var role in Roles)
                if (Mentions(visual, role))
                    names.Add(char.ToUpperInvariant(role[0]) + role[1..]);

            foreach (var name in names)
                appearances[name] = appearances.GetValueOrDefault(name) + 1;
        }

        foreach (var entry in appearances.Where(pair => pair.Value >= 2)
            .OrderByDescending(pair => pair.Value).Take(6))
        {
            if (project.Characters.Any(c => c.Name.Equals(entry.Key,
                StringComparison.OrdinalIgnoreCase))) continue;
            project.Characters.Add(new ProjectCharacter
            {
                Name = entry.Key,
                Appearance = LegacyAppearancePrefix.TrimEnd(),
                Seed = StableSeed(project.Id + "|" + entry.Key)
            });
        }
        foreach (var character in project.Characters)
            if (character.Seed <= 0)
                character.Seed = StableSeed(project.Id + "|" + character.Name);
        return project.Characters;
    }

    public static IReadOnlyList<ProjectCharacter> Match(
        VideoProject project, ProjectScene scene)
    {
        if (!project.Options.EnableCharacterConsistency || !HasVisibleHuman(scene))
            return [];

        var profiles = (project.Characters ?? []).Where(profile =>
            profile.Enabled && !string.IsNullOrWhiteSpace(profile.Name)).ToList();
        var visual = VisualText(scene);
        // Explicitly named visual subjects take precedence.
        var visible = profiles.Where(profile =>
            Mentions(visual, profile.Name.Trim())).ToList();
        if (visible.Count > 0) return visible;

        // If only the narration names the person, apply identity guidance
        // only when ONE named person is present. Avoid combining identities.
        var narrated = profiles.Where(profile =>
            Mentions(scene.Narration ?? "", profile.Name.Trim())).ToList();
        return narrated.Count == 1 ? narrated : [];
    }

    public static long StableSeed(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return (long)(BitConverter.ToUInt64(hash, 0) & 0x7fffffffffffffffUL) + 1;
    }

    public static string AnchorDirectory(VideoProject project) =>
        Path.Combine(project.ProjectDirectory, "CharacterReferences");

    // Legacy compatibility only. New image generation never auto-captures
    // whole scene pictures as character portraits.
    public static string CaptureFirstReference(VideoProject project,
        ProjectCharacter profile, string generatedPng)
    {
        if (!File.Exists(generatedPng))
            throw new FileNotFoundException("Scene image missing", generatedPng);
        if (string.IsNullOrWhiteSpace(project.ProjectDirectory))
            throw new InvalidOperationException("Project must be saved first.");
        var folder = AnchorDirectory(project);
        Directory.CreateDirectory(folder);
        var safe = Regex.Replace(profile.Name.Trim(), @"[^A-Za-z0-9_-]", "_");
        if (safe.Length == 0) safe = "Character";
        var target = Path.Combine(folder, safe + "-" + profile.Seed + ".png");
        if (!File.Exists(target)) File.Copy(generatedPng, target);
        profile.ReferenceImagePath = target;
        return target;
    }

    public static string? GetExistingReference(
        VideoProject project, ProjectCharacter character)
    {
        var path = character.ReferenceImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var safe = Regex.Replace(character.Name.Trim(), @"[^A-Za-z0-9_-]", "_");
        if (safe.Length == 0) safe = "Character";
        var legacy = Path.Combine(AnchorDirectory(project),
            safe + "-" + character.Seed + ".png");
        // Ignore automatically captured complete scenes from older builds.
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(legacy),
            StringComparison.OrdinalIgnoreCase))
            return null;

        // Deliberately imported character photos remain usable. Do not
        // silently select an earlier scene's full image as a substitute.
        return Path.GetFullPath(path);
    }
}
