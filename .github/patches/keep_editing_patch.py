from pathlib import Path

p = Path('src/ZeroVideo.App/Services/ClipchampTtsService.cs')
s = p.read_text(encoding='utf-8')

# Keep-editing transition (idempotent).
old = '''            var existingInput = await TryFindScriptInputOnceAsync(page, profile.Clipchamp.ScriptInputs);
            if (existingInput is null)
            {
                // Export overlays can hide the editor. Close them first, then try to re-select the
'''
new = '''            var existingInput = await TryFindScriptInputOnceAsync(page, profile.Clipchamp.ScriptInputs);
            if (existingInput is null)
            {
                // After an audio-only export Clipchamp leaves us on the "Your audio is ready" page.
                // Return to the SAME editor/project before preparing the next chapter so the manually
                // selected voice and existing TTS session are preserved.
                var keepEditing = await FindVisibleExactTextAcrossFramesAsync(page, "Keep editing");
                if (keepEditing is not null)
                {
                    try
                    {
                        await (await PromoteToClickableAsync(keepEditing)).ClickAsync(new() { Timeout = 10000 });
                        log?.Invoke($"Clipchamp chapter {chapter.Number}: clicked Keep editing after the previous audio export.");
                        await Task.Delay(1500, ct);
                        existingInput = await WaitForScriptInputAsync(page, profile.Clipchamp.ScriptInputs, 8000, ct);
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"Clipchamp chapter {chapter.Number}: Keep editing was visible but could not be clicked yet ({ex.Message}). Retrying through the existing editor recovery path.");
                    }
                }
            }

            if (existingInput is null)
            {
                // Export overlays can hide the editor. Close them first, then try to re-select the
'''
if old in s:
    s = s.replace(old, new, 1)
elif 'clicked Keep editing after the previous audio export.' not in s:
    raise SystemExit('keep-editing transition target not found')

# After Keep editing, remove ALL TTS clips that ZeroVideo has pasted so far, including placeholders,
# then reopen Text to speech so the next chapter starts from a clean timeline.
old2 = '''                        await Task.Delay(1500, ct);
                        existingInput = await WaitForScriptInputAsync(page, profile.Clipchamp.ScriptInputs, 8000, ct);
'''
new2 = '''                        await Task.Delay(1500, ct);

                        if (chapter.Number > 1)
                        {
                            var pastedNarrations = project.Chapters
                                .Where(c => c.Number <= chapter.Number && !string.IsNullOrWhiteSpace(c.Narration))
                                .Select(c => c.Narration)
                                .ToArray();
                            await DeleteExistingTtsTimelineItemsAsync(page, pastedNarrations, log, ct);

                            // Deleting the old TTS clip can close the right-side editor. Reopen a fresh
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
                        }

                        existingInput = await WaitForScriptInputAsync(page, profile.Clipchamp.ScriptInputs, 8000, ct);
'''
if old2 in s and 'DeleteExistingTtsTimelineItemsAsync(page, pastedNarrations' not in s:
    s = s.replace(old2, new2, 1)

anchor = '''    private static async Task<bool> TryOpenTimelineTtsClipAsync(IPage page, string previousNarration, Action<string>? log, CancellationToken ct)
'''
helper = '''    private static async Task DeleteExistingTtsTimelineItemsAsync(IPage page, IEnumerable<string> narrations, Action<string>? log, CancellationToken ct)
    {
        var labels = new List<string> { "New text to speech" };
        foreach (var narration in narrations)
        {
            var sample = CollapseWhitespace(narration ?? "");
            if (sample.Length > 70) sample = sample[..70];
            if (sample.Length >= 12 && !labels.Contains(sample, StringComparer.OrdinalIgnoreCase))
                labels.Add(sample);
        }

        var deleted = 0;
        for (var pass = 0; pass < 20; pass++)
        {
            ct.ThrowIfCancellationRequested();
            ILocator? best = null;
            double bestY = -1;

            foreach (var frame in page.Frames.Reverse())
            {
                double viewportHeight;
                try { viewportHeight = await frame.EvaluateAsync<double>("() => window.innerHeight"); }
                catch { continue; }

                foreach (var label in labels)
                {
                    try
                    {
                        var matches = frame.GetByText(label, new() { Exact = false });
                        var count = Math.Min(await matches.CountAsync(), 60);
                        for (var i = count - 1; i >= 0; i--)
                        {
                            var item = matches.Nth(i);
                            if (!await item.IsVisibleAsync()) continue;
                            var box = await item.BoundingBoxAsync();
                            if (box is null) continue;

                            // TTS timeline clips live in the lower part of Clipchamp and are short rows.
                            // This avoids ever pressing Delete inside the large narration editor itself.
                            if (box.Y < viewportHeight * 0.50 || box.Height > 120) continue;
                            if (box.Y <= bestY) continue;
                            best = item;
                            bestY = box.Y;
                        }
                    }
                    catch { }
                }
            }

            if (best is null) break;

            try
            {
                var clickable = await PromoteToClickableAsync(best);
                await clickable.ClickAsync(new() { Timeout = 5000 });
                await Task.Delay(120, ct);
                await page.Keyboard.PressAsync("Delete");
                await Task.Delay(350, ct);
                deleted++;
            }
            catch
            {
                break;
            }
        }

        if (deleted > 0)
            log?.Invoke($"Clipchamp: removed {deleted} previous/placeholder TTS timeline clip(s) before pasting the next chapter.");
        else
            log?.Invoke("Clipchamp: no previous TTS timeline clips needed cleanup before the next chapter.");
    }

'''
if 'private static async Task DeleteExistingTtsTimelineItemsAsync' not in s:
    if anchor not in s:
        raise SystemExit('helper insertion anchor not found')
    s = s.replace(anchor, helper + anchor, 1)

p.write_text(s, encoding='utf-8')
