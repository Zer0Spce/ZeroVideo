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
        " · "+(_localImages.HasCheckpoint?"SDXL downloaded":"SDXL model missing")+
        " · "+(_localImages.HasIllustratedCheckpoint?
            "DreamShaper XL installed":"DreamShaper XL not installed")+
        " · "+(_localImages.HasFluxSchnell?
            "FLUX schnell Q4 installed":"FLUX schnell Q4 not installed");
    public IReadOnlyList<string> LocalImageModelChoices { get; } =
        [LocalImageService.SdxlFileName, LocalImageService.IllustratedFileName,
         LocalImageService.FluxGgufFileName];
    public IReadOnlyList<string> ImageGenerationEngines { get; } =
        ["ChatGPT","Local ComfyUI","Hybrid"];
    public ICommand InstallLocalImagesCommand { get; private set; } = null!;
    public ICommand InstallIllustratedModelCommand { get; private set; } = null!;
    public ICommand InstallFluxSchnellCommand { get; private set; } = null!;
    public ICommand RestartLocalEngineCommand { get; private set; } = null!;

    public ICommand UseBaseModelCommand { get; private set; } = null!;
    public ICommand StartLocalImagesCommand { get; private set; } = null!;
    public ICommand TestLocalImagesCommand { get; private set; } = null!;
    public ICommand BenchmarkLocalStylesCommand { get; private set; } = null!;
    public ICommand OpenLocalImageFolderCommand { get; private set; } = null!;
    public ICommand OpenLocalBenchmarkFolderCommand { get; private set; } = null!;

    private void InitializeLocalImageCommands()
    {
        InstallLocalImagesCommand=new AsyncCommand(()=>RunManagedJobAsync("Local ComfyUI installation",InstallLocalImagesAsync));
        InstallIllustratedModelCommand=new AsyncCommand(()=>RunManagedJobAsync("Illustrated model installation",InstallIllustratedModelAsync));
        InstallFluxSchnellCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "FLUX schnell Q4 setup",InstallFluxSchnellAsync));
        RestartLocalEngineCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "Restart ComfyUI",async ()=>
        {
            await _localImages.RestartOwnedServerAsync(LocalProgress,_cts.Token);
            LocalImageStatus="Engine restart requested. Test connection before generation.";
        }));

        UseBaseModelCommand=new RelayCommand(() =>
        {
            Settings.LocalImageCheckpointFile=LocalImageService.SdxlFileName;
            Raise(nameof(Settings));
            LocalImageStatus="SDXL base selected; save your model choice before rendering.";
        });
        StartLocalImagesCommand=new AsyncCommand(()=>RunManagedJobAsync("Start local engine",StartLocalImagesAsync));
        TestLocalImagesCommand=new AsyncCommand(()=>RunManagedJobAsync("Local image connection check",TestLocalConnectionAsync));
        BenchmarkLocalStylesCommand=new AsyncCommand(()=>RunManagedJobAsync("Local style benchmarks",BenchmarkLocalStylesAsync));
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

    private async Task InstallIllustratedModelAsync()
    {
        if(LocalImageBusy)return;
        var answer=MessageBox.Show(
            "INSTALL AN OPTIONAL ILLUSTRATED CHECKPOINT?\n\n"+
            "ZeroVideo will download DreamShaper XL (approximately 6.94 GB) "+
            "from the creator's Hugging Face model repository and verify its "+
            "published SHA-256. It can produce stronger artistic/cartoon "+
            "results than generic SDXL base, but is not guaranteed to recreate "+
            "ChatGPT's visual quality or accurate classic-comic panels. "+
            "Review the model creator's license and usage terms before "+
            "commercial publishing:\n\n"+
            "https://huggingface.co/Lykon/DreamShaper\n\n"+
            "ComfyUI will be installed first if missing. No existing models, "+
            "projects, PNGs or MP4s are deleted. If installation succeeds "+
            "ZeroVideo selects DreamShaper XL for future local images only. "+
            "You can switch back to SDXL base or ChatGPT at any time.\n\n"+
            "Continue?",
            "ZeroVideo — Install Illustrated Model",
            MessageBoxButton.YesNo,MessageBoxImage.Question);
        if(answer!=MessageBoxResult.Yes)return;
        LocalImageBusy=true;
        try
        {
            if(!_localImages.HasComfyUi)
                await _localImages.InstallEverythingAsync(LocalProgress,_cts.Token);
            await _localImages.InstallIllustratedCheckpointAsync(
                LocalProgress,_cts.Token);
            Settings.LocalImageCheckpointFile=LocalImageService.IllustratedFileName;
            await _settingsService.SaveAsync(Settings);
            Raise(nameof(Settings));
            Raise(nameof(LocalImageAvailability));
            LocalImageStatus="DreamShaper XL installed, SHA-256 verified and selected. "+
                "Test the same three styles again to evaluate their actual quality.";
        }
        catch(OperationCanceledException)
        { LocalImageStatus="Illustrated model download canceled. Partial data is resumable."; }
        catch(Exception ex)
        {
            LocalImageStatus="Illustrated model setup failed: "+ex.Message;
            Log("Local illustrated model: "+ex);
        }
        finally {LocalImageBusy=false;}
    }

    private async Task InstallFluxSchnellAsync()
    {
        if(LocalImageBusy)return;
        var consent=MessageBox.Show(
            "INSTALL FLUX.1-SCHNELL Q4 (OPTIONAL)?\n\n"+
            "This is an Apache-2.0-licensed text-to-image model, quantized by "+
            "city96 for 8 GB GPUs. Installing it downloads approximately "+
            "12 GB of model assets (Q4 GGUF, T5 text encoder, CLIP and VAE), "+
            "plus a pinned ComfyUI-GGUF node extension and its Python dependency. "+
            "You need roughly 14 GB free disk space in the ZeroVideo LocalAI folder.\n\n"+
            "On an RTX 3060 8GB, it may offload model weights to system RAM and "+
            "can be much slower than DreamShaper XL. "+
            "It does not replace, remove or overwrite SDXL, DreamShaper, "+
            "ChatGPT, video output, TikTok settings or existing files.\n\n"+
            "All model assets have published SHA-256 hashes and will be "+
            "verified after download. You may need to restart ComfyUI after "+
            "installing the GGUF extension.\n\n"+
            "Approve download and optional ComfyUI plugin installation?",
            "ZeroVideo — Optional FLUX.1-schnell",
            MessageBoxButton.YesNo,MessageBoxImage.Question);
        if(consent!=MessageBoxResult.Yes)return;
        LocalImageBusy=true;
        try
        {
            await _localImages.InstallFluxSchnellAsync(LocalProgress,_cts.Token);
            Settings.LocalImageCheckpointFile=LocalImageService.FluxGgufFileName;
            await _settingsService.SaveAsync(Settings);
            Raise(nameof(Settings));
            Raise(nameof(LocalImageAvailability));
            await _localImages.RestartOwnedServerAsync(LocalProgress,_cts.Token);
            LocalImageStatus="FLUX.1-schnell Q4 verified and selected. "+
                "Restart any external ComfyUI server if necessary, "+
                "then run a style benchmark before generating full videos.";
        }
        catch(OperationCanceledException)
        {
            LocalImageStatus="FLUX setup canceled. Finished downloads are retained; "+
                "partial model downloads can resume.";
        }
        catch(Exception ex)
        {
            LocalImageStatus="FLUX setup failed: "+ex.Message;
            Log("FLUX installer: "+ex);
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
        var selected=string.IsNullOrWhiteSpace(Settings.LocalImageCheckpointFile)?
            LocalImageService.SdxlFileName:Settings.LocalImageCheckpointFile;
        var checkpointPath=selected==LocalImageService.FluxGgufFileName?
            LocalImageService.FluxModelPath:
            Path.Combine(LocalImageService.PortableRoot,"ComfyUI","models",
                "checkpoints",Path.GetFileName(selected));
        if(!File.Exists(checkpointPath))
        {
            LocalImageStatus="Install the selected local model before benchmarking: "+selected;
            return;
        }
        LocalImageBusy=true;
        var folder=Path.Combine(LocalImageService.Root,"Benchmarks",
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                (selected==LocalImageService.IllustratedFileName?"DreamShaperXL":
                 selected==LocalImageService.FluxGgufFileName?"FluxSchnellQ4":"SDXLBase"));
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
                    destination,Settings,LocalProgress,_cts.Token,
                    fixedSeed:45128901);
            }
            LocalImageStatus="Three style benchmarks generated with "+
                Path.GetFileName(selected)+" using a repeatable fixed seed. Saved to "+
                folder+". Compare all three before enabling full-video local generation.";
        }
        catch(OperationCanceledException)
        { LocalImageStatus="Benchmark canceled; completed sample images were kept."; }
        catch(Exception ex)
        { LocalImageStatus="Benchmark stopped: "+ex.Message+
            ". Completed images were kept in "+folder; }
        finally { LocalImageBusy=false; }
    }
}
