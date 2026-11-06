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

        // Portable builds move between folders. If the user is using ZeroVideo's normal
        // automation.default.json, always bind it to THIS build's bundled profile instead
        // of keeping an absolute path to an older extracted build. Custom profiles are kept.
        if (string.IsNullOrWhiteSpace(settings.AutomationProfilePath) ||
            string.Equals(Path.GetFileName(settings.AutomationProfilePath), "automation.default.json", StringComparison.OrdinalIgnoreCase))
        {
            settings.AutomationProfilePath = AppPaths.BundledAutomationProfile;
        }

        // Legacy animation selection is no longer supported. Do not delete
        // old model files here: cleanup is an explicit confirmed user action.
        if (settings.AiImageAnimationEngine is "Wan2.2 5B (ComfyUI)" or
            "FramePack (Local)" || string.IsNullOrWhiteSpace(settings.AiImageAnimationEngine))
            settings.AiImageAnimationEngine = "Camera Motion (FFmpeg)";
        if (settings.AiImageAnimationEngine is not ("Off" or
            "Camera Motion (FFmpeg)" or "LivePortrait (Local)"))
            settings.AiImageAnimationEngine = "Camera Motion (FFmpeg)";

        // Migrate missing/unrecognized values without changing legacy
        // projects' 8GB-friendly 1024x576 ComfyUI default.
        if (!LocalImageResolutionService.IsValidPreset(
                settings.LocalImageResolutionPreset))
            settings.LocalImageResolutionPreset = LocalImageResolutionService.Fast;
        if (!LocalImageResolutionService.IsValidMode(
                settings.LocalImageResolutionMode))
            settings.LocalImageResolutionMode = LocalImageResolutionService.Direct;

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
