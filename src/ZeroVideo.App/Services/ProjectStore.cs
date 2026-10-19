using System.Text.Json;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public async Task<string> SaveAsync(VideoProject project, string root)
    {
        var slug = Slug(project.Title.Length > 2 ? project.Title : project.Topic);
        var dir = string.IsNullOrWhiteSpace(project.ProjectDirectory) ? Path.Combine(root, $"{project.Id}-{slug}") : project.ProjectDirectory;
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "audio"));
        Directory.CreateDirectory(Path.Combine(dir, "media"));
        Directory.CreateDirectory(Path.Combine(dir, "render"));
        Directory.CreateDirectory(Path.Combine(dir, "exports"));
        Directory.CreateDirectory(Path.Combine(dir, "diagnostics"));
        project.ProjectDirectory = dir;
        project.UpdatedUtc = DateTime.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(dir, "project.json"), JsonSerializer.Serialize(project, JsonOptions));
        await WriteShotListAsync(project, dir);
        return dir;
    }

    public async Task<VideoProject> LoadAsync(string projectFile)
    {
        var json = await File.ReadAllTextAsync(projectFile);
        var project = JsonSerializer.Deserialize<VideoProject>(json, JsonOptions) ?? throw new InvalidDataException("Invalid project JSON.");
        project.ProjectDirectory = Path.GetDirectoryName(projectFile)!;
        return project;
    }

    public IEnumerable<string> FindProjects(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "project.json", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc)
        : [];

    private static async Task WriteShotListAsync(VideoProject project, string dir)
    {
        var rows = new List<string> { "chapter,scene,role,target_seconds,visual_type,visual_query,image_prompt,media_path" };
        foreach (var c in project.Chapters)
        foreach (var s in c.Scenes)
            rows.Add(string.Join(',', Csv(c.Number), Csv(s.Number), Csv(s.Role), Csv(s.TargetSeconds), Csv(s.VisualType), Csv(s.VisualQuery), Csv(s.ImagePrompt), Csv(s.MediaPath ?? "")));
        await File.WriteAllLinesAsync(Path.Combine(dir, "shot-list.csv"), rows);
    }

    private static string Csv(object? value)
    {
        var s = value?.ToString()?.Replace("\"", "\"\"") ?? "";
        return $"\"{s}\"";
    }

    private static string Slug(string value)
    {
        var normalized = new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        var slug = string.Join('-', normalized.Split('-', StringSplitOptions.RemoveEmptyEntries)).Trim('-');
        if (string.IsNullOrWhiteSpace(slug)) slug = "project";
        return slug[..Math.Min(50, slug.Length)];
    }
}
