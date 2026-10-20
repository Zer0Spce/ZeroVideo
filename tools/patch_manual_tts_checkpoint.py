from pathlib import Path

vm = Path('src/ZeroVideo.App/ViewModels/MainViewModel.cs')
text = vm.read_text()

if '_waitingForTtsContinue' not in text:
    text = text.replace('private bool _newChat;', 'private bool _newChat;\n    private bool _waitingForTtsContinue;\n    private TaskCompletionSource<bool>? _ttsContinueGate;', 1)
    text = text.replace('RunPipelineCommand = new AsyncCommand(RunPipelineAsync, CanBuildVideo);', 'RunPipelineCommand = new AsyncCommand(RunPipelineAsync, CanBuildVideo);\n        ContinueTtsCommand = new RelayCommand(ContinueTts);', 1)
    text = text.replace('public bool NewChat { get => _newChat; set => Set(ref _newChat, value); }', 'public bool NewChat { get => _newChat; set => Set(ref _newChat, value); }\n    public bool WaitingForTtsContinue { get => _waitingForTtsContinue; private set => Set(ref _waitingForTtsContinue, value); }', 1)
    text = text.replace('public ICommand RunPipelineCommand { get; }', 'public ICommand RunPipelineCommand { get; }\n    public ICommand ContinueTtsCommand { get; }', 1)

    start_token = 'Status = "Running long-form pipeline...";'
    end_token = 'var output = await _pipeline.RunAsync(Project, Settings, Log, p, _cts.Token);'
    start = text.find(start_token)
    end = text.find(end_token, start)
    if start < 0 or end < 0:
        raise SystemExit('RunPipelineAsync boundaries not found')
    line_start = text.rfind('\n', 0, start) + 1
    line_end = text.find('\n', end)
    indent = text[line_start:start]
    block = '''var needsTts = Project.Chapters.Any(c => string.IsNullOrWhiteSpace(c.AudioPath) || !File.Exists(c.AudioPath));
if (needsTts)
{
    _ttsContinueGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    WaitingForTtsContinue = true;
    Status = "Clipchamp: choose your voice manually, then click Continue TTS";
    Log("MANUAL TTS: choose the voice you want in Clipchamp. When Text to speech is ready, click Continue TTS in ZeroVideo.");
    await _clip.PrepareManualVoiceSelectionAsync(Settings, Log, _cts.Token);
    await _ttsContinueGate.Task.WaitAsync(_cts.Token);
    WaitingForTtsContinue = false;
    _clip.ConfirmManualVoiceSelection();
    Log("Manual Clipchamp voice confirmed. ZeroVideo will paste narration and continue without changing your voice.");
}

Status = "Running long-form pipeline...";
var p = new Progress<double>(x => Progress = x);
var output = await _pipeline.RunAsync(Project, Settings, Log, p, _cts.Token);'''
    block = '\n'.join(indent + line if line else '' for line in block.splitlines())
    text = text[:line_start] + block + text[line_end:]

    marker = '    private void ArmNewChatIfRequested()\n'
    if marker not in text:
        raise SystemExit('ArmNewChat marker not found')
    continue_method = '''    private void ContinueTts()
    {
        if (!WaitingForTtsContinue || _ttsContinueGate is null) return;
        _ttsContinueGate.TrySetResult(true);
    }

'''
    text = text.replace(marker, continue_method + marker, 1)

    fail_token = 'private void Fail(Exception ex)'
    fail_at = text.find(fail_token)
    if fail_at < 0:
        raise SystemExit('Fail method not found')
    brace_at = text.find('{', fail_at)
    insert_at = text.find('\n', brace_at) + 1
    text = text[:insert_at] + '        WaitingForTtsContinue = false;\n        _ttsContinueGate?.TrySetCanceled();\n        _ttsContinueGate = null;\n' + text[insert_at:]
    vm.write_text(text)

xaml = Path('src/ZeroVideo.App/MainWindow.xaml')
ui = xaml.read_text()
if 'Content="Continue TTS"' not in ui:
    needle = '<Button Style="{StaticResource PrimaryButton}" Command="{Binding RunPipelineCommand}" Content="2. Build video"/>'
    if needle not in ui:
        raise SystemExit('Build button not found')
    ui = ui.replace(needle, needle + '<Button Style="{StaticResource PrimaryButton}" Command="{Binding ContinueTtsCommand}" Content="Continue TTS" IsEnabled="{Binding WaitingForTtsContinue}" ToolTip="Choose your voice manually in Clipchamp, then click here to continue narration and export."/>', 1)
    xaml.write_text(ui)

svc = Path('src/ZeroVideo.App/Services/ClipchampTtsService.cs')
s = svc.read_text()

if '_manualVoiceConfirmedForNextChapter' not in s:
    class_token = 'public sealed class ClipchampTtsService(BrowserSession browser, AutomationProfileService profiles)\n{'
    if class_token not in s:
        raise SystemExit('Clipchamp class not found')
    s = s.replace(class_token, class_token + '\n    private bool _manualVoiceConfirmedForNextChapter;\n', 1)

    open_token = 'public Task OpenAsync(ZeroVideoSettings settings)\n        => browser.OpenManualAsync(settings, settings.ClipchampUrl);'
    if open_token not in s:
        raise SystemExit('OpenAsync not found')
    manual_api = open_token + '''

    public async Task PrepareManualVoiceSelectionAsync(ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)
    {
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
        => _manualVoiceConfirmedForNextChapter = true;'''
    s = s.replace(open_token, manual_api, 1)

    method_start = s.find('public async Task<string> GenerateChapterAsync(')
    page_token = 'var page = await browser.GetOrCreatePageAsync("clipchamp");'
    page_start = s.find(page_token, method_start)
    input_token = 'var input = await WaitForScriptInputAsync(page, profile.Clipchamp.ScriptInputs, 30000, ct)'
    nav_end = s.find(input_token, page_start)
    if method_start < 0 or page_start < 0 or nav_end < 0:
        raise SystemExit('GenerateChapter navigation boundaries not found')
    line_start = s.rfind('\n', 0, page_start) + 1
    line_end = s.rfind('\n', 0, nav_end) + 1
    indent = s[line_start:page_start]
    replacement = '''var page = await browser.GetOrCreatePageAsync("clipchamp");
var manualVoiceConfirmed = _manualVoiceConfirmedForNextChapter;
if (manualVoiceConfirmed)
{
    _manualVoiceConfirmedForNextChapter = false;
    log?.Invoke($"Clipchamp chapter {chapter.Number}: using the voice you selected manually. ZeroVideo will not change it.");
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
    log?.Invoke($"Clipchamp chapter {chapter.Number}: keeping the current manually selected Clipchamp voice.");
}

log?.Invoke($"Clipchamp chapter {chapter.Number}: preparing text-to-speech.");

try
{'''
    replacement = '\n'.join(indent + line if line else '' for line in replacement.splitlines()) + '\n'
    s = s[:line_start] + replacement + s[line_end:]

    chosen_start = s.find('await TryChooseAcrossFramesAsync(', method_start)
    if chosen_start >= 0:
        chosen_line_start = s.rfind('\n', 0, chosen_start) + 1
        input_line = s.find('var input = await WaitForScriptInputAsync', chosen_start)
        if input_line >= 0:
            input_line_start = s.rfind('\n', 0, input_line) + 1
            s = s[:chosen_line_start] + s[input_line_start:]

    s = s.replace('log?.Invoke($"TTS created with voice \'{(string.IsNullOrWhiteSpace(selectedVoice) ? "Clipchamp default" : selectedVoice)}\'. Preparing audio-only export...");', 'log?.Invoke("TTS created with the current manually selected Clipchamp voice. Preparing audio-only export...");', 1)
    svc.write_text(s)

print('manual TTS checkpoint patch applied')
