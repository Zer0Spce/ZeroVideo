from pathlib import Path

p = Path('src/ZeroVideo.App/Services/ClipchampTtsService.cs')
s = p.read_text(encoding='utf-8')

old = '''                            // Deleting the old TTS clip can close the right-side editor. Reopen a fresh
                            // Text to speech editor without navigating/reloading so the manually selected
                            // voice remains active for the whole run.
                            var freshTts = await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.TextToSpeechButtons, 5000, ct);
                            if (freshTts is not null)
                            {
                                try
                                {
                                    await freshTts.ClickAsync(new() { Timeout = 10000 });
                                    await Task.Delay(900, ct);
                                }
                                catch { }
                            }
'''
new = '''                            // Keep editing returns Clipchamp to the empty editor. Re-enter the TTS tool
                            // through Record & create -> Text to speech without reloading the page, so the
                            // manually selected voice remains active for the whole run.
                            await OpenFreshTtsEditorAsync(page, profile, log, ct);
'''
if old not in s:
    raise SystemExit('fresh TTS reopen block not found')
s = s.replace(old, new, 1)

old2 = '''                if (existingInput is null)
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
'''
new2 = '''                if (existingInput is null)
                {
                    // Stay in the current Clipchamp project/session. Never Goto() here: a reload resets
                    // Clipchamp's manually selected TTS voice back to its default. Re-enter TTS from the
                    // editor's current state instead.
                    await OpenFreshTtsEditorAsync(page, profile, log, ct);
                }
'''
if old2 not in s:
    raise SystemExit('fallback TTS entry block not found')
s = s.replace(old2, new2, 1)

old3 = '''            await FillNarrationAndVerifyAsync(input, chapter.Narration, log, ct);
            var durationBeforeSave = await TryReadPlaybackTotalSecondsAsync(page);
            await ClickFirstAcrossFramesAsync(page, profile.Clipchamp.SaveButtons, "Clipchamp TTS Save", 15000, ct);
            await WaitForSynthesizedTtsAsync(page, chapter.Narration, durationBeforeSave, log, ct);
'''
new3 = '''            await FillNarrationAndVerifyAsync(input, chapter.Narration, log, ct);
            var durationBeforeSave = await TryReadPlaybackTotalSecondsAsync(page);
            await ClickFirstAcrossFramesAsync(page, profile.Clipchamp.SaveButtons, "Clipchamp TTS Save", 15000, ct);
            await WaitForTtsSaveCompletionAsync(page, profile.Clipchamp.SaveButtons, log, ct);
            await WaitForSynthesizedTtsAsync(page, chapter.Narration, durationBeforeSave, log, ct);
'''
if old3 not in s:
    raise SystemExit('save/synthesis block not found')
s = s.replace(old3, new3, 1)

anchor = '''    private static async Task WaitForSynthesizedTtsAsync(IPage page, string narration, double? durationBeforeSave, Action<string>? log, CancellationToken ct)
'''
helpers = '''    private static async Task OpenFreshTtsEditorAsync(IPage page, AutomationProfile profile, Action<string>? log, CancellationToken ct)
    {
        if (await TryFindScriptInputOnceAsync(page, profile.Clipchamp.ScriptInputs) is not null)
            return;

        var recordCreate = await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.RecordCreateButtons, 6000, ct)
            ?? await FindVisibleExactTextAcrossFramesAsync(page, "Record & create");
        if (recordCreate is not null)
        {
            try
            {
                await (await PromoteToClickableAsync(recordCreate)).ClickAsync(new() { Timeout = 10000 });
                log?.Invoke("Clipchamp: opened Record & create for the next TTS chapter.");
                await Task.Delay(900, ct);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Clipchamp: Record & create was visible but could not be clicked yet ({ex.Message}).");
            }
        }

        var tts = await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.TextToSpeechButtons, 10000, ct)
            ?? await FindVisibleExactTextAcrossFramesAsync(page, "Text to speech");
        if (tts is null)
            throw new InvalidOperationException("Clipchamp returned to the editor, but ZeroVideo could not open Record & create -> Text to speech for the next chapter.");

        await (await PromoteToClickableAsync(tts)).ClickAsync(new() { Timeout = 10000 });
        log?.Invoke("Clipchamp: opened Text to speech for the next chapter.");
        await Task.Delay(900, ct);

        var input = await WaitForScriptInputAsync(page, profile.Clipchamp.ScriptInputs, 12000, ct);
        if (input is null)
            throw new InvalidOperationException("Clipchamp Text to speech opened, but the narration editor did not become available for the next chapter.");
    }

    private static async Task WaitForTtsSaveCompletionAsync(IPage page, IEnumerable<string> saveSelectors, Action<string>? log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var stableDisabledPolls = 0;
        var lastLog = DateTime.MinValue;
        log?.Invoke("Clipchamp: waiting for the TTS Save operation to finish before export...");
        await Task.Delay(1000, ct);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var enabledSave = false;
            foreach (var frame in page.Frames.Reverse())
            {
                foreach (var selector in saveSelectors.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    if (await FindVisibleEnabledAsync(frame, selector) is not null)
                    {
                        enabledSave = true;
                        break;
                    }
                }
                if (enabledSave) break;
            }

            if (!enabledSave)
            {
                stableDisabledPolls++;
                if (stableDisabledPolls >= 3)
                {
                    log?.Invoke("Clipchamp TTS Save completed. Waiting for the generated narration clip to finish synthesizing...");
                    return;
                }
            }
            else
            {
                stableDisabledPolls = 0;
            }

            if ((DateTime.UtcNow - lastLog).TotalSeconds >= 6)
            {
                log?.Invoke("Clipchamp TTS is still saving; Export will not be opened yet.");
                lastLog = DateTime.UtcNow;
            }
            await Task.Delay(500, ct);
        }

        throw new InvalidOperationException("Clipchamp TTS Save did not finish within 90 seconds. ZeroVideo will not export until the TTS is saved.");
    }

'''
if anchor not in s:
    raise SystemExit('helper anchor not found')
s = s.replace(anchor, helpers + anchor, 1)

p.write_text(s, encoding='utf-8')
