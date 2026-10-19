using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed record PreflightCheck(string Name, bool Passed, string Detail);

public sealed class PreflightService(ProcessRunner runner)
{
    public async Task<IReadOnlyList<PreflightCheck>> RunAsync(ZeroVideoSettings settings, CancellationToken ct = default)
    {
        var checks = new List<PreflightCheck>();
        var edge = FindEdge();
        checks.Add(new("Microsoft Edge", edge is not null, edge ?? "Microsoft Edge was not found."));
        checks.Add(new("Automation profile", File.Exists(settings.AutomationProfilePath), settings.AutomationProfilePath));

        try
        {
            Directory.CreateDirectory(settings.ProjectsRoot);
            var probe = Path.Combine(settings.ProjectsRoot, ".zerovideo-write-test");
            await File.WriteAllTextAsync(probe, "ok", ct);
            File.Delete(probe);
            checks.Add(new("Projects folder", true, settings.ProjectsRoot));
        }
        catch (Exception ex) { checks.Add(new("Projects folder", false, ex.Message)); }

        try
        {
            var result = await runner.RunAsync(settings.FfmpegPath, "-version", ct: ct);
            checks.Add(new("FFmpeg", result.ExitCode == 0, result.ExitCode == 0 ? settings.FfmpegPath : result.StdErr.Trim()));
        }
        catch (Exception ex) { checks.Add(new("FFmpeg", false, ex.Message)); }

        return checks;
    }

    private static string? FindEdge()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
