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
        _pipeline = new PipelineService(_clip, _ffmpeg, _store);
        GeneratePlanCommand = new AsyncCommand(GeneratePlanAsync, () => !string.IsNullOrWhiteSpace(Topic));
        RunPipelineCommand = new AsyncCommand(RunPipelineAsync, () => Project.Chapters.Count > 0);
        OpenChatGptCommand = new AsyncCommand(() => _chat.OpenAsync(Settings));
        OpenClipchampCommand = new AsyncCommand(() => _clip.OpenAsync(Settings));
        InstallFfmpegCommand = new AsyncCommand(InstallFfmpegAsync);
        SaveSettingsCommand = new AsyncCommand(SaveSettingsAsync);
        OpenProjectFolderCommand = new AsyncCommand(OpenProjectFolderAsync, () => Directory.Exists(ProjectDirectory));
        LoadProjectCommand = new AsyncCommand(LoadProjectAsync);
        CancelCommand = new RelayCommand(() => _cts.Cancel());
    }

    private readonly ChatGptBrowserService _chat;
    private readonly ClipchampTtsService _clip;
    private readonly FfmpegService _ffmpeg;
    private readonly PipelineService _pipeline;

    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<int> Durations { get; } = [5, 10, 15, 20, 30, 45, 60];
    public ObservableCollection<string> AspectRatios { get; } = ["16:9", "9:16"];
    public ObservableCollection<string> Styles { get; } = ["Documentary explainer with fast visual variety", "VOX-inspired paper/editorial explainer", "History documentary", "Mystery / investigation", "Science explainer", "Custom"];

    public string Topic { get => _topic; set { if (Set(ref _topic, value)) (GeneratePlanCommand as AsyncCommand)?.RaiseCanExecuteChanged(); } }
    public int Duration { get => _duration; set => Set(ref _duration, value); }
    public string AspectRatio { get => _aspectRatio; set => Set(ref _aspectRatio, value); }
    public string Style { get => _style; set => Set(ref _style, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string ProjectDirectory { get => _projectDirectory; set { if (Set(ref _projectDirectory, value)) (OpenProjectFolderCommand as AsyncCommand)?.RaiseCanExecuteChanged(); } }
    public VideoProject Project { get => _project; private set { Set(ref _project, value); Raise(nameof(Chapters)); (RunPipelineCommand as AsyncCommand)?.RaiseCanExecuteChanged(); } }
    public IEnumerable<ProjectChapter> Chapters => Project.Chapters;
    public ZeroVideoSettings Settings { get => _settings; private set => Set(ref _settings, value); }

    public ICommand GeneratePlanCommand { get; }
    public ICommand RunPipelineCommand { get; }
    public ICommand OpenChatGptCommand { get; }
    public ICommand OpenClipchampCommand { get; }
    public ICommand InstallFfmpegCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand OpenProjectFolderCommand { get; }
    public ICommand LoadProjectCommand { get; }
    public ICommand CancelCommand { get; }

    public async Task InitializeAsync()
    {
        Settings = await _settingsService.LoadAsync();
        Log("ZeroVideo initialized.");
        Log("First run: use Automation > Open ChatGPT and Open Clipchamp to sign in once.");
        Raise(nameof(Settings));
    }

    private async Task GeneratePlanAsync()
    {
        try
        {
            Status = "Generating plan with ChatGPT..."; Progress = 5;
            var seed = new VideoProject { Topic = Topic.Trim(), Title = Topic.Trim(), TargetMinutes = Duration, AspectRatio = AspectRatio, Style = Style };
            var prompt = _promptBuilder.Build(seed);
            Project = await _chat.GeneratePlanAsync(seed, Settings, prompt, Log, _cts.Token);
            ProjectDirectory = await _store.SaveAsync(Project, Settings.ProjectsRoot);
            Raise(nameof(Chapters));
            Progress = 25; Status = $"Plan ready: {Project.Chapters.Count} chapters";
            Log($"Project saved to {ProjectDirectory}");
        }
        catch (Exception ex) { Fail(ex); }
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
        Raise(nameof(Chapters)); Status = "Project loaded"; Log($"Loaded {Project.Title}");
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
        Status = "Stopped: " + ex.Message; Log("ERROR: " + ex); MessageBox.Show(ex.Message, "ZeroVideo", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public async ValueTask DisposeAsync() { _cts.Cancel(); _cts.Dispose(); await _browser.DisposeAsync(); }

    private sealed class RelayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
