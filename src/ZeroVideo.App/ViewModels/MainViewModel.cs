using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Models;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
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

    public MainViewModel()
    {
        _chat = new ChatGptBrowserService(_browser, _profiles);
        _clip = new ClipchampTtsService(_browser, _profiles);
        _ffmpeg = new FfmpegService(_runner);
        _pipeline = new PipelineService(_chat, _clip, _ffmpeg, _store);
        _preflight = new PreflightService(_runner);
        GeneratePlanCommand = new AsyncCommand(GeneratePlanAsync, () => !string.IsNullOrWhiteSpace(Topic) && Duration > 0);
        ContinueWritingCommand = new AsyncCommand(ContinueWritingAsync, CanContinueWriting);
        RunPipelineCommand = new AsyncCommand(RunPipelineAsync, CanBuildVideo);
        OpenChatGptCommand = new AsyncCommand(() => _browser.OpenManualAsync(Settings, Settings.ChatGptUrl));
        OpenClipchampCommand = new AsyncCommand(() => _browser.OpenManualAsync(Settings, Settings.ClipchampUrl));
        InstallFfmpegCommand = new AsyncCommand(InstallFfmpegAsync);
        SaveSettingsCommand = new AsyncCommand(SaveSettingsAsync);
        RunPreflightCommand = new AsyncCommand(RunPreflightAsync);
        OpenProjectFolderCommand = new AsyncCommand(OpenProjectFolderAsync, () => Directory.Exists(ProjectDirectory));
        LoadProjectCommand = new AsyncCommand(LoadProjectAsync);
        CancelCommand = new RelayCommand(() => _cts.Cancel());
    }

    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<string> PreflightLines { get; } = [];
    public ObservableCollection<int> Durations { get; } = [5, 10, 15, 20, 30, 45, 60, 90, 120];
    public ObservableCollection<string> AspectRatios { get; } = ["16:9", "9:16"];
    public ObservableCollection<string> Styles { get; } = ["Documentary explainer with fast visual variety", "VOX-inspired paper/editorial explainer", "History documentary", "Mystery / investigation", "Science explainer", "Custom"];

    public string Topic { get => _topic; set { if (Set(ref _topic, value)) RefreshCommands(); } }
    public int Duration { get => _duration; set { if (Set(ref _duration, Math.Max(1, value))) RefreshCommands(); } }
    public string AspectRatio { get => _aspectRatio; set => Set(ref _aspectRatio, value); }
    public string Style { get => _style; set => Set(ref _style, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string ProjectDirectory { get => _projectDirectory; set { if (Set(ref _projectDirectory, value)) RefreshCommands(); } }
    public VideoProject Project { get => _project; private set { Set(ref _project, value); Raise(nameof(Chapters)); RefreshCommands(); } }
    public IEnumerable<ProjectChapter> Chapters => Project.Chapters;
    public ZeroVideoSettings Settings { get => _settings; private set => Set(ref _settings, value); }

    public ICommand GeneratePlanCommand { get; }
    public ICommand ContinueWritingCommand { get; }
    public ICommand RunPipelineCommand { get; }
    public ICommand OpenChatGptCommand { get; }
    public ICommand OpenClipchampCommand { get; }
    public ICommand InstallFfmpegCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand RunPreflightCommand { get; }
    public ICommand OpenProjectFolderCommand { get; }
    public ICommand LoadProjectCommand { get; }
    public ICommand CancelCommand { get; }

    public async Task InitializeAsync()
    {
        Settings = await _settingsService.LoadAsync();
        Log("ZeroVideo initialized.");
        Log("First run: use Automation > Open ChatGPT and Open Clipchamp to sign in normally, then close that Edge window before generating or building.");
        Raise(nameof(Settings));
    }

    private async Task GeneratePlanAsync()
    {
        try
        {
            Status = "Creating master outline with ChatGPT..."; Progress = 3;
            var seed = new VideoProject
            {
                Topic = Topic.Trim(), Title = Topic.Trim(), TargetMinutes = Duration, AspectRatio = AspectRatio,
                Style = Style, Options = Project.Options
            };
            var outlinePrompt = _promptBuilder.BuildOutline(seed);
            Project = await _chat.GenerateOutlineAsync(seed, Settings, outlinePrompt, Log, _cts.Token);
            ProjectDirectory = await _store.SaveAsync(Project, Settings.ProjectsRoot);
            Progress = 5;
            Log($"Outline checkpoint saved to {ProjectDirectory}");
            await WriteMissingChaptersAsync();
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task ContinueWritingAsync()
    {
        try { await WriteMissingChaptersAsync(); }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task WriteMissingChaptersAsync()
    {
        if (Project.Chapters.Count == 0) throw new InvalidOperationException("No outline is loaded.");
        if (string.IsNullOrWhiteSpace(Project.ProjectDirectory)) ProjectDirectory = await _store.SaveAsync(Project, Settings.ProjectsRoot);

        var count = Project.Chapters.Count;
        for (var i = 0; i < count; i++)
        {
            _cts.Token.ThrowIfCancellationRequested();
            if (IsComplete(Project.Chapters[i])) continue;

            var metadata = Project.Chapters[i];
            Status = $"Writing chapter {i + 1}/{count}: {metadata.Title}";
            Log(Status);
            var prompt = _promptBuilder.BuildChapter(Project, metadata, i);
            Project.Chapters[i] = await _chat.GenerateChapterAsync(Project, metadata, Settings, prompt, Log, _cts.Token);
            await _store.SaveAsync(Project, Settings.ProjectsRoot);
            Progress = 5 + ((i + 1) / (double)count * 20.0);
            Raise(nameof(Chapters));
            RefreshCommands();
            Log($"Chapter {i + 1} checkpoint saved.");
        }

        Status = CanBuildVideo() ? $"Script ready: {count} chapters" : "Script paused — use Resume writing";
        if (CanBuildVideo()) Progress = 25;
        RefreshCommands();
    }

    private async Task RunPipelineAsync()
    {
        try
        {
            Status = "Running long-form pipeline...";
            var p = new Progress<double>(x => Progress = x);
            var output = await _pipeline.RunAsync(Project, Settings, Log, p, _cts.Token);
            Status = "Finished"; Progress = 100; Log($"FINAL VIDEO: {output}");
            MessageBox.Show($"Video finished:\n{output}", "ZeroVideo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Fail(ex); }
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
                PreflightLines.Add(line); Log(line);
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
            Raise(nameof(Settings)); Status = "FFmpeg ready";
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task SaveSettingsAsync()
    {
        await _settingsService.SaveAsync(Settings); Status = "Settings saved"; Log("Settings saved.");
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
        Topic = Project.Topic; Duration = Project.TargetMinutes; AspectRatio = Project.AspectRatio; Style = Project.Style; ProjectDirectory = Project.ProjectDirectory;
        Raise(nameof(Chapters)); Status = CanBuildVideo() ? "Project loaded — ready to build" : "Project loaded — writing can resume"; Log($"Loaded {Project.Title}");
        RefreshCommands();
    }

    private bool CanContinueWriting() => Project.Chapters.Count > 0 && Project.Chapters.Any(c => !IsComplete(c));
    private bool CanBuildVideo() => Project.Chapters.Count > 0 && Project.Chapters.All(IsComplete);
    private static bool IsComplete(ProjectChapter chapter) => chapter.Scenes.Count > 0 && !string.IsNullOrWhiteSpace(chapter.Narration);

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
        Status = "Stopped: " + ex.Message; Log("ERROR: " + ex); MessageBox.Show(ex.Message, "ZeroVideo", MessageBoxButton.OK, MessageBoxImage.Warning); RefreshCommands();
    }

    public async ValueTask DisposeAsync() { _cts.Cancel(); _cts.Dispose(); await _browser.DisposeAsync(); }

    private sealed class RelayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
