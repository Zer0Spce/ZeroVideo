using Microsoft.Playwright;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class ClipchampTtsService(BrowserSession browser, AutomationProfileService profiles)
{
    private bool _manualVoiceSessionActive;

    private static readonly string[] KnownVoiceNames =
    [
        "Ava", "Andrew", "Brian", "Emma", "Aria", "Christopher", "Eric", "Guy", "Jenny", "Michelle", "Roger", "Steffan"
    ];

    public Task OpenAsync(ZeroVideoSettings settings)
        => browser.OpenManualAsync(settings, settings.ClipchampUrl);

    public async Task PrepareManualVoiceSelectionAsync(ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)
    {
        _manualVoiceSessionActive = false;
        await browser.EnsureStartedAsync(settings);
        var profile = await profiles.LoadAsync(settings.AutomationProfilePath);
        var page = await browser.GetOrCreatePageAsync("clipchamp");
        await page.GotoAsync(settings.ClipchampUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });

        var entry = await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.NewVideoButtons, 6000, ct)
            ?? await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.TextToSpeechButtons, 6000, ct);
        if (entry is not null)
        {
            try
            {
                await entry.ClickAsync(new() { Timeout = 10000 });
                await Task.Delay(1200, ct);
            }
            catch { }
        }

        log?.Invoke("Clipchamp manual checkpoint: open Text to speech if needed, choose your voice, then click Continue TTS in ZeroVideo.");
    }

    public void ConfirmManualVoiceSelection()
        => _manualVoiceSessionActive = true;

    public async Task<string> GenerateChapterAsync(ProjectChapter chapter, VideoProject project, ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)
    {
        await browser.EnsureStartedAsync(settings);
        var profile = await profiles.LoadAsync(settings.AutomationProfilePath);
        var page = await browser.GetOrCreatePageAsync("clipchamp");
        if (_manualVoiceSessionActive)
        {
            log?.Invoke($"Clipchamp chapter {chapter.Number}: manual voice session active. ZeroVideo will not refresh Clipchamp or change the selected voice.");

            var existingInput = await TryFindScriptInputOnceAsync(page, profile.Clipchamp.ScriptInputs);
            if (existingInput is null)
            {
                // Stay in the current Clipchamp project/session. Never Goto() here: a reload resets
                // Clipchamp's manually selected TTS voice back to its default.
                var entry = await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.TextToSpeechButtons, 5000, ct)
                    ?? await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.NewVideoButtons, 5000, ct);
                if (entry is not null)
                {
                    try
                    {
                        await entry.ClickAsync(new() { Timeout = 10000 });
                        await Task.Delay(1000, ct);
                    }
                    catch { }
                }
            }
        }
        else
        {
            var existingInput = await TryFindScriptInputOnceAsync(page, profile.Clipchamp.ScriptInputs);
            if (existingInput is null)
            {
                await page.GotoAsync(settings.ClipchampUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
                var entry = await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.NewVideoButtons, 8000, ct)
                    ?? await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.TextToSpeechButtons, 8000, ct);
                if (entry is not null)
                {
                    await entry.ClickAsync(new() { Timeout = 10000 });
                    await Task.Delay(1200, ct);
                }
            }
            log?.Invoke($"Clipchamp chapter {chapter.Number}: using Clipchamp's current voice selection.");
        }

        log?.Invoke($"Clipchamp chapter {chapter.Number}: preparing text-to-speech.");

        try
        {
            var input = await WaitForScriptInputAsync(page, profile.Clipchamp.ScriptInputs, 30000, ct)
                ?? throw new InvalidOperationException("Clipchamp TTS controls are visible, but ZeroVideo could not find the narration editor in any Clipchamp frame.");

            await FillNarrationAndVerifyAsync(input, chapter.Narration, log, ct);
            await ClickFirstAcrossFramesAsync(page, profile.Clipchamp.SaveButtons, "Clipchamp TTS Save", 15000, ct);
            log?.Invoke("TTS created with the current manually selected Clipchamp voice. Preparing audio-only export...");

            await Task.Delay(2000, ct);
            await ClickFirstAcrossFramesAsync(page, profile.Clipchamp.ExportButtons, "Clipchamp Export", 15000, ct);

            var audioDir = Path.Combine(project.ProjectDirectory, "audio");
            Directory.CreateDirectory(audioDir);
            var destination = Path.Combine(audioDir, $"chapter-{chapter.Number:00}.m4a");
            var exportStartedAt = DateTime.Now.AddSeconds(-2);

            await ClickFirstAcrossFramesAsync(page, profile.Clipchamp.AudioOnlyButtons, "audio-only export", 20000, ct);
            log?.Invoke("Clipchamp audio-only selected. Confirming export...");

            var finalExport = await FindFinalExportButtonAsync(page, profile.Clipchamp.FinalExportButtons, 20000, ct)
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

                var downloadButton = await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.DownloadButtons, 120000, ct);
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

    private static async Task FillNarrationAndVerifyAsync(ILocator input, string narration, Action<string>? log, CancellationToken ct)
    {
        var expected = narration.Trim();
        if (expected.Length == 0)
            throw new InvalidOperationException("The chapter narration is empty; TTS cannot be created.");

        await input.ClickAsync(new() { Timeout = 10000 });
        await input.FillAsync(expected, new() { Timeout = 20000 });
        await Task.Delay(450, ct);

        var actual = await ReadEditorTextAsync(input);
        var required = Math.Min(120, Math.Max(20, expected.Length / 3));
        if (actual.Trim().Length < required)
        {
            // React/contenteditable can occasionally ignore a first programmatic fill while hydrating.
            // Retry once, then fail rather than exporting a five-second silent project.
            await input.ClickAsync(new() { Timeout = 10000 });
            await input.FillAsync(expected, new() { Timeout = 20000 });
            await Task.Delay(650, ct);
            actual = await ReadEditorTextAsync(input);
        }

        if (actual.Trim().Length < required)
            throw new InvalidOperationException($"Clipchamp narration did not stick in the Text to speech editor (expected {expected.Length} chars, detected {actual.Trim().Length}). ZeroVideo will not export silent audio.");

        log?.Invoke($"Clipchamp narration verified in the TTS editor ({actual.Trim().Length} characters detected).");
    }

    private static async Task<string> ReadEditorTextAsync(ILocator input)
    {
        try
        {
            var value = await input.InputValueAsync(new() { Timeout = 3000 });
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        catch { }

        try
        {
            var text = await input.InnerTextAsync(new() { Timeout = 3000 });
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        catch { }

        try { return await input.TextContentAsync(new() { Timeout = 3000 }) ?? ""; }
        catch { return ""; }
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
            if (voiceValue is not null)
            {
                var currentVoice = await ReadLocatorLabelAsync(voiceValue);
                log?.Invoke($"Clipchamp TTS controls ready. Current voice: {currentVoice}. Frames scanned: {page.Frames.Count}.");
                return;
            }

            if ((DateTime.UtcNow - lastLog).TotalSeconds >= 8)
            {
                log?.Invoke($"Clipchamp TTS controls not found yet; scanning {page.Frames.Count} frame(s)...");
                lastLog = DateTime.UtcNow;
            }

            await Task.Delay(350, ct);
        }

        throw new InvalidOperationException($"Clipchamp voice selector was visible in the browser but could not be located across {page.Frames.Count} frame(s) within 60 seconds.");
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
            throw new InvalidOperationException($"Could not locate Clipchamp's current voice selector across any editor frame while trying to select '{targetDisplay}'.");

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
        foreach (var frame in page.Frames.Reverse())
        {
            try
            {
                var controls = frame.Locator("button, [role='combobox'], [role='button'], [aria-haspopup='listbox']");
                var count = Math.Min(await controls.CountAsync(), 500);
                for (var i = count - 1; i >= 0; i--)
                {
                    var item = controls.Nth(i);
                    if (!await item.IsVisibleAsync() || !await item.IsEnabledAsync()) continue;
                    var label = await ReadLocatorLabelAsync(item);
                    if (KnownVoiceNames.Any(v => VoiceLabelMatches(label, v))) return item;
                }
            }
            catch { }

            foreach (var voice in KnownVoiceNames)
            {
                foreach (var label in VoiceDisplayCandidates(voice))
                {
                    var match = await FindVisibleExactTextAsync(frame, label);
                    if (match is not null) return await PromoteToClickableAsync(match);
                }
            }

            try
            {
                var headings = frame.GetByText("Voice", new() { Exact = true });
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
        }

        return null;
    }

    private static async Task<ILocator?> FindExactClipchampVoiceOptionAsync(IPage page, string normalizedRequested, string targetDisplay, CancellationToken ct)
    {
        var exact = await FindVisibleExactTextAcrossFramesAsync(page, targetDisplay);
        if (exact is not null) return await PromoteToVoiceOptionAsync(exact);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            foreach (var frame in page.Frames.Reverse())
            {
                try
                {
                    var matches = frame.GetByText(normalizedRequested, new() { Exact = false });
                    var count = Math.Min(await matches.CountAsync(), 160);
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

                try
                {
                    var scrollers = frame.Locator("[role='listbox'], [role='menu'], [data-radix-scroll-area-viewport]");
                    var count = await scrollers.CountAsync();
                    for (var i = count - 1; i >= 0; i--)
                    {
                        var scroller = scrollers.Nth(i);
                        if (!await scroller.IsVisibleAsync()) continue;
                        await scroller.EvaluateAsync("el => { el.scrollTop += Math.max(240, el.clientHeight * 0.75); }");
                    }
                }
                catch { }
            }

            await Task.Delay(180, ct);
            exact = await FindVisibleExactTextAcrossFramesAsync(page, targetDisplay);
            if (exact is not null) return await PromoteToVoiceOptionAsync(exact);
        }

        return null;
    }

    private static async Task<ILocator?> WaitForDisplayedVoiceAsync(IPage page, string requestedVoice, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
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

    private static async Task<ILocator?> WaitForScriptInputAsync(IPage page, IEnumerable<string> selectors, int timeoutMs, CancellationToken ct)
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

        foreach (var frame in page.Frames.Reverse())
        {
            foreach (var selector in selectors.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var found = await FindVisibleEnabledAsync(frame, selector);
                if (found is not null) return found;
            }

            foreach (var selector in directSelectors)
            {
                var found = await FindVisibleEnabledAsync(frame, selector);
                if (found is not null) return found;
            }

            try
            {
                var byPlaceholder = frame.GetByPlaceholder("Type what you'd like to say", new() { Exact = false });
                var count = await byPlaceholder.CountAsync();
                for (var i = count - 1; i >= 0; i--)
                {
                    var item = byPlaceholder.Nth(i);
                    if (await item.IsVisibleAsync() && await item.IsEnabledAsync()) return item;
                }
            }
            catch { }
        }

        return null;
    }

    private static async Task<ILocator?> FindVisibleEnabledAsync(IFrame frame, string selector)
    {
        try
        {
            var items = frame.Locator(selector);
            var count = await items.CountAsync();
            for (var i = count - 1; i >= 0; i--)
            {
                var item = items.Nth(i);
                if (await item.IsVisibleAsync() && await item.IsEnabledAsync()) return item;
            }
        }
        catch { }
        return null;
    }

    private static async Task<ILocator?> FirstVisibleAcrossFramesAsync(IPage page, IEnumerable<string> selectors, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var frame in page.Frames.Reverse())
            {
                foreach (var selector in selectors.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    var item = await FindVisibleEnabledAsync(frame, selector);
                    if (item is not null) return item;
                }
            }
            await Task.Delay(150, ct);
        }
        return null;
    }

    private static async Task ClickFirstAcrossFramesAsync(IPage page, IEnumerable<string> selectors, string description, int timeoutMs, CancellationToken ct)
    {
        var item = await FirstVisibleAcrossFramesAsync(page, selectors, timeoutMs, ct)
            ?? throw new InvalidOperationException($"Could not find Clipchamp control: {description}.");
        await item.ClickAsync(new() { Timeout = timeoutMs });
    }

    private static async Task TryChooseAcrossFramesAsync(IPage page, IEnumerable<string> controls, string value, string label, Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value) || !controls.Any()) return;
        var control = await FirstVisibleAcrossFramesAsync(page, controls, 5000, ct);
        if (control is null)
        {
            log?.Invoke($"Clipchamp {label} selector not found; keeping current selection.");
            return;
        }

        try
        {
            await control.ClickAsync(new() { Timeout = 5000 });
            var option = await FindVisibleExactTextAcrossFramesAsync(page, value);
            if (option is null) throw new InvalidOperationException();
            await (await PromoteToVoiceOptionAsync(option)).ClickAsync(new() { Timeout = 5000 });
            log?.Invoke($"Clipchamp {label}: {value}");
        }
        catch
        {
            log?.Invoke($"Clipchamp could not select {label} '{value}'; keeping current selection.");
        }
    }

    private static async Task<ILocator?> FindVisibleExactTextAcrossFramesAsync(IPage page, string text)
    {
        foreach (var frame in page.Frames.Reverse())
        {
            var item = await FindVisibleExactTextAsync(frame, text);
            if (item is not null) return item;
        }
        return null;
    }

    private static async Task<ILocator?> FindVisibleExactTextAsync(IFrame frame, string text)
    {
        try
        {
            var matches = frame.GetByText(text, new() { Exact = true });
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

    private static async Task<ILocator> PromoteToClickableAsync(ILocator item)
    {
        try
        {
            var clickable = item.Locator("xpath=ancestor-or-self::*[self::button or @role='combobox' or @role='button' or @aria-haspopup='listbox' or @tabindex][1]");
            if (await clickable.CountAsync() > 0)
            {
                var first = clickable.First;
                if (await first.IsVisibleAsync() && await first.IsEnabledAsync()) return first;
            }
        }
        catch { }
        return item;
    }

    private static async Task<ILocator> PromoteToVoiceOptionAsync(ILocator item)
    {
        try
        {
            var option = item.Locator("xpath=ancestor-or-self::*[@role='option' or @role='menuitem' or @role='listitem' or self::button or @tabindex][1]");
            if (await option.CountAsync() > 0)
            {
                var first = option.First;
                if (await first.IsVisibleAsync() && await first.IsEnabledAsync()) return first;
            }
        }
        catch { }
        return item;
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
        => baseVoice.EndsWith("Multilingual", StringComparison.OrdinalIgnoreCase) ? baseVoice : $"{baseVoice} Multilingual";

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

    private static async Task<ILocator?> FindFinalExportButtonAsync(IPage page, IEnumerable<string> selectors, int timeoutMs, CancellationToken ct)
    {
        var configured = await FirstVisibleAcrossFramesAsync(page, selectors, timeoutMs, ct);
        if (configured is not null) return configured;

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var frame in page.Frames.Reverse())
            {
                try
                {
                    var buttons = frame.GetByRole(AriaRole.Button, new() { Name = "Export", Exact = true });
                    var count = await buttons.CountAsync();
                    for (var i = count - 1; i >= 0; i--)
                    {
                        var button = buttons.Nth(i);
                        if (await button.IsVisibleAsync() && await button.IsEnabledAsync()) return button;
                    }
                }
                catch { }
            }
            await Task.Delay(150, ct);
        }
        return null;
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
