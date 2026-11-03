using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Services;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.ViewModels;

public sealed partial class MainViewModel
{
    private string _localImageStatus =
        "Optional local engine. ChatGPT remains the default until you change the image engine setting.";
    private bool _localImageBusy;
    public string LocalImageStatus
    {
        get => _localImageStatus; private set => Set(ref _localImageStatus,value);
    }
    public bool LocalImageBusy
    {
        get => _localImageBusy; private set => Set(ref _localImageBusy,value);
    }
    public string LocalImageInstallFolder => LocalImageService.PortableRoot;
    public string LocalImageCheckpointFolder => Path.GetDirectoryName(LocalImageService.CheckpointPath)!;
    public string LocalImageAvailability =>
        (_localImages.HasComfyUi?"ComfyUI installed":"ComfyUI not installed")+
        " · "+(_localImages.HasCheckpoint?"SDXL downloaded":"SDXL model missing");
    public IReadOnlyList<string> ImageGenerationEngines { get; } =
        ["ChatGPT","Local ComfyUI","Hybrid"];
    public ICommand InstallLocalImagesCommand { get; private set; } = null!;
    public ICommand StartLocalImagesCommand { get; private set; } = null!;
    public ICommand TestLocalImagesCommand { get; private set; } = null!;
    public ICommand BenchmarkLocalStylesCommand { get; private set; } = null!;
    public ICommand OpenLocalImageFolderCommand { get; private set; } = null!;
    public ICommand OpenLocalBenchmarkFolderCommand { get; private set; } = null!;

    private void InitializeLocalImageCommands()
    {
        InstallLocalImagesCommand=new AsyncCommand(InstallLocalImagesAsync);
        StartLocalImagesCommand=new AsyncCommand(StartLocalImagesAsync);
        TestLocalImagesCommand=new AsyncCommand(TestLocalConnectionAsync);
        BenchmarkLocalStylesCommand=new AsyncCommand(BenchmarkLocalStylesAsync);
        OpenLocalImageFolderCommand=new RelayCommand(()=>
        {
            if(!Directory.Exists(LocalImageService.Root))
                Directory.CreateDirectory(LocalImageService.Root);
            Process.Start(new ProcessStartInfo("explorer.exe",
                LocalImageService.Root){UseShellExecute=true});
        });
        OpenLocalBenchmarkFolderCommand=new RelayCommand(()=>
        {
            var dir=Path.Combine(LocalImageService.Root,"Benchmarks");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe",dir){UseShellExecute=true});
        });
    }

    private void LocalProgress(string message)
    {
        // ComfyUI download callbacks may execute on thread-pool threads.
        Application.Current.Dispatcher.Invoke(()=>
        {
            LocalImageStatus=message;
            Raise(nameof(LocalImageAvailability));
            Log("Local images: "+message);
        });
    }

    private async Task InstallLocalImagesAsync()
    {
        if(LocalImageBusy)return;
        var consent=MessageBox.Show(
            "INSTALL LOCAL AI IMAGE GENERATION?\n\n"+
            "ZeroVideo will download the official ComfyUI NVIDIA Windows portable "+
            "package from GitHub and the Stability AI SDXL base checkpoint (~6.94 GB) "+
            "from Hugging Face. ComfyUI includes its Python and CUDA runtime. "+
            "The complete installation may need 18+ GB of free disk space and "+
            "a current NVIDIA driver. Downloaded files will stay in:\n\n"+
            LocalImageService.Root+"\n\n"+
            "A supported Windows tar.exe or existing 7-Zip is required for "+
            "the portable .7z archive. No passwords or API tokens are required. "+
            "Downloads are resumable; the model is SHA-256 verified. "+
            "This will not change your publishing, captions, project data or "+
            "existing ChatGPT image generation mode.\n\n"+
            "Continue with the installation?",
            "ZeroVideo — Install Local AI",MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if(consent!=MessageBoxResult.Yes)return;
        LocalImageBusy=true;
        try
        {
            await _localImages.InstallEverythingAsync(LocalProgress,_cts.Token);
            LocalImageStatus="Installation complete. Start ComfyUI, test the connection, "+
                "then benchmark sample styles before choosing Local ComfyUI or Hybrid.";
            Raise(nameof(LocalImageAvailability));
        }
        catch(OperationCanceledException)
        { LocalImageStatus="Installation canceled. Partial downloads are resumable."; }
        catch(Exception ex)
        {
            LocalImageStatus="Installation failed: "+ex.Message;
            Log("Local AI installer: "+ex);
        }
        finally { LocalImageBusy=false; }
    }

    private async Task StartLocalImagesAsync()
    {
        if(LocalImageBusy)return;
        LocalImageBusy=true;
        try
        {
            await _localImages.StartAsync(LocalProgress,_cts.Token);
            LocalImageStatus="ComfyUI is running at http://127.0.0.1:8188 "+
                "(localhost only). Existing ChatGPT image workflow remains available.";
        }
        catch(Exception ex) { LocalImageStatus="ComfyUI could not start: "+ex.Message; }
        finally { LocalImageBusy=false; }
    }

    private async Task TestLocalConnectionAsync()
    {
        try
        {
            var online=await _localImages.IsOnlineAsync(_cts.Token);
            LocalImageStatus=online?
                "ComfyUI responds at 127.0.0.1:8188. "+
                LocalImageAvailability:
                "ComfyUI is not responding. Install Everything and Start ComfyUI, "+
                "or run your own ComfyUI instance on 127.0.0.1:8188.";
        }
        catch(Exception ex) { LocalImageStatus="Connection test: "+ex.Message; }
    }

    private async Task BenchmarkLocalStylesAsync()
    {
        if(LocalImageBusy)return;
        if(!_localImages.HasCheckpoint)
        {
            LocalImageStatus="Install Everything first; SDXL model not found.";
            return;
        }
        LocalImageBusy=true;
        var folder=Path.Combine(LocalImageService.Root,"Benchmarks",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);
        var styles=new[]
        {
            "Cinematic Comic Explainer",
            "Comic Book Explainer",
            "Editorial Cartoon Explainer"
        };
        try
        {
            await _localImages.StartAsync(LocalProgress,_cts.Token);
            foreach(var (style,index) in styles.Select((value,i)=>(value,i)))
            {
                LocalImageStatus=$"Rendering benchmark {index+1}/{styles.Length}: {style}";
                var destination=Path.Combine(folder,
                    $"{index+1:00}-{style.Replace(" ","-")}.png");
                await _localImages.GenerateToPathAsync(
                    "A stormy mystery in an old town",
                    "A detective finds an old brass key beside a torn newspaper.",
                    style,"9:16","The hidden clue",
                    "At night, a detective picks up a mysterious brass key on a rain-soaked desk.",
                    "Medium shot of a detective's hand holding a brass key above an aged newspaper, "+
                    "dark vintage office and glowing desk lamp",
                    "Brass key, old newspaper, rainy window",
                    destination,Settings,LocalProgress,_cts.Token);
            }
            LocalImageStatus="Three style benchmarks saved to "+folder+
                ". Compare the results before enabling local generation for a full video.";
        }
        catch(OperationCanceledException)
        { LocalImageStatus="Benchmark canceled; completed sample images were kept."; }
        catch(Exception ex)
        { LocalImageStatus="Benchmark stopped: "+ex.Message+
            ". Completed images were kept in "+folder; }
        finally { LocalImageBusy=false; }
    }
}
