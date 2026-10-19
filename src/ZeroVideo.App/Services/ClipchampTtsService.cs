using Microsoft.Playwright;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class ClipchampTtsService(BrowserSession browser, AutomationProfileService profiles)
{
    public async Task OpenAsync(ZeroVideoSettings settings)
    {
        await browser.EnsureStartedAsync(settings);
        var page = await browser.GetOrCreatePageAsync("clipchamp");
        await page.GotoAsync(settings.ClipchampUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
    }

    public async Task<string> GenerateChapterAsync(ProjectChapter chapter, VideoProject project, ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)
    {
        await browser.EnsureStartedAsync(settings);
        var profile = await profiles.LoadAsync(settings.AutomationProfilePath);
        var page = await browser.GetOrCreatePageAsync("clipchamp");
        await page.GotoAsync(settings.ClipchampUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
        log?.Invoke($"Clipchamp chapter {chapter.Number}: preparing text-to-speech.");

        try
        {
            var newVideo = await BrowserLocator.FirstVisibleAsync(page, profile.Clipchamp.NewVideoButtons, 4000);
            if (newVideo is not null) await newVideo.ClickAsync();
            await Task.Delay(2500, ct);
            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.RecordCreateButtons, "Record & create", 5000);
            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.TextToSpeechButtons, "Text to speech", 5000);

            await TryChooseAsync(page, profile.Clipchamp.LanguageControls, settings.ClipchampLanguage, "language", log);
            await TryChooseAsync(page, profile.Clipchamp.VoiceControls, settings.ClipchampVoice, "voice", log);

            var input = await BrowserLocator.FirstVisibleAsync(page, profile.Clipchamp.ScriptInputs, 5000) ?? throw new InvalidOperationException("Could not find Clipchamp TTS script input.");
            await input.FillAsync(chapter.Narration);
            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.SaveButtons, "Clipchamp TTS Save", 5000);
            log?.Invoke("TTS created. Preparing audio-only export...");
            await Task.Delay(2000, ct);
            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.ExportButtons, "Clipchamp Export", 5000);

            var audioDir = Path.Combine(project.ProjectDirectory, "audio");
            Directory.CreateDirectory(audioDir);
            var destination = Path.Combine(audioDir, $"chapter-{chapter.Number:00}.m4a");
            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.AudioOnlyButtons, "audio-only export", 7000);

            IDownload? download = null;
            try
            {
                download = await page.RunAndWaitForDownloadAsync(async () =>
                {
                    var downloadButton = await BrowserLocator.FirstVisibleAsync(page, profile.Clipchamp.DownloadButtons, 120000);
                    if (downloadButton is not null) await downloadButton.ClickAsync();
                }, new() { Timeout = 180000 });
            }
            catch
            {
                var newest = FindRecentDownload("*.m4a", TimeSpan.FromMinutes(5));
                if (newest is not null) { File.Copy(newest, destination, true); chapter.AudioPath = destination; return destination; }
                throw;
            }

            if (download is null) throw new InvalidOperationException("Clipchamp export finished but no audio download was detected.");
            await download.SaveAsAsync(destination);
            chapter.AudioPath = destination;
            return destination;
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(AppPaths.DiagnosticsDirectory);
            var shot = Path.Combine(AppPaths.DiagnosticsDirectory, $"clipchamp-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            try { await page.ScreenshotAsync(new() { Path = shot, FullPage = true }); } catch { }
            throw new InvalidOperationException($"Clipchamp automation stopped safely: {ex.Message} Diagnostic: {shot}", ex);
        }
    }

    private static async Task TryChooseAsync(IPage page, IEnumerable<string> controls, string value, string label, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(value) || controls.Count() == 0) return;
        var control = await BrowserLocator.FirstVisibleAsync(page, controls, 1800);
        if (control is null) { log?.Invoke($"Clipchamp {label} selector not found; keeping current selection."); return; }
        try
        {
            await control.ClickAsync();
            var option = page.GetByText(value, new() { Exact = true }).Last;
            await option.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 2500 });
            await option.ClickAsync();
            log?.Invoke($"Clipchamp {label}: {value}");
        }
        catch { log?.Invoke($"Clipchamp could not select {label} '{value}'; keeping current selection."); }
    }

    private static string? FindRecentDownload(string pattern, TimeSpan maxAge)
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(downloads)) return null;
        return Directory.EnumerateFiles(downloads, pattern).Select(p => new FileInfo(p)).Where(f => DateTime.Now - f.LastWriteTime < maxAge).OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
    }
}
