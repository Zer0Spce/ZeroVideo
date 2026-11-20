using System.Windows;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly LivePortraitSetupService _portraitSetup = new(new ProcessRunner());
    private string _livePortraitStatus =
        "LivePortrait uses ZeroVideo's existing ComfyUI, not a separate " +
        "FramePack or Wan server. Install nodes, restart ComfyUI, and test.";
    private bool _includeSharedWanTextEncoder;

    public string LivePortraitStatus
    {
        get => _livePortraitStatus;
        private set => Set(ref _livePortraitStatus, value);
    }

    public bool IncludeSharedWanTextEncoder
    {
        get => _includeSharedWanTextEncoder;
        set => Set(ref _includeSharedWanTextEncoder, value);
    }

    public ICommand InstallLivePortraitCommand { get; private set; } = null!;
    public ICommand StartLivePortraitComfyUiCommand { get; private set; } = null!;
    public ICommand TestLivePortraitCommand { get; private set; } = null!;
    public ICommand ReviewDeleteLegacyAnimationCommand { get; private set; } = null!;

    private void InitializeLivePortraitCommands()
    {
        InstallLivePortraitCommand = new AsyncCommand(() => RunManagedJobAsync(
            "Install LivePortrait", InstallLivePortraitAsync));
        StartLivePortraitComfyUiCommand = new AsyncCommand(() => RunManagedJobAsync(
            "Start ComfyUI for LivePortrait", StartLivePortraitComfyUiAsync));
        TestLivePortraitCommand = new AsyncCommand(() => RunManagedJobAsync(
            "Test LivePortrait Connection", TestLivePortraitAsync));
        ReviewDeleteLegacyAnimationCommand = new AsyncCommand(() => RunManagedJobAsync(
            "Review legacy animation files", ReviewDeleteLegacyAnimationAsync));
    }

    private async Task InstallLivePortraitAsync()
    {
        LivePortraitStatus =
            "Installing official LivePortrait nodes into the existing ComfyUI...";
        await _portraitSetup.InstallAsync(message =>
        {
            LivePortraitStatus = message;
            Log(message);
        }, _cts.Token);
        Settings.AiImageAnimationEngine = "LivePortrait (Local)";
        await _settingsService.SaveAsync(Settings);
        Raise(nameof(Settings));
        LivePortraitStatus =
            "LivePortrait nodes installed. Restart ComfyUI, then Test Connection. " +
            "The first actual face animation will download its required weights.";
        Log(LivePortraitStatus);
    }

    private async Task StartLivePortraitComfyUiAsync()
    {
        LivePortraitStatus = "Starting or safely restarting ZeroVideo ComfyUI at 127.0.0.1:8188...";
        LivePortraitStatus = await _localImages.RestartForLivePortraitAsync(
            Log, ConfirmComfyUiProcessRestart, _cts.Token);
        Log(LivePortraitStatus);
    }

    // Used by both the LivePortrait and Local Images restart buttons.
    // A stale process from an older ZeroVideo session is only stopped after
    // the running executable, startup arguments and port owner are verified.
    private static bool ConfirmComfyUiProcessRestart(string warning) =>
        Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(warning, "ZeroVideo — restart ComfyUI",
                MessageBoxButton.YesNo, MessageBoxImage.Warning)
                == MessageBoxResult.Yes);

    private async Task TestLivePortraitAsync()
    {
        var service = new LivePortraitService(_localImages);
        await service.TestConnectionAsync(_cts.Token);
        LivePortraitStatus = "LivePortrait nodes online and local ComfyUI connected. " +
            "Ready to test a generated image containing a clear visible face.";
        Status = LivePortraitStatus;
        Log(LivePortraitStatus);
    }

    private Task ReviewDeleteLegacyAnimationAsync()
    {
        var files = LegacyAnimationCleanupService.Inspect(
            IncludeSharedWanTextEncoder);
        if (files.Count == 0)
        {
            LivePortraitStatus =
                "No legacy Wan/FramePack assets were found in ZeroVideo-owned paths. " +
                "External Hugging Face caches and user-installed models are never touched.";
            Log(LivePortraitStatus);
            return Task.CompletedTask;
        }

        var lines = string.Join(Environment.NewLine + Environment.NewLine,
            files.Select(f => f.Name + " (" +
                (f.Bytes / 1073741824.0).ToString("0.00") + " GB)" +
                Environment.NewLine + f.FullPath +
                (f.SharedWithOtherWorkflows
                    ? Environment.NewLine +
                      "SHARED MODEL: other ComfyUI workflows may need this!" : "")));
        var totalGb = files.Sum(x => x.Bytes) / 1073741824.0;

        var warning =
            "REVIEW BEFORE CLEANUP\n\nOnly the following exact paths are " +
            "eligible. File and directory contents will be sent to the Windows " +
            "Recycle Bin, NOT permanently deleted.\n\n" +
            lines + "\n\nApproximate total: " + totalGb.ToString("0.00") +
            " GB.\n\nNo project media, FLUX/SDXL models, OmniVoice, or other " +
            "ComfyUI checkpoints will be touched.\n\n" +
            "External/global model caches are NOT included. " +
            "Stop FramePack and Wan/ComfyUI jobs before proceeding.\n\n" +
            "Send ONLY these displayed items to Recycle Bin?";
        if (MessageBox.Show(warning, "ZeroVideo — review old animation files",
                MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
        {
            LivePortraitStatus = "Legacy animation cleanup canceled. No files changed.";
            Log(LivePortraitStatus);
            return Task.CompletedTask;
        }

        var outcome = LegacyAnimationCleanupService.Recycle(files);
        LivePortraitStatus = "Legacy animation cleanup: " + outcome.Removed +
            " sent to Recycle Bin, " + outcome.Failed +
            " could not be removed. Details in log.";
        Log(LivePortraitStatus);
        Log(outcome.Report);
        return Task.CompletedTask;
    }
}
