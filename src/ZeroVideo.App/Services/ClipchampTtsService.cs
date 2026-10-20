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

            log?.Invoke("Clipchamp: waiting for the text-to-speech controls to appear...");
            await WaitForTtsPanelReadyAsync(page, log, ct);

            await TryChooseAsync(page, profile.Clipchamp.LanguageControls, settings.ClipchampLanguage, "language", log);

            var selectedVoice = !string.IsNullOrWhiteSpace(project.Voice)
                ? project.Voice.Trim()
                : settings.ClipchampVoice?.Trim() ?? "";

            await ChooseVoiceAsync(page, selectedVoice, log, ct);

            var input = await WaitForScriptInputAsync(page, profile.Clipchamp.ScriptInputs, 30000, ct)
                ?? throw new InvalidOperationException("Clipchamp TTS panel is visible, but ZeroVideo could not find the narration text box.");

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

    private static async Task WaitForTtsPanelReadyAsync(IPage page, Action<string>? log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var lastLog = DateTime.MinValue;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (page.IsClosed)
                throw new InvalidOperationException("Clipchamp page closed while waiting for the text-to-speech panel.");

            var voiceValue = await FindDisplayedVoiceValueAsync(page);
            var hasVoiceHeading = await HasVisibleExactTextAsync(page, "Voice");
            var hasTtsHeading = await HasVisibleExactTextAsync(page, "Text to speech");

            // Do not require the narration editor here. Clipchamp currently renders the
            // voice controls before the editor is represented by a normal textarea/role.
            // The screenshot can already show a completely usable TTS panel while the old
            // script-input selector still reports nothing.
            if (voiceValue is not null && hasVoiceHeading && hasTtsHeading)
            {
                var currentVoice = await ReadLocatorLabelAsync(voiceValue);
                log?.Invoke($"Clipchamp TTS controls ready. Current voice: {currentVoice}.");
                return;
            }

            if ((DateTime.UtcNow - lastLog).TotalSeconds >= 8)
            {
                log?.Invoke("Clipchamp TTS controls are still loading; waiting...");
                lastLog = DateTime.UtcNow;
            }

            await Task.Delay(400, ct);
        }

        throw new InvalidOperationException("Clipchamp voice controls did not become available within 60 seconds.");
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
        // Prefer the automation profile first.
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

        // Current Clipchamp displays "Type what you'd like to say" inside a custom editor,
        // which may be a textarea, contenteditable element, or role=textbox depending on the
        // rollout. Search all of those directly.
        var directSelectors = new[]
        {
            "textarea[placeholder*='Type what' i]",
            "[contenteditable='true'][aria-label*='Type what' i]",
            "[contenteditable='true'][data-placeholder*='Type what' i]",
            "[role='textbox'][aria-label*='Type what' i]",
            "[role='textbox'][contenteditable='true']",
            "textarea",
            "[contenteditable='true']"
        };

        foreach (var selector in directSelectors)
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
            var byPlaceholder = page.GetByPlaceholder("Type what you'd like to say", new() { Exact = false });
            var count = await byPlaceholder.CountAsync();
            for (var i = count - 1; i >= 0; i--)
            {
                var item = byPlaceholder.Nth(i);
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

        var currentControl = await WaitForCurrentVoiceValueAsync(page, 20000, ct);
        if (currentControl is null)
            throw new InvalidOperationException($"Could not find Clipchamp's current voice selector while trying to select '{targetDisplay}'.");

        var currentLabel = await ReadLocatorLabelAsync(currentControl);
        if (VoiceLabelMatches(currentLabel, normalizedRequested))
        {
            log?.Invoke($"Clipchamp voice already selected: {currentLabel}");
            return;
        }

        log?.Invoke($"Clipchamp voice: switching {currentLabel} -> {targetDisplay}...");
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
            await Task.Delay(250, ct);
        }

        return null;
    }

    private static async Task<ILocator?> FindDisplayedVoiceValueAsync(IPage page)
    {
        // First scan actual interactive controls. This directly finds the current Clipchamp
        // selector such as a button/combobox whose text is "Ava Multilingual".
        try
        {
            var controls = page.Locator("button, [role='combobox'], [aria-haspopup='listbox']");
            var count = Math.Min(await controls.CountAsync(), 400);
            for (var i = count - 1; i >= 0; i--)
            {
                var item = controls.Nth(i);
                if (!await item.IsVisibleAsync() || !await item.IsEnabledAsync()) continue;
                var label = await ReadLocatorLabelAsync(item);
                if (KnownVoiceNames.Any(v => VoiceLabelMatches(label, v))) return item;
            }
        }
        catch { }

        // Fallback: find the visible voice text, then promote it to the nearest clickable
        // ancestor so clicks hit the selector container instead of a child text span.
        foreach (var voice in KnownVoiceNames)
        {
            foreach (var label in VoiceDisplayCandidates(voice))
            {
                var match = await FindVisibleExactTextAsync(page, label);
                if (match is not null) return await PromoteToClickableAsync(match);
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
                    return await PromoteToClickableAsync(candidate.First);
            }
        }
        catch { }

        return null;
    }

    private static async Task<ILocator> PromoteToClickableAsync(ILocator item)
    {
        try
        {
            var clickable = item.Locator("xpath=ancestor::*[self::button or @role='combobox' or @aria-haspopup='listbox' or @tabindex][1]");
            if (await clickable.CountAsync() > 0)
            {
                var first = clickable.First;
                if (await first.IsVisibleAsync() && await first.IsEnabledAsync()) return first;
            }
        }
        catch { }

        return item;
    }

    private static async Task<ILocator?> FindExactClipchampVoiceOptionAsync(IPage page, string normalizedRequested, string targetDisplay, CancellationToken ct)
    {
        var exact = await FindVisibleExactTextAsync(page, targetDisplay);
        if (exact is not null) return await PromoteToVoiceOptionAsync(exact);

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
                    if (VoiceLabelMatches(label, normalizedRequested))
                        return await PromoteToVoiceOptionAsync(item);
                }
            }
            catch { }

            try { await page.Mouse.WheelAsync(0, 360); } catch { }
            await Task.Delay(180, ct);

            exact = await FindVisibleExactTextAsync(page, targetDisplay);
            if (exact is not null) return await PromoteToVoiceOptionAsync(exact);
        }

        return null;
    }

    private static async Task<ILocator> PromoteToVoiceOptionAsync(ILocator item)
    {
        try
        {
            var option = item.Locator("xpath=ancestor::*[@role='option' or @role='menuitem' or @role='listitem' or self::button or @tabindex][1]");
            if (await option.CountAsync() > 0)
            {
                var first = option.First;
                if (await first.IsVisibleAsync() && await first.IsEnabledAsync()) return first;
            }
        }
        catch { }

        return item;
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
