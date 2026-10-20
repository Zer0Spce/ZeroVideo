using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public static class ProjectMedia
{
    public const int CurrentGeneratedAssetVersion = 2;
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".avif", ".mp4", ".mov", ".mkv", ".webm", ".avi"];
    private static readonly string[] GeneratedImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".avif"];

    public static string? Find(VideoProject project, ProjectChapter chapter, ProjectScene scene)
        => FindAll(project, chapter, scene).FirstOrDefault();

    public static IReadOnlyList<string> FindAll(VideoProject project, ProjectChapter chapter, ProjectScene scene)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfExists(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var path = Path.IsPathRooted(raw) ? raw : Path.Combine(project.ProjectDirectory, raw);
            if (!File.Exists(path)) return;
            path = Path.GetFullPath(path);
            if (seen.Add(path)) result.Add(path);
        }

        AddIfExists(scene.MediaPath);
        foreach (var path in scene.MediaPaths) AddIfExists(path);

        if (string.IsNullOrWhiteSpace(project.ProjectDirectory)) return result;
        var mediaDir = Path.Combine(project.ProjectDirectory, "media");
        if (!Directory.Exists(mediaDir)) return result;

        var stem = $"c{chapter.Number:00}-s{scene.Number:000}";
        foreach (var file in Directory.EnumerateFiles(mediaDir)
                     .Where(f => IsSceneMediaFile(Path.GetFileNameWithoutExtension(f), stem))
                     .OrderBy(SceneVariantSortKey))
            AddIfExists(file);

        // Legacy scene-only naming remains supported.
        var legacyStem = $"{scene.Number:000}";
        foreach (var ext in Extensions)
            AddIfExists(Path.Combine(mediaDir, legacyStem + ext));

        return result;
    }

    public static IReadOnlyList<string> FindGeneratedImages(VideoProject project, ProjectChapter chapter, ProjectScene scene)
    {
        if (string.IsNullOrWhiteSpace(project.ProjectDirectory)) return [];
        var mediaDir = Path.Combine(project.ProjectDirectory, "media");
        if (!Directory.Exists(mediaDir)) return [];

        var stem = $"c{chapter.Number:00}-s{scene.Number:000}";
        return Directory.EnumerateFiles(mediaDir)
            .Where(f => GeneratedImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => IsSceneMediaFile(Path.GetFileNameWithoutExtension(f), stem))
            .OrderBy(SceneVariantSortKey)
            .ToList();
    }

    public static int CountGeneratedImages(VideoProject project)
    {
        if (string.IsNullOrWhiteSpace(project.ProjectDirectory)) return 0;
        var mediaDir = Path.Combine(project.ProjectDirectory, "media");
        if (!Directory.Exists(mediaDir)) return 0;

        return Directory.EnumerateFiles(mediaDir)
            .Count(f => GeneratedImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                        && Path.GetFileNameWithoutExtension(f).StartsWith("c", StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileNameWithoutExtension(f).Contains("-s", StringComparison.OrdinalIgnoreCase));
    }

    public static string GeneratedImagePath(VideoProject project, ProjectChapter chapter, ProjectScene scene, int variant = 1)
    {
        var mediaDir = Path.Combine(project.ProjectDirectory, "media");
        Directory.CreateDirectory(mediaDir);
        var stem = $"c{chapter.Number:00}-s{scene.Number:000}";
        return variant <= 1
            ? Path.Combine(mediaDir, stem + ".png")
            : Path.Combine(mediaDir, $"{stem}-v{variant:00}.png");
    }

    public static void DeleteGeneratedImageVariants(VideoProject project, ProjectChapter chapter, ProjectScene scene)
    {
        if (string.IsNullOrWhiteSpace(project.ProjectDirectory)) return;
        var mediaDir = Path.Combine(project.ProjectDirectory, "media");
        if (!Directory.Exists(mediaDir)) return;

        var stem = $"c{chapter.Number:00}-s{scene.Number:000}";
        foreach (var path in Directory.EnumerateFiles(mediaDir)
                     .Where(f => GeneratedImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                     .Where(f => IsSceneMediaFile(Path.GetFileNameWithoutExtension(f), stem))
                     .ToList())
        {
            try { File.Delete(path); } catch { }
        }

        if (!string.IsNullOrWhiteSpace(scene.MediaPath))
        {
            try
            {
                var full = Path.GetFullPath(Path.IsPathRooted(scene.MediaPath) ? scene.MediaPath : Path.Combine(project.ProjectDirectory, scene.MediaPath));
                if (IsGeneratedScenePath(full, mediaDir, stem)) scene.MediaPath = null;
            }
            catch { }
        }

        scene.MediaPaths = scene.MediaPaths
            .Where(p =>
            {
                try
                {
                    var full = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(project.ProjectDirectory, p));
                    return !IsGeneratedScenePath(full, mediaDir, stem);
                }
                catch { return true; }
            })
            .ToList();
    }

    private static bool IsSceneMediaFile(string fileStem, string sceneStem)
        => fileStem.Equals(sceneStem, StringComparison.OrdinalIgnoreCase)
           || fileStem.StartsWith(sceneStem + "-v", StringComparison.OrdinalIgnoreCase);

    private static string SceneVariantSortKey(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var marker = stem.LastIndexOf("-v", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return "0000-" + stem;
        var suffix = stem[(marker + 2)..];
        return int.TryParse(suffix, out var value) ? $"{value:0000}-{stem}" : "9999-" + stem;
    }

    private static bool IsGeneratedScenePath(string fullPath, string mediaDir, string stem)
    {
        var projectMedia = Path.GetFullPath(mediaDir) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(projectMedia, StringComparison.OrdinalIgnoreCase)) return false;
        if (!GeneratedImageExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase)) return false;
        return IsSceneMediaFile(Path.GetFileNameWithoutExtension(fullPath), stem);
    }
}
