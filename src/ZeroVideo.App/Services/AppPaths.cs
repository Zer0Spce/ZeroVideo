namespace ZeroVideo.App.Services;

public static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeroVideo");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string DefaultProjectsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ZeroVideo");
    public static string BrowserProfileDirectory => Path.Combine(DataDirectory, "browser-profile");
    public static string DiagnosticsDirectory => Path.Combine(DataDirectory, "diagnostics");
    public static string BundledAutomationProfile => Path.Combine(BaseDirectory, "profiles", "automation.default.json");
    public static string BundledFfmpegDirectory => Path.Combine(BaseDirectory, "tools", "ffmpeg");

    public static void Ensure()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(DefaultProjectsRoot);
        Directory.CreateDirectory(BrowserProfileDirectory);
        Directory.CreateDirectory(DiagnosticsDirectory);
    }
}
