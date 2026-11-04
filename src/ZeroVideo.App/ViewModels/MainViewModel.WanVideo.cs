using System.Windows.Input;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed partial class MainViewModel
{
    private string _wanVideoStatus = "Wan2.2 TI2V 5B uses the existing local " +
        "ComfyUI engine. Install only three required models (~17 GB). " +
        "Optimized for 8 GB VRAM with offloading.";
    public string WanVideoStatus
    {
        get => _wanVideoStatus;
        private set => Set(ref _wanVideoStatus, value);
    }
    public ICommand InstallWanVideoCommand { get; private set; } = null!;
    public ICommand StartWanVideoCommand { get; private set; } = null!;
    public ICommand TestWanVideoCommand { get; private set; } = null!;

    private void InitializeWanVideoCommands()
    {
        InstallWanVideoCommand = new AsyncCommand(() =>
            RunManagedJobAsync("Install Wan2.2 Video Models", InstallWanVideoAsync));
        StartWanVideoCommand = new AsyncCommand(() =>
            RunManagedJobAsync("Start local ComfyUI for Wan video", StartWanVideoAsync));
        TestWanVideoCommand = new AsyncCommand(() =>
            RunManagedJobAsync("Test ComfyUI Video Connection", TestWanVideoAsync));
    }

    private async Task InstallWanVideoAsync()
    {
        WanVideoStatus = "Downloading Wan2.2 video model dependencies (~17 GB total).";
        await _localImages.InstallWanVideoModelsAsync(message =>
        {
            WanVideoStatus = message;
            Log(message);
        }, _cts.Token);
        Settings.AiImageAnimationEngine = "Wan2.2 5B (ComfyUI)";
        await _settingsService.SaveAsync(Settings);
        Raise(nameof(Settings));
        WanVideoStatus = "Wan2.2 model files verified. Start or restart " +
            "ComfyUI, then click Test ComfyUI Video Connection.";
        Log(WanVideoStatus);
    }

    private async Task StartWanVideoAsync()
    {
        WanVideoStatus = "Starting existing ComfyUI installation on 127.0.0.1:8188...";
        if (await _localImages.IsOnlineAsync(_cts.Token))
        {
            // A running instance might have started BEFORE the video models
            // were downloaded. Restart only a server ZeroVideo owns.
            await _localImages.RestartOwnedServerAsync(Log, _cts.Token);
        }
        else
            await _localImages.StartAsync(Log, _cts.Token);
        WanVideoStatus = "ComfyUI online. Test video nodes and model selection.";
        Log(WanVideoStatus);
    }

    private async Task TestWanVideoAsync()
    {
        if (!_localImages.HasWanVideoModels)
            throw new FileNotFoundException(
                "Wan2.2 models are not installed. Download the three required files first.");
        if (!await _localImages.IsOnlineAsync(_cts.Token))
            throw new IOException(
                "ComfyUI is offline. Click Start ComfyUI for Video first.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        foreach (var node in new[] { "Wan22ImageToVideoLatent", "CreateVideo", "SaveVideo" })
        {
            using var response = await http.GetAsync(
                "http://127.0.0.1:8188/object_info/" + node, _cts.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    "ComfyUI is missing " + node + ". Update the ComfyUI portable installation.");
            var body = await response.Content.ReadAsStringAsync(_cts.Token);
            using var parsed = System.Text.Json.JsonDocument.Parse(body);
            if (!parsed.RootElement.TryGetProperty(node, out _))
                throw new InvalidOperationException(
                    "ComfyUI is running, but the " + node + " node is not registered. " +
                    "Update ComfyUI and restart it.");
        }
        WanVideoStatus = "Wan2.2: ComfyUI video nodes online, model files present. " +
            "Ready for a local GPU animation test.";
        Log(WanVideoStatus);
        Status = WanVideoStatus;
    }
}
