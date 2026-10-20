using Microsoft.Playwright;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class ClipchampTtsService(BrowserSession browser, AutomationProfileService profiles)
{
    public Task OpenAsync(ZeroVideoSettings settings)
        => browser.OpenManualAsync(settings, settings.ClipchampUrl);

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
            var selectedVoice = !string.IsNullOrWhiteSpace(project.Voice) ? project.Voice : settings.ClipchampVoice;
            await TryChooseAsync(page, profile.Clipchamp.VoiceControls, selectedVoice, "voice", log);

            var input = await BrowserLocator.FirstVisibleAsync(page, profile.Clipchamp.ScriptInputs, 5000) ?? throw new InvalidOperationException("Could not find Clipchamp TTS script input.");
            await input.FillAsync(chapter.Narration);
            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.SaveButtons, "Clipchamp TTS Save", 5000);
            log?.Invoke("TTS created. Preparing audio-only export...");
            await Task.Delay(2000, ct);
            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.ExportButtons, "Clipchamp Export", 7000);

            var audioDir = Path.Combine(project.ProjectDirectory, "audio");
            Directory.CreateDirectory(audioDir);
            var destination = Path.Combine(audioDir, $"chapter-{chapter.Number:00}.m4a");
            var exportStartedAt = DateTime.Now.AddSeconds(-2);

            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.AudioOnlyButtons, "audio-only export", 10000);
            log?.Invoke("Clipchamp audio-only selected. Confirming export...");

            var finalExport = await FindFinalExportButtonAsync(page, profile.Clipchamp.FinalExportButtons, 12000)
                ?? throw new InvalidOperationException("Could not find the final Clipchamp Audio-only Export button.");

            IDownload? download = null;
            var confirmationClicked = false;
            try
            {
                download = await page.RunAndWaitForDownloadAsync(async () =>
                {
                    await finalExport.ClickAsync(new() { Timeout = 10000 });
                    confirmationClicked = true;
                }, new() { Timeout = 180000 });
            }
            catch (Exception ex)
            {
                // Current Clipchamp versions may render/export first and show another
                // download control instead of emitting a browser download immediately.
                // If the confirmation click happened, continue into that second-stage flow.
                if (!confirmationClicked)
                    throw new InvalidOperationException($"Could not click Clipchamp's final Audio-only Export button: {ex.Message}", ex);

                log?.Invoke("Clipchamp export confirmed; waiting for the audio download to become available...");
            }

            if (download is null)
            {
                var recent = FindRecentDownload("*.m4a", exportStartedAt);
                if (recent is not null)
                {
                    File.Copy(recent, destination, true);
                    chapter.AudioPath = destination;
                    log?.Invoke($"Clipchamp chapter {chapter.Number}: audio download detected.");
                    return destination;
                }

                var downloadButton = await BrowserLocator.FirstVisibleAsync(page, profile.Clipchamp.DownloadButtons, 120000);
                if (downloadButton is not null)
                {
                    try
                    {
                        download = await page.RunAndWaitForDownloadAsync(
                            () => downloadButton.ClickAsync(new() { Timeout = 10000 }),
                            new() { Timeout = 180000 });
                    }
                    catch
                    {
                        recent = FindRecentDownload("*.m4a", exportStartedAt);
                        if (recent is not null)
                        {
                            File.Copy(recent, destination, true);
                            chapter.AudioPath = destination;
                            log?.Invoke($"Clipchamp chapter {chapter.Number}: audio recovered from Downloads.");
                            return destination;
                        }
                        throw;
                    }
                }
            }

            if (download is null)
            {
                var recent = FindRecentDownload("*.m4a", exportStartedAt);
                if (recent is not null)
                {
                    File.Copy(recent, destination, true);
                    chapter.AudioPath = destination;
                    log?.Invoke($"Clipchamp chapter {chapter.Number}: audio recovered from Downloads.");
                    return destination;
                }
                throw new InvalidOperationException("Clipchamp export finished but no audio download was detected.");
            }

            await download.SaveAsAsync(destination);
            chapter.AudioPath = destination;
            log?.Invoke($"Clipchamp chapter {chapter.Number}: audio saved.");
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

    private static async Task<ILocator?> FindFinalExportButtonAsync(IPage page, IEnumerable<string> selectors, int timeoutMs)
    {
        foreach (var selector in selectors.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            try
            {
                var items = page.Locator(selector);
                var count = await items.CountAsync();
                for (var i = count - 1; i >= 0; i--)
                {
                    var item = items.Nth(i);
                    if (!await item.IsVisibleAsync()) continue;
                    if (!await item.IsEnabledAsync()) continue;
                    return item;
                }
            }
            catch { }
        }

        try
        {
            var button = page.GetByRole(AriaRole.Button, new() { Name = "Export", Exact = true }).Last;
            await button.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeoutMs });
            if (await button.IsEnabledAsync()) return button;
        }
        catch { }

        return null;
    }

    private static async Task TryChooseAsync(IPage page, IEnumerable<string> controls, string value, string label, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(value) || !controls.Any())
        {
            if (label == "voice") log?.Invoke("Clipchamp voice: keeping current/default voice.");
            return;
        }

        var control = await BrowserLocator.FirstVisibleAsync(page, controls, 1800);
        if (control is null) { log?.Invoke($"Clipchamp {label} selector not found; keeping current selection."); return; }
        try
        {
            await control.ClickAsync();
            var option = page.GetByText(value, new() { Exact = true }).Last;
            await option.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3500 });
            await option.ClickAsync();
            log?.Invoke($"Clipchamp {label}: {value}");
        }
        catch { log?.Invoke($"Clipchamp could not select {label} '{value}'; keeping current selection."); }
    }

    private static string? FindRecentDownload(string pattern, DateTime notBefore)
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(downloads)) return null;
        return Directory.EnumerateFiles(downloads, pattern)
            .Select(p => new FileInfo(p))
            .Where(f => f.LastWriteTime >= notBefore && f.Length > 1000)
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault()?.FullName;
    }
}
