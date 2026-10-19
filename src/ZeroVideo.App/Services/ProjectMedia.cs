using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public static class ProjectMedia
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".mp4", ".mov", ".mkv", ".webm", ".avi"];

    public static string? Find(VideoProject project, ProjectChapter chapter, ProjectScene scene)
    {
        if (!string.IsNullOrWhiteSpace(scene.MediaPath))
        {
            var configured = Path.IsPathRooted(scene.MediaPath) ? scene.MediaPath : Path.Combine(project.ProjectDirectory, scene.MediaPath);
            if (File.Exists(configured)) return configured;
        }

        if (string.IsNullOrWhiteSpace(project.ProjectDirectory)) return null;
        var mediaDir = Path.Combine(project.ProjectDirectory, "media");
        if (!Directory.Exists(mediaDir)) return null;

        var stems = new[] { $"c{chapter.Number:00}-s{scene.Number:000}", $"{scene.Number:000}" };
        foreach (var stem in stems)
        foreach (var ext in Extensions)
        {
            var candidate = Path.Combine(mediaDir, stem + ext);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string GeneratedImagePath(VideoProject project, ProjectChapter chapter, ProjectScene scene)
    {
        var mediaDir = Path.Combine(project.ProjectDirectory, "media");
        Directory.CreateDirectory(mediaDir);
        return Path.Combine(mediaDir, $"c{chapter.Number:00}-s{scene.Number:000}.png");
    }
}
