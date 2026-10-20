using System.Text.Json;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<ZeroVideoSettings> LoadAsync()
    {
        AppPaths.Ensure();
        ZeroVideoSettings settings;
        if (File.Exists(AppPaths.SettingsFile))
        {
            await using var stream = File.OpenRead(AppPaths.SettingsFile);
            settings = await JsonSerializer.DeserializeAsync<ZeroVideoSettings>(stream, JsonOptions) ?? new();
        }
        else settings = new();

        settings.ProjectsRoot = string.IsNullOrWhiteSpace(settings.ProjectsRoot) ? AppPaths.DefaultProjectsRoot : settings.ProjectsRoot;
        settings.EdgeUserDataDirectory = string.IsNullOrWhiteSpace(settings.EdgeUserDataDirectory) ? AppPaths.BrowserProfileDirectory : settings.EdgeUserDataDirectory;
        settings.AutomationProfilePath = string.IsNullOrWhiteSpace(settings.AutomationProfilePath) ? AppPaths.BundledAutomationProfile : settings.AutomationProfilePath;
        settings.FfmpegPath = ResolveTool(settings.FfmpegPath, "ffmpeg.exe");
        settings.FfprobePath = ResolveTool(settings.FfprobePath, "ffprobe.exe");
        return settings;
    }

    public async Task SaveAsync(ZeroVideoSettings settings)
    {
        AppPaths.Ensure();
        await File.WriteAllTextAsync(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public static string ResolveTool(string configured, string name)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);

        var managed = Path.Combine(AppPaths.ManagedFfmpegDirectory, "bin", name);
        if (File.Exists(managed)) return managed;

        var bundled = Path.Combine(AppPaths.BundledFfmpegDirectory, "bin", name);
        if (File.Exists(bundled)) return bundled;

        var onPath = FindOnPath(name);
        return onPath ?? "";
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(raw.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }
}
