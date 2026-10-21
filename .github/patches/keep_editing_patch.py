from pathlib import Path

p = Path('src/ZeroVideo.App/Services/ClipchampTtsService.cs')
s = p.read_text(encoding='utf-8')
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
if old not in s:
    raise SystemExit('target block not found')
s = s.replace(old, new, 1)
p.write_text(s, encoding='utf-8')
