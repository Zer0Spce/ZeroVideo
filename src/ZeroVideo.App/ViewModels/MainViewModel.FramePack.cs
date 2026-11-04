using System.Diagnostics;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly FramePackSetupService _framePackSetup = new(new ProcessRunner());
    private string _framePackSetupStatus =
        "Not installed by ZeroVideo. Install the official 1.81 GB Windows package; " +
        "first model loading downloads over 30 GB separately.";

    public string FramePackSetupStatus
    {
        get => _framePackSetupStatus;
        private set => Set(ref _framePackSetupStatus, value);
    }

    public string FramePackInstallDirectory => FramePackSetupService.InstallRoot;

    public ICommand InstallFramePackCommand { get; private set; } = null!;
    public ICommand StartFramePackCommand { get; private set; } = null!;
    public ICommand StopFramePackCommand { get; private set; } = null!;
    public ICommand OpenFramePackFolderCommand { get; private set; } = null!;

    private void InitializeFramePackCommands()
    {
        InstallFramePackCommand = new AsyncCommand(() =>
            RunManagedJobAsync("Install FramePack Windows", InstallFramePackAsync));
        StartFramePackCommand = new AsyncCommand(() =>
            RunManagedJobAsync("Start FramePack Server", StartFramePackAsync));
        StopFramePackCommand = new RelayCommand(() =>
        {
            FramePackSetupService.StopOwnedServer();
            FramePackSetupStatus =
                "Stopped ZeroVideo-owned FramePack server. Other Python processes were not affected.";
            Log(FramePackSetupStatus);
        });
        OpenFramePackFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(FramePackSetupService.InstallRoot);
            Process.Start(new ProcessStartInfo("explorer.exe",
                FramePackSetupService.InstallRoot) { UseShellExecute = true });
        });
    }

    private async Task InstallFramePackAsync()
    {
        FramePackSetupStatus =
            "Downloading and installing official FramePack. No model weights will be downloaded yet.";
        var install = await _framePackSetup.InstallAsync(
            Settings, message =>
            {
                FramePackSetupStatus = message;
                Log(message);
            }, _cts.Token);
        Settings.FramePackPythonPath = install.PythonExe;
        Settings.FramePackServerUrl = "http://127.0.0.1:7860";
        await _settingsService.SaveAsync(Settings);
        Raise(nameof(Settings));
        FramePackSetupStatus = "FramePack package installed and Python path saved. " +
            "Click Start FramePack to download/load model weights when ready.";
        Status = FramePackSetupStatus;
        Log(FramePackSetupStatus);
    }

    private Task StartFramePackAsync()
    {
        var install = FramePackSetupService.FindExisting(Settings)
            ?? throw new FileNotFoundException(
                "Official FramePack installation not detected. Click Install FramePack first.");
        Settings.FramePackPythonPath = install.PythonExe;
        Settings.FramePackServerUrl =
            string.IsNullOrWhiteSpace(Settings.FramePackServerUrl)
                ? "http://127.0.0.1:7860" : Settings.FramePackServerUrl;
        FramePackSetupStatus = _framePackSetup.StartServer(install,
            Settings.FramePackServerUrl, message => Log(message));
        Raise(nameof(Settings));
        Status = FramePackSetupStatus;
        Log(FramePackSetupStatus);
        return _settingsService.SaveAsync(Settings);
    }
}
