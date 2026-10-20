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
            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.RecordCreateButtons, "Record & create", 5000);
            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.TextToSpeechButtons, "Text to speech", 5000);

            await TryChooseAsync(page, profile.Clipchamp.LanguageControls, settings.ClipchampLanguage, "language", log);

            var selectedVoice = !string.IsNullOrWhiteSpace(project.Voice)
                ? project.Voice.Trim()
                : settings.ClipchampVoice?.Trim() ?? "";
            await ChooseVoiceAsync(page, profile.Clipchamp.VoiceControls, selectedVoice, log, ct);

            var input = await BrowserLocator.FirstVisibleAsync(page, profile.Clipchamp.ScriptInputs, 5000)
                ?? throw new InvalidOperationException("Could not find Clipchamp TTS script input.");
            await input.FillAsync(chapter.Narration);
            await BrowserLocator.ClickFirstAsync(page, profile.Clipchamp.SaveButtons, "Clipchamp TTS Save", 5000);
            log?.Invoke($"TTS created with voice '{(string.IsNullOrWhiteSpace(selectedVoice) ? "Clipchamp default" : selectedVoice)}'. Preparing audio-only export...");
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

    private static async Task ChooseVoiceAsync(
        IPage page,
        IEnumerable<string> configuredControls,
        string value,
        Action<string>? log,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            log?.Invoke("Clipchamp voice: keeping the current/default voice.");
            return;
        }

        var control = await FindVoiceControlAsync(page, configuredControls, value);
        if (control is null)
            throw new InvalidOperationException($"Could not find Clipchamp's voice selector for requested voice '{value}'. ZeroVideo will not silently use Ava.");

        await control.ClickAsync(new() { Timeout = 8000 });
        await Task.Delay(450, ct);

        // Clipchamp virtualizes its voice list. A voice can be available without being
        // rendered in the DOM yet, so actively use the picker search field when present.
        var searched = await TrySearchVoicePickerAsync(page, value, ct);
        if (searched)
        {
            log?.Invoke($"Clipchamp voice picker: searched for '{value}'.");
            await Task.Delay(450, ct);
        }

        var option = await FindVoiceOptionAsync(page, value, 9000);
        if (option is null && !searched)
        {
            // No search field: walk/scroll a virtualized list until the requested voice is rendered.
            option = await ScrollVoiceListAndFindAsync(page, value, ct);
        }

        if (option is null)
        {
            var visible = await ReadVisibleVoiceLabelsAsync(page);
            var suffix = visible.Count == 0 ? "" : $" Visible voices: {string.Join(", ", visible.Take(12))}.";
            throw new InvalidOperationException($"Clipchamp voice '{value}' was not found in the voice picker.{suffix} ZeroVideo will not silently use Ava.");
        }

        var matchedLabel = await ReadLocatorLabelAsync(option);
        await option.ClickAsync(new() { Timeout = 8000 });
        await Task.Delay(650, ct);

        if (!await IsVoiceSelectedAsync(page, value, matchedLabel))
            throw new InvalidOperationException($"Clipchamp did not confirm voice '{value}' after selection. TTS was not created, preventing an accidental Ava fallback.");

        log?.Invoke(string.IsNullOrWhiteSpace(matchedLabel) || matchedLabel.Equals(value, StringComparison.OrdinalIgnoreCase)
            ? $"Clipchamp voice verified: {value}"
            : $"Clipchamp voice verified: {matchedLabel} (requested {value})");
    }

    private static async Task<ILocator?> FindVoiceControlAsync(IPage page, IEnumerable<string> configuredControls, string requestedVoice)
    {
        foreach (var selector in configuredControls.Where(x => !string.IsNullOrWhiteSpace(x)))
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

        // Current Clipchamp often labels this control with the CURRENT voice (for example
        // "Ava") instead of the literal word "Voice".
        var candidates = page.Locator("[role='combobox'], button[aria-haspopup='listbox'], button[aria-haspopup='menu'], button");
        var candidateCount = Math.Min(await candidates.CountAsync(), 350);
        for (var i = candidateCount - 1; i >= 0; i--)
        {
            try
            {
                var item = candidates.Nth(i);
                if (!await item.IsVisibleAsync() || !await item.IsEnabledAsync()) continue;

                var label = await ReadLocatorLabelAsync(item);
                if (label.Contains("voice", StringComparison.OrdinalIgnoreCase) ||
                    VoiceLabelMatches(label, requestedVoice) ||
                    KnownVoiceNames.Any(v => VoiceLabelMatches(label, v)))
                    return item;
            }
            catch { }
        }

        return null;
    }

    private static async Task<bool> TrySearchVoicePickerAsync(IPage page, string value, CancellationToken ct)
    {
        var selectors = new[]
        {
            "input[type='search']",
            "input[placeholder*='search' i]",
            "input[aria-label*='search' i]",
            "input[placeholder*='voice' i]",
            "input[aria-label*='voice' i]",
            "[role='dialog'] input",
            "[role='listbox'] input"
        };

        foreach (var selector in selectors)
        {
            try
            {
                var inputs = page.Locator(selector);
                var count = await inputs.CountAsync();
                for (var i = count - 1; i >= 0; i--)
                {
                    var input = inputs.Nth(i);
                    if (!await input.IsVisibleAsync() || !await input.IsEnabledAsync()) continue;
                    await input.FillAsync(value, new() { Timeout = 5000 });
                    await Task.Delay(250, ct);
                    return true;
                }
            }
            catch { }
        }

        return false;
    }

    private static async Task<ILocator?> FindVoiceOptionAsync(IPage page, string value, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var matched = await FindBestVisibleVoiceOptionAsync(page, value);
            if (matched is not null) return matched;
            await Task.Delay(200);
        }

        return null;
    }

    private static async Task<ILocator?> FindBestVisibleVoiceOptionAsync(IPage page, string value)
    {
        // Exact ARIA option first.
        try
        {
            var exactOption = page.GetByRole(AriaRole.Option, new() { Name = value, Exact = true }).Last;
            if (await exactOption.IsVisibleAsync()) return exactOption;
        }
        catch { }

        // Then scan option-like elements so labels such as "Andrew (Multilingual)" match
        // a project selection of simply "Andrew".
        var optionLike = page.Locator("[role='option'], [role='menuitem'], [role='menuitemradio'], [role='listitem'], li, button");
        var count = Math.Min(await optionLike.CountAsync(), 600);
        ILocator? prefixMatch = null;

        for (var i = count - 1; i >= 0; i--)
        {
            try
            {
                var item = optionLike.Nth(i);
                if (!await item.IsVisibleAsync() || !await item.IsEnabledAsync()) continue;
                var label = await ReadLocatorLabelAsync(item);
                if (label.Equals(value, StringComparison.OrdinalIgnoreCase)) return item;
                if (prefixMatch is null && VoiceLabelMatches(label, value)) prefixMatch = item;
            }
            catch { }
        }

        return prefixMatch;
    }

    private static async Task<ILocator?> ScrollVoiceListAndFindAsync(IPage page, string value, CancellationToken ct)
    {
        var scrollers = page.Locator("[role='listbox'], [role='menu'], [role='dialog']");
        var count = await scrollers.CountAsync();

        for (var s = count - 1; s >= 0; s--)
        {
            var scroller = scrollers.Nth(s);
            try
            {
                if (!await scroller.IsVisibleAsync()) continue;
                await scroller.EvaluateAsync("el => { el.scrollTop = 0; }");

                for (var step = 0; step < 30; step++)
                {
                    var match = await FindBestVisibleVoiceOptionAsync(page, value);
                    if (match is not null) return match;

                    var before = await scroller.EvaluateAsync<double>("el => el.scrollTop");
                    await scroller.EvaluateAsync("el => { el.scrollTop += Math.max(260, el.clientHeight * 0.8); }");
                    await Task.Delay(160, ct);
                    var after = await scroller.EvaluateAsync<double>("el => el.scrollTop");
                    if (Math.Abs(after - before) < 1) break;
                }
            }
            catch { }
        }

        return null;
    }

    private static async Task<List<string>> ReadVisibleVoiceLabelsAsync(IPage page)
    {
        var result = new List<string>();
        var items = page.Locator("[role='option'], [role='menuitem'], [role='menuitemradio'], [role='listitem'], li");
        var count = Math.Min(await items.CountAsync(), 100);
        for (var i = 0; i < count; i++)
        {
            try
            {
                var item = items.Nth(i);
                if (!await item.IsVisibleAsync()) continue;
                var label = await ReadLocatorLabelAsync(item);
                if (string.IsNullOrWhiteSpace(label) || label.Length > 80) continue;
                if (!result.Contains(label, StringComparer.OrdinalIgnoreCase)) result.Add(label);
            }
            catch { }
        }
        return result;
    }

    private static async Task<bool> IsVoiceSelectedAsync(IPage page, string requested, string matchedLabel)
    {
        var expectedLabels = new[] { requested, matchedLabel }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        try
        {
            var selected = page.Locator("[aria-selected='true'], [aria-checked='true']");
            var count = await selected.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var item = selected.Nth(i);
                if (!await item.IsVisibleAsync()) continue;
                var label = await ReadLocatorLabelAsync(item);
                if (expectedLabels.Any(expected => VoiceLabelMatches(label, expected))) return true;
            }
        }
        catch { }

        // After the popup closes, the selector itself should display the chosen voice.
        try
        {
            var controls = page.Locator("[role='combobox'], button[aria-haspopup='listbox'], button[aria-haspopup='menu'], button");
            var count = Math.Min(await controls.CountAsync(), 350);
            for (var i = 0; i < count; i++)
            {
                var item = controls.Nth(i);
                if (!await item.IsVisibleAsync()) continue;
                var label = await ReadLocatorLabelAsync(item);
                if (expectedLabels.Any(expected => VoiceLabelMatches(label, expected))) return true;
            }
        }
        catch { }

        return false;
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

    private static bool VoiceLabelMatches(string candidate, string requested)
    {
        candidate = CollapseWhitespace(candidate).Trim();
        requested = CollapseWhitespace(requested).Trim();
        if (candidate.Length == 0 || requested.Length == 0) return false;
        if (candidate.Equals(requested, StringComparison.OrdinalIgnoreCase)) return true;

        var separators = new[] { " ", " (", " -", " –", " —", ",", ":" };
        return separators.Any(separator => candidate.StartsWith(requested + separator, StringComparison.OrdinalIgnoreCase));
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

        var control = await BrowserLocator.FirstVisibleAsync(page, controls, 1800);
        if (control is null)
        {
            log?.Invoke($"Clipchamp {label} selector not found; keeping current selection.");
            return;
        }

        try
        {
            await control.ClickAsync();
            var option = page.GetByText(value, new() { Exact = true }).Last;
            await option.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3500 });
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
