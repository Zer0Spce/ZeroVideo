from pathlib import Path

# Clipchamp: keep the user's manually selected voice for the entire TTS run,
# never reload Clipchamp after Continue TTS, and verify narration was really pasted.
svc_path = Path('src/ZeroVideo.App/Services/ClipchampTtsService.cs')
s = svc_path.read_text()
s = s.replace('_manualVoiceConfirmedForNextChapter', '_manualVoiceSessionActive')

prep_anchor = '''    public async Task PrepareManualVoiceSelectionAsync(ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)\n    {\n'''
if prep_anchor not in s:
    raise SystemExit('PrepareManualVoiceSelectionAsync anchor not found')
s = s.replace(prep_anchor, prep_anchor + '        _manualVoiceSessionActive = false;\n', 1)

start_token = '        var manualVoiceConfirmed = _manualVoiceSessionActive;'
end_token = '        log?.Invoke($"Clipchamp chapter {chapter.Number}: preparing text-to-speech.");'
start = s.find(start_token)
end = s.find(end_token, start)
if start < 0 or end < 0:
    raise SystemExit('GenerateChapter manual voice block not found')
replacement = '''        if (_manualVoiceSessionActive)\n        {\n            log?.Invoke($"Clipchamp chapter {chapter.Number}: manual voice session active. ZeroVideo will not refresh Clipchamp or change the selected voice.");\n\n            var existingInput = await TryFindScriptInputOnceAsync(page, profile.Clipchamp.ScriptInputs);\n            if (existingInput is null)\n            {\n                // Stay in the current Clipchamp project/session. Never Goto() here: a reload resets\n                // Clipchamp's manually selected TTS voice back to its default.\n                var entry = await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.TextToSpeechButtons, 5000, ct)\n                    ?? await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.NewVideoButtons, 5000, ct);\n                if (entry is not null)\n                {\n                    try\n                    {\n                        await entry.ClickAsync(new() { Timeout = 10000 });\n                        await Task.Delay(1000, ct);\n                    }\n                    catch { }\n                }\n            }\n        }\n        else\n        {\n            var existingInput = await TryFindScriptInputOnceAsync(page, profile.Clipchamp.ScriptInputs);\n            if (existingInput is null)\n            {\n                await page.GotoAsync(settings.ClipchampUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });\n                var entry = await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.NewVideoButtons, 8000, ct)\n                    ?? await FirstVisibleAcrossFramesAsync(page, profile.Clipchamp.TextToSpeechButtons, 8000, ct);\n                if (entry is not null)\n                {\n                    await entry.ClickAsync(new() { Timeout = 10000 });\n                    await Task.Delay(1200, ct);\n                }\n            }\n            log?.Invoke($"Clipchamp chapter {chapter.Number}: using Clipchamp's current voice selection.");\n        }\n\n'''
s = s[:start] + replacement + s[end:]

fill_old = '            await input.FillAsync(chapter.Narration, new() { Timeout = 15000 });'
fill_new = '            await FillNarrationAndVerifyAsync(input, chapter.Narration, log, ct);'
if fill_old not in s:
    raise SystemExit('Narration FillAsync anchor not found')
s = s.replace(fill_old, fill_new, 1)

helper_anchor = '    private static async Task WaitForTtsPanelReadyAsync(IPage page, Action<string>? log, CancellationToken ct)\n'
if helper_anchor not in s:
    raise SystemExit('WaitForTtsPanelReadyAsync anchor not found')
helper = '''    private static async Task FillNarrationAndVerifyAsync(ILocator input, string narration, Action<string>? log, CancellationToken ct)\n    {\n        var expected = narration.Trim();\n        if (expected.Length == 0)\n            throw new InvalidOperationException("The chapter narration is empty; TTS cannot be created.");\n\n        await input.ClickAsync(new() { Timeout = 10000 });\n        await input.FillAsync(expected, new() { Timeout = 20000 });\n        await Task.Delay(450, ct);\n\n        var actual = await ReadEditorTextAsync(input);\n        var required = Math.Min(120, Math.Max(20, expected.Length / 3));\n        if (actual.Trim().Length < required)\n        {\n            // React/contenteditable can occasionally ignore a first programmatic fill while hydrating.\n            // Retry once, then fail rather than exporting a five-second silent project.\n            await input.ClickAsync(new() { Timeout = 10000 });\n            await input.FillAsync(expected, new() { Timeout = 20000 });\n            await Task.Delay(650, ct);\n            actual = await ReadEditorTextAsync(input);\n        }\n\n        if (actual.Trim().Length < required)\n            throw new InvalidOperationException($"Clipchamp narration did not stick in the Text to speech editor (expected {expected.Length} chars, detected {actual.Trim().Length}). ZeroVideo will not export silent audio.");\n\n        log?.Invoke($"Clipchamp narration verified in the TTS editor ({actual.Trim().Length} characters detected).");\n    }\n\n    private static async Task<string> ReadEditorTextAsync(ILocator input)\n    {\n        try\n        {\n            var value = await input.InputValueAsync(new() { Timeout = 3000 });\n            if (!string.IsNullOrWhiteSpace(value)) return value;\n        }\n        catch { }\n\n        try\n        {\n            var text = await input.InnerTextAsync(new() { Timeout = 3000 });\n            if (!string.IsNullOrWhiteSpace(text)) return text;\n        }\n        catch { }\n\n        try { return await input.TextContentAsync(new() { Timeout = 3000 }) ?? ""; }\n        catch { return ""; }\n    }\n\n'''
s = s.replace(helper_anchor, helper + helper_anchor, 1)
svc_path.write_text(s)

# FFmpeg: validate that chapter audio and the final MP4 actually contain audible sound.
ff_path = Path('src/ZeroVideo.App/Services/FfmpegService.cs')
f = ff_path.read_text()
if 'using System.Text.RegularExpressions;' not in f:
    f = f.replace('using System.Text;\n', 'using System.Text;\nusing System.Text.RegularExpressions;\n', 1)

probe_anchor = '''    public async Task<double> ProbeDurationAsync(string path, ZeroVideoSettings settings, CancellationToken ct = default)\n    {\n        var args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \\\"{path}\\\"";\n        var result = await runner.RunAsync(settings.FfprobePath, args, ct: ct);\n        return double.TryParse(result.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;\n    }\n'''
if probe_anchor not in f:
    raise SystemExit('ProbeDurationAsync anchor not found')
validation = probe_anchor + '''\n    public async Task ValidateAudibleAudioAsync(string path, ZeroVideoSettings settings, string label, Action<string>? log = null, CancellationToken ct = default)\n    {\n        await EnsureToolsAsync(settings, log, ct);\n        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))\n            throw new FileNotFoundException($"{label} audio file is missing.", path);\n\n        var streamArgs = $"-v error -select_streams a:0 -show_entries stream=codec_type -of csv=p=0 \\\"{path}\\\"";\n        var stream = await runner.RunAsync(settings.FfprobePath, streamArgs, ct: ct);\n        if (stream.ExitCode != 0 || !stream.StdOut.Contains("audio", StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException($"{label} has no audio stream.");\n\n        var duration = await ProbeDurationAsync(path, settings, ct);\n        if (duration < 0.5)\n            throw new InvalidOperationException($"{label} audio is only {duration:0.00}s long.");\n\n        var volume = await runner.RunAsync(settings.FfmpegPath, $"-hide_banner -nostats -i \\\"{path}\\\" -af volumedetect -f null NUL", ct: ct);\n        var report = (volume.StdOut ?? "") + "\\n" + (volume.StdErr ?? "");\n        var match = Regex.Match(report, @"max_volume:\\s*(?<v>-?inf|-?\\d+(?:\\.\\d+)?)\\s*dB", RegexOptions.IgnoreCase);\n        if (!match.Success)\n            throw new InvalidOperationException($"{label} audio could not be verified for audible sound.");\n\n        var raw = match.Groups["v"].Value;\n        if (raw.Equals("-inf", StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException($"{label} is completely silent.");\n\n        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxDb))\n            throw new InvalidOperationException($"{label} audio level could not be parsed.");\n        if (maxDb <= -60)\n            throw new InvalidOperationException($"{label} is effectively silent (max volume {maxDb:0.0} dB).");\n\n        log?.Invoke($"Audio validation passed: {label} ({duration:0.0}s, max {maxDb:0.0} dB).");\n    }\n'''
f = f.replace(probe_anchor, validation, 1)

concat_old = '        var concat = await runner.RunAsync(settings.FfmpegPath, $"-y -f concat -safe 0 -i \\\"{concatList}\\\" -c copy \\\"{voiceFinal}\\\"", renderDir, ct);'
concat_new = '        var concat = await runner.RunAsync(settings.FfmpegPath, $"-y -f concat -safe 0 -i \\\"{concatList}\\\" -map 0:v:0 -map 0:a:0 -c:v copy -c:a aac -b:a 192k \\\"{voiceFinal}\\\"", renderDir, ct);'
if concat_old not in f:
    raise SystemExit('FFmpeg chapter concat anchor not found')
f = f.replace(concat_old, concat_new, 1)

return_anchor = '''        if (project.Options.GenerateCaptions)\n            await WriteSrtAsync(project, Path.ChangeExtension(finalPath, ".srt"), ct);\n\n        return finalPath;\n'''
if return_anchor not in f:
    raise SystemExit('Render final return anchor not found')
f = f.replace(return_anchor, '''        if (project.Options.GenerateCaptions)\n            await WriteSrtAsync(project, Path.ChangeExtension(finalPath, ".srt"), ct);\n\n        await ValidateAudibleAudioAsync(finalPath, settings, "Final video", log, ct);\n        return finalPath;\n''', 1)
ff_path.write_text(f)

# Pipeline: never reuse a silent/broken chapter audio file; validate newly exported TTS too.
p_path = Path('src/ZeroVideo.App/Services/PipelineService.cs')
p = p_path.read_text()
old_loop = '''            AdoptExistingAudio(project, chapter);\n            if (string.IsNullOrWhiteSpace(chapter.AudioPath) || !File.Exists(chapter.AudioPath))\n            {\n                log?.Invoke($"Generating TTS chapter {chapter.Number}/{project.Chapters.Count}: {chapter.Title}");\n                await clipchamp.GenerateChapterAsync(chapter, project, settings, log, ct);\n                await store.SaveAsync(project, settings.ProjectsRoot);\n            }\n            else log?.Invoke($"Chapter {chapter.Number}: using existing audio.");\n'''
if old_loop not in p:
    raise SystemExit('Pipeline audio loop anchor not found')
new_loop = '''            AdoptExistingAudio(project, chapter);\n\n            if (!string.IsNullOrWhiteSpace(chapter.AudioPath) && File.Exists(chapter.AudioPath))\n            {\n                try\n                {\n                    await ffmpeg.ValidateAudibleAudioAsync(chapter.AudioPath, settings, $"Chapter {chapter.Number} existing audio", log, ct);\n                }\n                catch (Exception ex)\n                {\n                    log?.Invoke($"Chapter {chapter.Number}: existing audio is invalid/silent and will be regenerated — {ex.Message}");\n                    try { File.Delete(chapter.AudioPath); } catch { }\n                    chapter.AudioPath = "";\n                    await store.SaveAsync(project, settings.ProjectsRoot);\n                }\n            }\n\n            if (string.IsNullOrWhiteSpace(chapter.AudioPath) || !File.Exists(chapter.AudioPath))\n            {\n                log?.Invoke($"Generating TTS chapter {chapter.Number}/{project.Chapters.Count}: {chapter.Title}");\n                await clipchamp.GenerateChapterAsync(chapter, project, settings, log, ct);\n                await ffmpeg.ValidateAudibleAudioAsync(chapter.AudioPath, settings, $"Chapter {chapter.Number} generated TTS", log, ct);\n                await store.SaveAsync(project, settings.ProjectsRoot);\n            }\n            else log?.Invoke($"Chapter {chapter.Number}: using validated existing audio.");\n'''
p = p.replace(old_loop, new_loop, 1)
p_path.write_text(p)

# Main VM: validate existing audio before deciding whether a manual TTS checkpoint is needed.
vm_path = Path('src/ZeroVideo.App/ViewModels/MainViewModel.cs')
v = vm_path.read_text()
old_needs = '            var needsTts = Project.Chapters.Any(c => string.IsNullOrWhiteSpace(c.AudioPath) || !File.Exists(c.AudioPath));\n'
if old_needs not in v:
    raise SystemExit('MainViewModel needsTts anchor not found')
new_needs = '''            var needsTts = false;\n            foreach (var chapter in Project.Chapters)\n            {\n                if (string.IsNullOrWhiteSpace(chapter.AudioPath) || !File.Exists(chapter.AudioPath))\n                {\n                    needsTts = true;\n                    continue;\n                }\n\n                try\n                {\n                    await _ffmpeg.ValidateAudibleAudioAsync(chapter.AudioPath, Settings, $"Chapter {chapter.Number} existing audio", Log, _cts.Token);\n                }\n                catch (Exception ex)\n                {\n                    Log($"Chapter {chapter.Number}: existing audio failed validation and will be regenerated — {ex.Message}");\n                    try { File.Delete(chapter.AudioPath); } catch { }\n                    chapter.AudioPath = "";\n                    needsTts = true;\n                }\n            }\n            if (needsTts) await _store.SaveAsync(Project, Settings.ProjectsRoot);\n'''
v = v.replace(old_needs, new_needs, 1)
vm_path.write_text(v)

print('Patched manual voice session persistence, narration verification, and audible-audio validation.')
