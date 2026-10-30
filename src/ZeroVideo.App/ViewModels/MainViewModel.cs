using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Models;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SettingsService _settingsService = new();
    private readonly ProjectStore _store = new();
    private readonly PromptBuilder _promptBuilder = new();
    private readonly BrowserSession _browser = new();
    private readonly AutomationProfileService _profiles = new();
    private readonly ProcessRunner _runner = new();
    private readonly DependencyService _dependencies = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ChatGptBrowserService _chat;
    private readonly ClipchampTtsService _clip;
    private readonly FfmpegService _ffmpeg;
    private readonly PipelineService _pipeline;
    private readonly PreflightService _preflight;
    private ZeroVideoSettings _settings = new();
    private VideoProject _project = new();
    private string _topic = "";
    private int _duration = 10;
    private string _aspectRatio = "16:9";
    private string _style = "Documentary explainer with fast visual variety";
    private string _status = "Ready";
    private double _progress;
    private string _projectDirectory = "";
    private bool _newChat;
    private bool _waitingForTtsContinue;
    private bool _shortsTtsConfirmed;
    private TaskCompletionSource<bool>? _ttsContinueGate;

    public MainViewModel()
    {
        _chat = new ChatGptBrowserService(_browser, _profiles);
        _clip = new ClipchampTtsService(_browser, _profiles);
        _ffmpeg = new FfmpegService(_runner, _dependencies);
        _pipeline = new PipelineService(_chat, _clip, _ffmpeg, _store);
        _preflight = new PreflightService(_runner);
        GeneratePlanCommand = new AsyncCommand(GeneratePlanAsync, () => !string.IsNullOrWhiteSpace(Topic) && Duration > 0);
        ContinueWritingCommand = new AsyncCommand(ContinueWritingAsync, CanContinueWriting);
        RunPipelineCommand = new AsyncCommand(RunPipelineAsync, CanBuildVideo);
        ContinueTtsCommand = new RelayCommand(ContinueTts);
        OpenChatGptCommand = new AsyncCommand(() => _chat.OpenAsync(Settings));
        OpenClipchampCommand = new AsyncCommand(() => _clip.OpenAsync(Settings));
        InstallFfmpegCommand = new AsyncCommand(InstallFfmpegAsync);
        SaveSettingsCommand = new AsyncCommand(SaveSettingsAsync);
        RunPreflightCommand = new AsyncCommand(RunPreflightAsync);
        OpenProjectFolderCommand = new AsyncCommand(OpenProjectFolderAsync, () => Directory.Exists(ProjectDirectory));
        LoadProjectCommand = new AsyncCommand(LoadProjectAsync);
        CancelCommand = new RelayCommand(() => _cts.Cancel());
        InitializeShortsCommands();
        InitializeIdeasCommands();
        InitializePublishingCommands();
        InitializeTikTokIntelligenceCommands();
        InitializeLibraryAndCalendarCommands();
        InitializeContentSeriesCommands();
        BrowseMusicCommand = new RelayCommand(BrowseMusic);
    }

    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<string> PreflightLines { get; } = [];
    public ObservableCollection<double> MusicLevelsDb { get; } = [0, -5, -10, -15, -20, -25, -30, -35, -40];
    public ObservableCollection<int> Durations { get; } = [5, 10, 15, 20, 30, 45, 60, 90, 120];
    public ObservableCollection<string> AspectRatios { get; } = ["16:9", "9:16"];
    public ObservableCollection<string> Styles { get; } = new(VisualStylePresets.Names);
    public ObservableCollection<string> ClipchampVoices { get; } =
    [
        "Andrew", "Ava", "Brian", "Emma", "Aria", "Christopher", "Eric", "Guy", "Jenny", "Michelle", "Roger", "Steffan"
    ];

    public string Topic { get => _topic; set { if (Set(ref _topic, value)) RefreshCommands(); } }
    public int Duration { get => _duration; set { if (Set(ref _duration, Math.Max(1, value))) RefreshCommands(); } }
    public string AspectRatio { get => _aspectRatio; set => Set(ref _aspectRatio, value); }
    public string Style { get => _style; set => Set(ref _style, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public bool NewChat { get => _newChat; set => Set(ref _newChat, value); }
    public bool WaitingForTtsContinue { get => _waitingForTtsContinue; private set => Set(ref _waitingForTtsContinue, value); }
    public string ProjectDirectory { get => _projectDirectory; set { if (Set(ref _projectDirectory, value)) RefreshCommands(); } }
    public VideoProject Project { get => _project; private set { Set(ref _project, value); Raise(nameof(Chapters)); RefreshCommands(); } }
    public IEnumerable<ProjectChapter> Chapters => Project.Chapters;
    public ZeroVideoSettings Settings { get => _settings; private set => Set(ref _settings, value); }

    public ICommand GeneratePlanCommand { get; }
    public ICommand ContinueWritingCommand { get; }
    public ICommand RunPipelineCommand { get; }
    public ICommand ContinueTtsCommand { get; }
    public ICommand OpenChatGptCommand { get; }
    public ICommand OpenClipchampCommand { get; }
    public ICommand InstallFfmpegCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand RunPreflightCommand { get; }
    public ICommand OpenProjectFolderCommand { get; }
    public ICommand LoadProjectCommand { get; }
    public ICommand BrowseMusicCommand { get; }
    public ICommand CancelCommand { get; }

    public async Task InitializeAsync()
    {
        Settings = await _settingsService.LoadAsync();
        LoadTikTokIntelligence();
        LoadShortsRecovery();
        RefreshPublishingCalendar();
        RefreshVideoLibrary();
        if (string.IsNullOrWhiteSpace(Project.Voice)) Project.Voice = Settings.ClipchampVoice;
        Raise(nameof(Project));
        Log("ZeroVideo initialized.");
        Log("Use Automation > Open ChatGPT / Open Clipchamp to sign in normally. Generation automatically hands that dedicated Edge profile over to automation.");
        Raise(nameof(Settings));
    }

    private async Task GeneratePlanAsync()
    {
        try
        {
            ArmNewChatIfRequested();
            Status = "Creating master outline with ChatGPT...";
            Progress = 3;
            var seed = new VideoProject
            {
                Topic = Topic.Trim(),
                Title = Topic.Trim(),
                TargetMinutes = Duration,
                AspectRatio = AspectRatio,
                Style = Style,
                Voice = Project.Voice,
                Options = Project.Options
            };

            var requestedChapters = Math.Clamp(seed.Options.HowManyChapters, 1, 40);
            var outlinePrompt = _promptBuilder.BuildOutline(seed);
            VideoProject? generated = null;

            for (var shapeAttempt = 1; shapeAttempt <= 2; shapeAttempt++)
            {
                generated = await _chat.GenerateOutlineAsync(seed, Settings, outlinePrompt, Log, _cts.Token);
                if (generated.Chapters.Count == requestedChapters) break;

                if (shapeAttempt >= 2)
                    throw new InvalidDataException($"ChatGPT returned {generated.Chapters.Count} chapters, but How Many Chapters is {requestedChapters}.");

                Log($"Outline shape mismatch: received {generated.Chapters.Count} chapters; requested exactly {requestedChapters}. Retrying in the same project chat.");
                outlinePrompt += $"\nCORRECTION: Return EXACTLY {requestedChapters} chapters. Your previous outline had {generated.Chapters.Count}. Do not change the requested count.";
            }

            Project = generated!;
            ProjectDirectory = await _store.SaveAsync(Project, Settings.ProjectsRoot);
            Progress = 5;
            Log($"Outline checkpoint saved to {ProjectDirectory}");
            await WriteMissingChaptersAsync();
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task ContinueWritingAsync()
    {
        try
        {
            ArmNewChatIfRequested();
            await WriteMissingChaptersAsync();
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task WriteMissingChaptersAsync()
    {
        if (Project.Chapters.Count == 0) throw new InvalidOperationException("No outline is loaded.");
        if (string.IsNullOrWhiteSpace(Project.ProjectDirectory)) ProjectDirectory = await _store.SaveAsync(Project, Settings.ProjectsRoot);

        var requestedSceneCount = Math.Clamp(Project.Options.ImagesPerChapter, 1, 50);
        var count = Project.Chapters.Count;
        for (var i = 0; i < count; i++)
        {
            _cts.Token.ThrowIfCancellationRequested();
            if (IsComplete(Project.Chapters[i])) continue;

            var metadata = Project.Chapters[i];
            Status = $"Writing chapter {i + 1}/{count}: {metadata.Title}";
            Log(Status);
            var basePrompt = _promptBuilder.BuildChapter(Project, metadata, i);
            ProjectChapter? generatedChapter = null;

            for (var shapeAttempt = 1; shapeAttempt <= 2; shapeAttempt++)
            {
                var prompt = basePrompt;
                if (shapeAttempt > 1)
                    prompt += $"\nCORRECTION: Return EXACTLY {requestedSceneCount} scenes, numbered 1 through {requestedSceneCount}. Each scene must contain spoken narration and one matching imagePrompt.";

                generatedChapter = await _chat.GenerateChapterAsync(Project, metadata, Settings, prompt, Log, _cts.Token);
                if (generatedChapter.Scenes.Count == requestedSceneCount) break;

                if (shapeAttempt >= 2)
                    throw new InvalidDataException($"Chapter {metadata.Number} returned {generatedChapter.Scenes.Count} scenes, but Images Per Chapter is {requestedSceneCount}.");

                Log($"Chapter {metadata.Number} shape mismatch: received {generatedChapter.Scenes.Count} scenes; requested exactly {requestedSceneCount}. Retrying in the same project chat.");
            }

            for (var s = 0; s < generatedChapter!.Scenes.Count; s++)
                generatedChapter.Scenes[s].Number = s + 1;
            generatedChapter.Narration = string.Join(" ", generatedChapter.Scenes.Select(s => s.Narration.Trim()).Where(s => s.Length > 0));

            Project.Chapters[i] = generatedChapter;
            await _store.SaveAsync(Project, Settings.ProjectsRoot);
            Progress = 5 + ((i + 1) / (double)count * 20.0);
            Raise(nameof(Chapters));
            RefreshCommands();
            Log($"Chapter {i + 1} checkpoint saved: {generatedChapter.Scenes.Count} narration/image scenes. ChatGPT chat: {Project.ChatGptConversationUrl ?? "pending"}");
        }

        Status = CanBuildVideo() ? $"Script ready: {count} chapters" : "Script paused — use Resume writing";
        if (CanBuildVideo()) Progress = 25;
        RefreshCommands();
    }

    private async Task RunPipelineAsync()
    {
        try
        {
            ArmNewChatIfRequested();
            Settings.ClipchampVoice = Project.Voice;
            await _settingsService.SaveAsync(Settings);
            if (string.IsNullOrWhiteSpace(Project.Voice)) Log("Clipchamp voice: using the current/default Clipchamp voice.");
            else Log($"Clipchamp voice selected: {Project.Voice}");
            if (!string.IsNullOrWhiteSpace(Settings.BackgroundMusicPath))
            {
                if (!File.Exists(Settings.BackgroundMusicPath))
                    throw new FileNotFoundException("Selected background music file not found", Settings.BackgroundMusicPath);
                Project.Options.AddBackgroundMusic = true;
                Project.Options.BackgroundMusicPath = Settings.BackgroundMusicPath;
                Project.Options.BackgroundMusicVolume = Math.Pow(10.0, Math.Clamp(Settings.BackgroundMusicDb, -60, 0) / 20.0);
                Log("Custom background music enabled: " + Path.GetFileName(Settings.BackgroundMusicPath) +
                    " at " + Settings.BackgroundMusicDb.ToString("0") + " dB.");
            }
            else if (Project.Options.AddBackgroundMusic &&
                (string.IsNullOrWhiteSpace(Project.Options.BackgroundMusicPath) || !File.Exists(Project.Options.BackgroundMusicPath)))
                throw new FileNotFoundException("Music is enabled but the project music file is missing.", Project.Options.BackgroundMusicPath);
            var needsTts = false;
            foreach (var chapter in Project.Chapters)
            {
                if (string.IsNullOrWhiteSpace(chapter.AudioPath) || !File.Exists(chapter.AudioPath))
                {
                    needsTts = true;
                    continue;
                }

                try
                {
                    await _ffmpeg.ValidateAudibleAudioAsync(chapter.AudioPath, Settings, $"Chapter {chapter.Number} existing audio", Log, _cts.Token);
                }
                catch (Exception ex)
                {
                    Log($"Chapter {chapter.Number}: existing audio failed validation and will be regenerated — {ex.Message}");
                    try { File.Delete(chapter.AudioPath); } catch { }
                    chapter.AudioPath = "";
                    needsTts = true;
                }
            }
            if (needsTts) await _store.SaveAsync(Project, Settings.ProjectsRoot);
            if (needsTts && !_shortsTtsConfirmed)
            {
                _ttsContinueGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                WaitingForTtsContinue = true;
                Status = "Clipchamp: choose your voice manually, then click Continue TTS";
                Log("MANUAL TTS: choose the voice you want in Clipchamp. When Text to speech is ready, click Continue TTS in ZeroVideo.");
                await _clip.PrepareManualVoiceSelectionAsync(Settings, Log, _cts.Token);
                await _ttsContinueGate.Task.WaitAsync(_cts.Token);
                WaitingForTtsContinue = false;
                _clip.ConfirmManualVoiceSelection();
                if (ShortsRunning) _shortsTtsConfirmed = true;
                Log("Manual Clipchamp voice confirmed. ZeroVideo will paste narration and continue without changing your voice.");
            }

            Status = "Running long-form pipeline...";
            var p = new Progress<double>(x => Progress = x);
            var output = await _pipeline.RunAsync(Project, Settings, Log, p, _cts.Token);
            await _settingsService.SaveAsync(Settings);
            Status = "Finished";
            Progress = 100;
            Log($"FINAL VIDEO: {output}");
            QueueCompletedVideo(output);
            // A finished MP4 is immediately queued. Metadata is generated
            // automatically for BOTH platforms, independent of publishing.
            // Failures are logged and the finished video stays in the queue.
            var finishedItem = PublishingQueue.FirstOrDefault(x =>
                x.VideoPath.Equals(output, StringComparison.OrdinalIgnoreCase));
            if (finishedItem is not null)
            {
                Status = "Finished video; generating YouTube/TikTok SEO metadata...";
                await TryGenerateMetadataForItemAsync(finishedItem, force: false);
                Status = "Finished";
            }
            if (ShortsRunning)
                Log("Shorts automation: video finished and queued for Publishing; continuing to the next video.");
            else
            MessageBox.Show($"Video finished:\n{output}", "ZeroVideo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void BrowseMusic()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select background music",
            Filter = "Audio files|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg|All files|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            Settings.BackgroundMusicPath = dialog.FileName;
            Project.Options.AddBackgroundMusic = true;
            Project.Options.BackgroundMusicPath = dialog.FileName;
            Raise(nameof(Settings));
            Raise(nameof(Project));
            Log($"Background music selected: {dialog.FileName}");
        }
    }

    private void ContinueTts()
    {
        if (!WaitingForTtsContinue || _ttsContinueGate is null) return;
        _ttsContinueGate.TrySetResult(true);
    }

    private void ArmNewChatIfRequested()
    {
        if (!NewChat) return;
        _chat.RequestNewChat();
        NewChat = false;
        Log("New Chat requested: the next ChatGPT operation will start fresh, then ZeroVideo will reuse that chat for all following work.");
    }

    private async Task RunPreflightAsync()
    {
        try
        {
            Status = "Checking system...";
            PreflightLines.Clear();
            var checks = await _preflight.RunAsync(Settings, _cts.Token);
            foreach (var check in checks)
            {
                var line = $"{(check.Passed ? "PASS" : "FAIL")}  {check.Name} — {check.Detail}";
                PreflightLines.Add(line);
                Log(line);
            }
            Status = checks.All(x => x.Passed) ? "System ready" : "Setup needs attention";
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task InstallFfmpegAsync()
    {
        try
        {
            Status = "Installing FFmpeg...";
            var ffmpeg = await _dependencies.InstallFfmpegAsync(Log, _cts.Token);
            Settings.FfmpegPath = ffmpeg;
            Settings.FfprobePath = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
            await _settingsService.SaveAsync(Settings);
            Raise(nameof(Settings));
            Status = "FFmpeg ready";
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task SaveSettingsAsync()
    {
        Settings.ClipchampVoice = Project.Voice;
        await _settingsService.SaveAsync(Settings);
        Status = "Settings saved";
        Log("Settings saved.");
    }

    private Task OpenProjectFolderAsync()
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ProjectDirectory}\"") { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async Task LoadProjectAsync()
    {
        var dialog = new OpenFileDialog { Filter = "ZeroVideo Project|project.json", InitialDirectory = Settings.ProjectsRoot };
        if (dialog.ShowDialog() != true) return;
        Project = await _store.LoadAsync(dialog.FileName);
        if (string.IsNullOrWhiteSpace(Project.Voice)) Project.Voice = Settings.ClipchampVoice;
        Topic = Project.Topic;
        Duration = Project.TargetMinutes;
        AspectRatio = Project.AspectRatio;
        Style = Project.Style;
        ProjectDirectory = Project.ProjectDirectory;
        NewChat = false;
        Raise(nameof(Project));
        Raise(nameof(Chapters));
        Status = CanBuildVideo() ? "Project loaded — ready to build" : "Project loaded — writing can resume";
        Log($"Loaded {Project.Title}");
        if (!string.IsNullOrWhiteSpace(Project.ChatGptConversationUrl)) Log("Saved ChatGPT project chat will be reused.");
        RefreshCommands();
    }

    private bool CanContinueWriting() => Project.Chapters.Count > 0 && Project.Chapters.Any(c => !IsComplete(c));
    private bool CanBuildVideo()
    {
        var requestedChapters = Math.Clamp(Project.Options.HowManyChapters, 1, 40);
        return Project.Chapters.Count == requestedChapters && Project.Chapters.All(IsComplete);
    }

    private bool IsComplete(ProjectChapter chapter)
    {
        var requestedScenes = Math.Clamp(Project.Options.ImagesPerChapter, 1, 50);
        return chapter.Scenes.Count == requestedScenes && !string.IsNullOrWhiteSpace(chapter.Narration);
    }

    private void RefreshCommands()
    {
        (GeneratePlanCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (ContinueWritingCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (RunPipelineCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (OpenProjectFolderCommand as AsyncCommand)?.RaiseCanExecuteChanged();
    }

    private void Log(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (Logs.Count > 500) Logs.RemoveAt(0);
        });
    }

    private void Fail(Exception ex)
    {
        WaitingForTtsContinue = false;
        _ttsContinueGate?.TrySetCanceled();
        _ttsContinueGate = null;
        Status = "Stopped: " + ex.Message;
        Log("ERROR: " + ex);
        MessageBox.Show(ex.Message, "ZeroVideo", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshCommands();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        await _browser.DisposeAsync();
    }

    private sealed class RelayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
