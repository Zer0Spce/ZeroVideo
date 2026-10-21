from pathlib import Path

clip = Path('src/ZeroVideo.App/Services/ClipchampTtsService.cs')
s = clip.read_text()

if 'using System.Text.RegularExpressions;' not in s:
    s = s.replace('using Microsoft.Playwright;\n', 'using Microsoft.Playwright;\nusing System.Text.RegularExpressions;\n', 1)

old_save = '''            await FillNarrationAndVerifyAsync(input, chapter.Narration, log, ct);
            await ClickFirstAcrossFramesAsync(page, profile.Clipchamp.SaveButtons, "Clipchamp TTS Save", 15000, ct);
            log?.Invoke("TTS created with the current manually selected Clipchamp voice. Preparing audio-only export...");

            await Task.Delay(2000, ct);'''
new_save = '''            await FillNarrationAndVerifyAsync(input, chapter.Narration, log, ct);
            var durationBeforeSave = await TryReadPlaybackTotalSecondsAsync(page);
            await ClickFirstAcrossFramesAsync(page, profile.Clipchamp.SaveButtons, "Clipchamp TTS Save", 15000, ct);
            await WaitForSynthesizedTtsAsync(page, chapter.Narration, durationBeforeSave, log, ct);
            log?.Invoke("TTS synthesis finished with the current manually selected Clipchamp voice. Preparing audio-only export...");

            await Task.Delay(1000, ct);'''
if old_save not in s:
    raise SystemExit('Clipchamp Save/export anchor not found')
s = s.replace(old_save, new_save, 1)

old_manual = '''            var existingInput = await TryFindScriptInputOnceAsync(page, profile.Clipchamp.ScriptInputs);
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
            }'''
new_manual = '''            var existingInput = await TryFindScriptInputOnceAsync(page, profile.Clipchamp.ScriptInputs);
            if (existingInput is null)
            {
                // Export overlays can hide the editor. Close them first, then try to re-select the
                // previous TTS clip so we EDIT the same clip instead of stacking another 5-second
                // "New text to speech" placeholder on the timeline.
                for (var escapeAttempt = 0; escapeAttempt < 3 && existingInput is null; escapeAttempt++)
                {
                    try { await page.Keyboard.PressAsync("Escape"); } catch { }
                    await Task.Delay(250, ct);
                    existingInput = await TryFindScriptInputOnceAsync(page, profile.Clipchamp.ScriptInputs);
                }

                if (existingInput is null && chapter.Number > 1)
                {
                    var previous = project.Chapters
                        .Where(c => c.Number < chapter.Number && !string.IsNullOrWhiteSpace(c.Narration))
                        .OrderByDescending(c => c.Number)
                        .FirstOrDefault();
                    if (previous is not null && await TryOpenTimelineTtsClipAsync(page, previous.Narration, log, ct))
                        existingInput = await WaitForScriptInputAsync(page, profile.Clipchamp.ScriptInputs, 5000, ct);
                }

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
            }'''
if old_manual not in s:
    raise SystemExit('Manual session navigation anchor not found')
s = s.replace(old_manual, new_manual, 1)

method_anchor = '    private static async Task FillNarrationAndVerifyAsync(ILocator input, string narration, Action<string>? log, CancellationToken ct)\n'
if method_anchor not in s:
    raise SystemExit('FillNarration method anchor not found')
helpers = r'''    private static async Task WaitForSynthesizedTtsAsync(IPage page, string narration, double? durationBeforeSave, Action<string>? log, CancellationToken ct)
    {
        var words = Regex.Matches(narration, @"\b[\p{L}\p{N}][\p{L}\p{N}'’-]*\b").Count;
        var minimumExpectedSeconds = Math.Max(8.0, words / 5.0); // very conservative: <= 300 spoken words/minute
        var deadline = DateTime.UtcNow.AddMinutes(3);
        var notBefore = DateTime.UtcNow.AddSeconds(12); // Clipchamp initially leaves a 5-second placeholder while synthesis runs.
        var lastLog = DateTime.MinValue;
        var bestDuration = durationBeforeSave.GetValueOrDefault();
        var stableReadyPolls = 0;

        log?.Invoke($"Clipchamp: waiting for the real TTS clip to finish synthesizing (minimum plausible duration {minimumExpectedSeconds:0.0}s; default 5s placeholders are rejected).");

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var duration = await TryReadPlaybackTotalSecondsAsync(page);
            if (duration.HasValue)
            {
                bestDuration = Math.Max(bestDuration, duration.Value);
                if (DateTime.UtcNow >= notBefore && duration.Value >= minimumExpectedSeconds)
                {
                    stableReadyPolls++;
                    if (stableReadyPolls >= 2)
                    {
                        log?.Invoke($"Clipchamp TTS ready: timeline duration {duration.Value:0.0}s for {words} spoken word(s). Export may begin.");
                        return;
                    }
                }
                else
                {
                    stableReadyPolls = 0;
                }
            }

            if ((DateTime.UtcNow - lastLog).TotalSeconds >= 5)
            {
                var detected = duration.HasValue ? $"{duration.Value:0.0}s" : "not readable yet";
                log?.Invoke($"Clipchamp TTS is still synthesizing; detected timeline duration: {detected}.");
                lastLog = DateTime.UtcNow;
            }

            await Task.Delay(1000, ct);
        }

        throw new InvalidOperationException($"Clipchamp TTS never became a real-duration clip. Best detected duration was {bestDuration:0.0}s; narration requires at least about {minimumExpectedSeconds:0.0}s. ZeroVideo will not export the 5-second placeholder.");
    }

    private static async Task<double?> TryReadPlaybackTotalSecondsAsync(IPage page)
    {
        // Clipchamp's transport displays values such as "0:00.00 / 0:05.00" while a new TTS clip
        // is still a placeholder. Once synthesis completes, the total grows to the real narration length.
        var regex = new Regex(@"(?<current>\d{1,2}:\d{2}(?::\d{2})?(?:\.\d{1,2})?)\s*/\s*(?<total>\d{1,2}:\d{2}(?::\d{2})?(?:\.\d{1,2})?)", RegexOptions.Compiled);
        double? best = null;

        foreach (var frame in page.Frames.Reverse())
        {
            try
            {
                var text = await frame.Locator("body").InnerTextAsync(new() { Timeout = 2500 });
                foreach (Match match in regex.Matches(text))
                {
                    var parsed = ParseClipchampTime(match.Groups["total"].Value);
                    if (parsed.HasValue && (!best.HasValue || parsed.Value > best.Value)) best = parsed.Value;
                }
            }
            catch { }
        }

        return best;
    }

    private static double? ParseClipchampTime(string value)
    {
        var clean = value.Trim();
        var parts = clean.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        if (!double.TryParse(parts[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)) return null;
        if (!int.TryParse(parts[^2], out var minutes)) return null;
        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], out hours)) return null;
        return hours * 3600 + minutes * 60 + seconds;
    }

    private static async Task<bool> TryOpenTimelineTtsClipAsync(IPage page, string previousNarration, Action<string>? log, CancellationToken ct)
    {
        var sample = CollapseWhitespace(previousNarration);
        if (sample.Length > 70) sample = sample[..70];
        if (sample.Length < 12) return false;

        foreach (var frame in page.Frames.Reverse())
        {
            try
            {
                var matches = frame.GetByText(sample, new() { Exact = false });
                var count = Math.Min(await matches.CountAsync(), 30);
                for (var i = count - 1; i >= 0; i--)
                {
                    var item = matches.Nth(i);
                    if (!await item.IsVisibleAsync()) continue;
                    var clickable = await PromoteToClickableAsync(item);
                    await clickable.ClickAsync(new() { Timeout = 5000 });
                    await Task.Delay(650, ct);
                    log?.Invoke("Clipchamp: re-opened the existing TTS timeline clip instead of creating another placeholder track.");
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

'''
s = s.replace(method_anchor, helpers + method_anchor, 1)
clip.write_text(s)

ff = Path('src/ZeroVideo.App/Services/FfmpegService.cs')
f = ff.read_text()
validate_anchor = '    public async Task<string> RenderAsync(VideoProject project, ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)\n'
if validate_anchor not in f:
    raise SystemExit('Ffmpeg Render anchor not found')
if 'ValidateNarrationAudioAsync' not in f:
    narration_validation = r'''    public async Task ValidateNarrationAudioAsync(string path, ZeroVideoSettings settings, string label, string narration, Action<string>? log = null, CancellationToken ct = default)
    {
        await ValidateAudibleAudioAsync(path, settings, label, log, ct);
        var duration = await ProbeDurationAsync(path, settings, ct);
        var words = Regex.Matches(narration ?? "", @"\b[\p{L}\p{N}][\p{L}\p{N}'’-]*\b").Count;
        var minimumExpectedSeconds = Math.Max(8.0, words / 5.0); // reject Clipchamp's audible-but-wrong 5-second placeholder
        if (duration < minimumExpectedSeconds)
            throw new InvalidOperationException($"{label} is only {duration:0.0}s for {words} narration word(s). Expected at least about {minimumExpectedSeconds:0.0}s; this is probably Clipchamp's 5-second placeholder export.");
        log?.Invoke($"Narration-duration validation passed: {label} ({duration:0.0}s for {words} word(s)).");
    }

'''
    f = f.replace(validate_anchor, narration_validation + validate_anchor, 1)
ff.write_text(f)

pipe = Path('src/ZeroVideo.App/Services/PipelineService.cs')
p = pipe.read_text()
p = p.replace('await ffmpeg.ValidateAudibleAudioAsync(chapter.AudioPath, settings, $"Chapter {chapter.Number} existing audio", log, ct);', 'await ffmpeg.ValidateNarrationAudioAsync(chapter.AudioPath, settings, $"Chapter {chapter.Number} existing audio", chapter.Narration, log, ct);')

old_generate = '''                log?.Invoke($"Generating TTS chapter {chapter.Number}/{project.Chapters.Count}: {chapter.Title}");
                await clipchamp.GenerateChapterAsync(chapter, project, settings, log, ct);
                await ffmpeg.ValidateAudibleAudioAsync(chapter.AudioPath, settings, $"Chapter {chapter.Number} generated TTS", log, ct);
                await store.SaveAsync(project, settings.ProjectsRoot);'''
new_generate = '''                log?.Invoke($"Generating TTS chapter {chapter.Number}/{project.Chapters.Count}: {chapter.Title}");
                Exception? lastTtsError = null;
                for (var ttsAttempt = 1; ttsAttempt <= 2; ttsAttempt++)
                {
                    try
                    {
                        await clipchamp.GenerateChapterAsync(chapter, project, settings, log, ct);
                        await ffmpeg.ValidateNarrationAudioAsync(chapter.AudioPath, settings, $"Chapter {chapter.Number} generated TTS", chapter.Narration, log, ct);
                        lastTtsError = null;
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lastTtsError = ex;
                        log?.Invoke($"Chapter {chapter.Number}: TTS attempt {ttsAttempt}/2 was invalid — {ex.Message}");
                        if (!string.IsNullOrWhiteSpace(chapter.AudioPath))
                        {
                            try { if (File.Exists(chapter.AudioPath)) File.Delete(chapter.AudioPath); } catch { }
                            chapter.AudioPath = "";
                        }
                        await store.SaveAsync(project, settings.ProjectsRoot);
                        if (ttsAttempt < 2) await Task.Delay(2500, ct);
                    }
                }
                if (lastTtsError is not null) throw new InvalidOperationException($"Chapter {chapter.Number} TTS failed twice and was not usable.", lastTtsError);
                await store.SaveAsync(project, settings.ProjectsRoot);'''
if old_generate not in p:
    raise SystemExit('Pipeline generated TTS anchor not found')
p = p.replace(old_generate, new_generate, 1)
pipe.write_text(p)

# Clean up this temporary patch machinery after it applies.
Path('.github/scripts/patch-real-tts-duration.py').unlink(missing_ok=True)
Path('.github/workflows/apply-real-tts-duration-fix.yml').unlink(missing_ok=True)
