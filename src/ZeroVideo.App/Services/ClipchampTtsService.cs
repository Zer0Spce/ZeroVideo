using Microsoft.Playwright;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class ClipchampTtsService(BrowserSession browser, AutomationProfileService profiles)
{
    private static readonly string[] KnownVoiceNames =
    [
        "Ava", "Andrew", "Brian", "Emma", "Aria", "Christopher", "Eric", "Guy", "Jenny", "Michelle", "Roger", "Steffan"
    ];

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

            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.RecordCreateButtons, "Record & create", 10000);
            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.TextToSpeechButtons, "Text to speech", 10000);

            log?.Invoke("Clipchamp: waiting for the text-to-speech panel to finish loading...");
            await WaitForTtsPanelReadyAsync(page, profile.Clipchamp.ScriptInputs, log, ct);

            await TryChooseAsync(page, profile.Clipchamp.LanguageControls, settings.ClipchampLanguage, "language", log);

            var selectedVoice = !string.IsNullOrWhiteSpace(project.Voice)
                ? project.Voice.Trim()
                : settings.ClipchampVoice?.Trim() ?? "";

            await ChooseVoiceAsync(page, selectedVoice, log, ct);

            var input = await WaitForScriptInputAsync(page, profile.Clipchamp.ScriptInputs, 30000, ct)
                ?? throw new InvalidOperationException("Could not find Clipchamp TTS script input after the panel finished loading.");

            await input.FillAsync(chapter.Narration);
            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.SaveButtons, "Clipchamp TTS Save", 10000);
            log?.Invoke($"TTS created with voice '{(string.IsNullOrWhiteSpace(selectedVoice) ? "Clipchamp default" : selectedVoice)}'. Preparing audio-only export...");

            await Task.Delay(2000, ct);
            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.ExportButtons, "Clipchamp Export", 10000);

            var audioDir = Path.Combine(project.ProjectDirectory, "audio");
            Directory.CreateDirectory(audioDir);
            var destination = Path.Combine(audioDir, $"chapter-{chapter.Number:00}.m4a");
            var exportStartedAt = DateTime.Now.AddSeconds(-2);

            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.AudioOnlyButtons, "audio-only export", 15000);
            log?.Invoke("Clipchamp audio-only selected. Confirming export...");

            var finalExport = await FindFinalExportButtonAsync(page, profile.Clipchamp.FinalExportButtons, 15000)
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

    private static async Task WaitForTtsPanelReadyAsync(
        IPage page,
        IEnumerable<string> scriptInputSelectors,
        Action<string>? log,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var lastLog = DateTime.MinValue;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (page.IsClosed)
                throw new InvalidOperationException("Clipchamp page closed while waiting for the text-to-speech panel.");

            var voiceValue = await FindDisplayedVoiceValueAsync(page);
            var scriptInput = await TryFindScriptInputOnceAsync(page, scriptInputSelectors);
            var hasVoiceHeading = await HasVisibleExactTextAsync(page, "Voice");
            var hasTextHeading = await HasVisibleExactTextAsync(page, "Text");

            if (voiceValue is not null && scriptInput is not null && hasVoiceHeading && hasTextHeading)
            {
                var currentVoice = await ReadLocatorLabelAsync(voiceValue);
                log?.Invoke($"Clipchamp TTS panel ready. Current voice: {currentVoice}.");
                return;
            }

            if ((DateTime.UtcNow - lastLog).TotalSeconds >= 8)
            {
                log?.Invoke("Clipchamp TTS panel is still loading; waiting...");
                lastLog = DateTime.UtcNow;
            }

            await Task.Delay(500, ct);
        }

        throw new InvalidOperationException("Clipchamp text-to-speech panel did not finish loading within 45 seconds.");
    }

    private static async Task<ILocator?> WaitForScriptInputAsync(
        IPage page,
        IEnumerable<string> selectors,
        int timeoutMs,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var input = await TryFindScriptInputOnceAsync(page, selectors);
            if (input is not null) return input;
            await Task.Delay(250, ct);
        }

        return null;
    }

    private static async Task<ILocator?> TryFindScriptInputOnceAsync(IPage page, IEnumerable<string> selectors)
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
                    if (await item.IsVisibleAsync() && await item.IsEnabledAsync()) return item;
                }
            }
            catch { }
        }

        try
        {
            var textareas = page.Locator("textarea");
            var count = await textareas.CountAsync();
            for (var i = count - 1; i >= 0; i--)
            {
                var item = textareas.Nth(i);
                if (await item.IsVisibleAsync() && await item.IsEnabledAsync()) return item;
            }
        }
        catch { }

        return null;
    }

    private static async Task<bool> HasVisibleExactTextAsync(IPage page, string text)
    {
        try
        {
            var matches = page.GetByText(text, new() { Exact = true });
            var count = await matches.CountAsync();
            for (var i = count - 1; i >= 0; i--)
                if (await matches.Nth(i).IsVisibleAsync()) return true;
        }
        catch { }

        return false;
    }

    private static async Task ChooseVoiceAsync(IPage page, string requestedVoice, Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestedVoice))
        {
            log?.Invoke("Clipchamp voice: keeping the current/default voice.");
            return;
        }

        var normalizedRequested = NormalizeRequestedVoice(requestedVoice);
        var targetDisplay = ToClipchampVoiceLabel(normalizedRequested);

        // Even after the panel readiness check, Clipchamp can replace DOM nodes while
        // finishing React hydration. Re-acquire the current voice selector for up to 20s.
        var currentControl = await WaitForCurrentVoiceValueAsync(page, 20000, ct);
        if (currentControl is null)
            throw new InvalidOperationException($"Could not find Clipchamp's current voice value after waiting for the TTS panel while trying to select '{targetDisplay}'.");

        var currentLabel = await ReadLocatorLabelAsync(currentControl);
        if (VoiceLabelMatches(currentLabel, normalizedRequested))
        {
            log?.Invoke($"Clipchamp voice already selected: {currentLabel}");
            return;
        }

        await currentControl.ClickAsync(new() { Timeout = 10000 });
        await Task.Delay(500, ct);

        var option = await FindExactClipchampVoiceOptionAsync(page, normalizedRequested, targetDisplay, ct);
        if (option is null)
            throw new InvalidOperationException($"Clipchamp voice '{targetDisplay}' was not found after opening the Voice dropdown.");

        var matchedLabel = await ReadLocatorLabelAsync(option);
        await option.ClickAsync(new() { Timeout = 10000 });
        await Task.Delay(650, ct);

        var verified = await WaitForDisplayedVoiceAsync(page, normalizedRequested, 10000, ct);
        if (verified is null)
            throw new InvalidOperationException($"Clipchamp did not switch from '{currentLabel}' to '{targetDisplay}'. TTS was not created.");

        var verifiedLabel = await ReadLocatorLabelAsync(verified);
        log?.Invoke($"Clipchamp voice verified: {verifiedLabel} (requested {requestedVoice})");
    }

    private static async Task<ILocator?> WaitForCurrentVoiceValueAsync(IPage page, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var current = await FindDisplayedVoiceValueAsync(page);
            if (current is not null) return current;
            await Task.Delay(300, ct);
        }

        return null;
    }

    private static async Task<ILocator?> FindDisplayedVoiceValueAsync(IPage page)
    {
        foreach (var voice in KnownVoiceNames)
        {
            foreach (var label in VoiceDisplayCandidates(voice))
            {
                var match = await FindVisibleExactTextAsync(page, label);
                if (match is not null) return match;
            }
        }

        try
        {
            var headings = page.GetByText("Voice", new() { Exact = true });
            var count = await headings.CountAsync();
            for (var i = count - 1; i >= 0; i--)
            {
                var heading = headings.Nth(i);
                if (!await heading.IsVisibleAsync()) continue;

                var candidate = heading.Locator("xpath=following::*[contains(normalize-space(.), 'Multilingual')][1]");
                if (await candidate.CountAsync() > 0 && await candidate.First.IsVisibleAsync())
                    return candidate.First;
            }
        }
        catch { }

        return null;
    }

    private static async Task<ILocator?> FindExactClipchampVoiceOptionAsync(IPage page, string normalizedRequested, string targetDisplay, CancellationToken ct)
    {
        var exact = await FindVisibleExactTextAsync(page, targetDisplay);
        if (exact is not null) return exact;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                var matches = page.GetByText(normalizedRequested, new() { Exact = false });
                var count = Math.Min(await matches.CountAsync(), 120);
                for (var i = count - 1; i >= 0; i--)
                {
                    var item = matches.Nth(i);
                    if (!await item.IsVisibleAsync()) continue;
                    var label = await ReadLocatorLabelAsync(item);
                    if (VoiceLabelMatches(label, normalizedRequested)) return item;
                }
            }
            catch { }

            try { await page.Mouse.WheelAsync(0, 360); } catch { }
            await Task.Delay(180, ct);

            exact = await FindVisibleExactTextAsync(page, targetDisplay);
            if (exact is not null) return exact;
        }

        return null;
    }

    private static async Task<ILocator?> WaitForDisplayedVoiceAsync(IPage page, string requestedVoice, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var current = await FindDisplayedVoiceValueAsync(page);
            if (current is not null)
            {
                var label = await ReadLocatorLabelAsync(current);
                if (VoiceLabelMatches(label, requestedVoice)) return current;
            }

            await Task.Delay(150, ct);
        }

        return null;
    }

    private static async Task<ILocator?> FindVisibleExactTextAsync(IPage page, string text)
    {
        try
        {
            var matches = page.GetByText(text, new() { Exact = true });
            var count = await matches.CountAsync();
            for (var i = count - 1; i >= 0; i--)
            {
                var item = matches.Nth(i);
                if (await item.IsVisibleAsync()) return item;
            }
        }
        catch { }

        return null;
    }

    private static string NormalizeRequestedVoice(string value)
    {
        var normalized = CollapseWhitespace(value).Trim();
        const string multilingual = " Multilingual";
        if (normalized.EndsWith(multilingual, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^multilingual.Length].Trim();
        return normalized;
    }

    private static string ToClipchampVoiceLabel(string baseVoice)
        => baseVoice.EndsWith("Multilingual", StringComparison.OrdinalIgnoreCase)
            ? baseVoice
            : $"{baseVoice} Multilingual";

    private static IEnumerable<string> VoiceDisplayCandidates(string baseVoice)
    {
        yield return $"{baseVoice} Multilingual";
        yield return baseVoice;
    }

    private static bool VoiceLabelMatches(string candidate, string requested)
    {
        candidate = CollapseWhitespace(candidate).Trim();
        requested = NormalizeRequestedVoice(requested);
        if (candidate.Length == 0 || requested.Length == 0) return false;
        if (candidate.Equals(requested, StringComparison.OrdinalIgnoreCase)) return true;
        if (candidate.Equals($"{requested} Multilingual", StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.StartsWith(requested + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadLocatorLabelAsync(ILocator item)
    {
        string text = "", aria = "", title = "";
        try { text = (await item.InnerTextAsync()).Trim(); } catch { }
        try { aria = (await item.GetAttributeAsync("aria-label") ?? "").Trim(); } catch { }
        try { title = (await item.GetAttributeAsync("title") ?? "").Trim(); } catch { }

        if (!string.IsNullOrWhiteSpace(text)) return CollapseWhitespace(text);
        if (!string.IsNullOrWhiteSpace(aria)) return CollapseWhitespace(aria);
        return CollapseWhitespace(title);
    }

    private static string CollapseWhitespace(string value)
        => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
        if (string.IsNullOrWhiteSpace(value) || !controls.Any()) return;

        var control = await BrowserLocator.FirstVisibleAsync(page, controls, 5000);
        if (control is null)
        {
            log?.Invoke($"Clipchamp {label} selector not found; keeping current selection.");
            return;
        }

        try
        {
            await control.ClickAsync();
            var option = page.GetByText(value, new() { Exact = true }).Last;
            await option.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
            await option.ClickAsync();
            log?.Invoke($"Clipchamp {label}: {value}");
        }
        catch
        {
            log?.Invoke($"Clipchamp could not select {label} '{value}'; keeping current selection.");
        }
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
