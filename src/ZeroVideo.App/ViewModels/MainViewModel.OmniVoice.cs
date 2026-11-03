using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Models;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed partial class MainViewModel
{
    private string _localTtsStatus =
        "Local OmniVoice is optional. Clipchamp remains the original/default TTS engine.";
    private string _omniProfileName="My Narrator";
    private string _omniReferenceSource="";
    private string _omniReferenceTranscript="";
    private string _omniTestText=
        "On a dark and stormy evening, a mysterious discovery changed everything.";
    private string _omniPreviewPath="";
    private OmniVoiceProfile? _selectedOmniProfile;
    private bool _omniVoiceSampleAuthorized;
    private bool _omniTtsBusy;

    public ObservableCollection<string> TextToSpeechEngines { get; } =
        ["Clipchamp","OmniVoice (Local)"];
    public ObservableCollection<OmniVoiceProfile> OmniVoiceProfiles { get; } = [];
    public string OmniVoiceAvailability =>
        _omniVoice.IsInstalled?"OmniVoice 0.2.1 installed in isolated Python environment":
        "OmniVoice not installed";
    public string LocalTtsStatus
    {
        get=>_localTtsStatus;private set=>Set(ref _localTtsStatus,value);
    }
    public string OmniProfileName
    {
        get=>_omniProfileName;set=>Set(ref _omniProfileName,value);
    }
    public string OmniReferenceSource
    {
        get=>_omniReferenceSource;set=>Set(ref _omniReferenceSource,value);
    }
    public string OmniReferenceTranscript
    {
        get=>_omniReferenceTranscript;set=>Set(ref _omniReferenceTranscript,value);
    }
    public string OmniTestText
    {
        get=>_omniTestText;set=>Set(ref _omniTestText,value);
    }
    public string OmniPreviewPath
    {
        get=>_omniPreviewPath;private set=>Set(ref _omniPreviewPath,value);
    }
    public bool OmniVoiceSampleAuthorized
    {
        get=>_omniVoiceSampleAuthorized;set=>Set(ref _omniVoiceSampleAuthorized,value);
    }
    public bool OmniTtsBusy
    {
        get=>_omniTtsBusy;private set=>Set(ref _omniTtsBusy,value);
    }
    public OmniVoiceProfile? SelectedOmniProfile
    {
        get=>_selectedOmniProfile;
        set
        {
            if(Set(ref _selectedOmniProfile,value))
            {
                Settings.OmniVoiceProfileId=value?.Id??"";
                Raise(nameof(Settings));
            }
        }
    }

    public ICommand InstallOmniVoiceCommand {get;private set;}=null!;
    public ICommand BrowseOmniReferenceCommand {get;private set;}=null!;
    public ICommand ImportOmniProfileCommand {get;private set;}=null!;
    public ICommand TestOmniVoiceCommand {get;private set;}=null!;
    public ICommand PlayOmniPreviewCommand {get;private set;}=null!;
    public ICommand OpenOmniFolderCommand {get;private set;}=null!;

    private void InitializeOmniVoiceCommands()
    {
        InstallOmniVoiceCommand=new AsyncCommand(()=>
            RunManagedJobAsync("Install OmniVoice",InstallOmniVoiceAsync));
        BrowseOmniReferenceCommand=new RelayCommand(()=>
        {
            var file=new OpenFileDialog
            {
                Title="Choose an authorized single-speaker voice sample",
                Filter="Audio|*.wav;*.mp3;*.m4a;*.flac;*.ogg|All files|*.*",
                CheckFileExists=true
            };
            if(file.ShowDialog()!=true)return;
            OmniReferenceSource=file.FileName;
            LocalTtsStatus="Sample selected. Paste the exact transcript of the audio. "+
                "3–10 seconds of clean speech gives the best initial results.";
        });
        ImportOmniProfileCommand=new AsyncCommand(()=>
            RunManagedJobAsync("Import approved OmniVoice sample",ImportOmniVoiceProfileAsync));
        TestOmniVoiceCommand=new AsyncCommand(()=>
            RunManagedJobAsync("Test local OmniVoice TTS",TestOmniVoiceAsync));
        PlayOmniPreviewCommand=new RelayCommand(()=>
        {
            if(File.Exists(OmniPreviewPath))
                Process.Start(new ProcessStartInfo(OmniPreviewPath){UseShellExecute=true});
            else LocalTtsStatus="No test preview exists yet.";
        });
        OpenOmniFolderCommand=new RelayCommand(()=>
        {
            Directory.CreateDirectory(OmniVoiceService.Root);
            Process.Start(new ProcessStartInfo("explorer.exe",
                OmniVoiceService.Root){UseShellExecute=true});
        });
    }

    private void LoadOmniVoiceProfiles()
    {
        OmniVoiceProfiles.Clear();
        foreach(var p in _omniVoice.LoadProfiles())OmniVoiceProfiles.Add(p);
        SelectedOmniProfile=OmniVoiceProfiles.FirstOrDefault(p=>
            p.Id==Settings.OmniVoiceProfileId);
        Raise(nameof(OmniVoiceAvailability));
    }

    private void OmniProgress(string value)
    {
        Application.Current.Dispatcher.Invoke(()=>
        {
            LocalTtsStatus=value;
            Log(value);
        });
    }

    private void VerifyOmniVoiceEvaluation()
    {
        if(!Settings.OmniVoiceNonCommercialAcknowledged)
            throw new InvalidOperationException(
                "The official OmniVoice pretrained model is CC BY-NC 4.0. "+
                "Confirm non-commercial evaluation terms before installing or cloning.");
    }

    private async Task InstallOmniVoiceAsync()
    {
        if(OmniTtsBusy)return;
        try {VerifyOmniVoiceEvaluation();}
        catch(Exception ex){LocalTtsStatus=ex.Message;return;}
        var answer=MessageBox.Show(
            "INSTALL OMNIVOICE FOR LOCAL NON-COMMERCIAL EVALUATION?\n\n"+
            "The OmniVoice pretrained model weights are CC BY-NC 4.0 and "+
            "CANNOT be used in monetized YouTube/TikTok videos without "+
            "separate permission from the rights holder.\n\n"+
            "ZeroVideo will download verified Astral uv, a separate managed "+
            "Python 3.11 environment, CUDA PyTorch, the OmniVoice Python "+
            "package, and official model files into LocalAppData. "+
            "Please allow roughly 15 GB of free space. "+
            "This does NOT alter ComfyUI, your Clipchamp configuration, "+
            "existing project files, or your system Python.\n\n"+
            "Voice cloning requires a sample you own or have permission to "+
            "clone. Only use the model for non-commercial evaluation.\n\n"+
            "Continue?",
            "ZeroVideo — OmniVoice License and Setup",
            MessageBoxButton.YesNo,MessageBoxImage.Warning);
        if(answer!=MessageBoxResult.Yes)return;
        OmniTtsBusy=true;
        try
        {
            await _omniVoice.InstallAsync(OmniProgress,_cts.Token);
            LocalTtsStatus="OmniVoice setup complete. Import a short authorized "+
                "sample with its exact transcript, then generate a test.";
            Raise(nameof(OmniVoiceAvailability));
        }
        catch(OperationCanceledException)
        {
            LocalTtsStatus="OmniVoice installation canceled; existing files remain unchanged.";
        }
        catch(Exception ex)
        {
            LocalTtsStatus="OmniVoice setup failed: "+ex.Message;
            Log(LocalTtsStatus);
        }
        finally{OmniTtsBusy=false;}
    }

    private async Task ImportOmniVoiceProfileAsync()
    {
        if(OmniTtsBusy)return;
        try {VerifyOmniVoiceEvaluation();}
        catch(Exception ex){LocalTtsStatus=ex.Message;return;}
        if(!OmniVoiceSampleAuthorized)
        {
            LocalTtsStatus="Confirm you own or have permission to clone this voice first.";
            return;
        }
        if(string.IsNullOrWhiteSpace(OmniReferenceSource) ||
           string.IsNullOrWhiteSpace(OmniReferenceTranscript))
        {
            LocalTtsStatus="Choose a reference file and enter the actual spoken "+
                "words of the reference audio.";
            return;
        }
        OmniTtsBusy=true;
        try
        {
            var profile=await _omniVoice.ImportProfileAsync(
                OmniProfileName,OmniReferenceSource,OmniReferenceTranscript,
                Settings,OmniProgress,_cts.Token);
            OmniVoiceProfiles.Add(profile);
            SelectedOmniProfile=profile;
            await _settingsService.SaveAsync(Settings);
            LocalTtsStatus="Voice sample imported and saved locally: "+profile.Name+
                ". Choose Generate Test Narration to hear the result.";
        }
        catch(OperationCanceledException)
        {LocalTtsStatus="Sample import canceled; no profile was saved.";}
        catch(Exception ex){LocalTtsStatus="Voice import failed: "+ex.Message;}
        finally{OmniTtsBusy=false;}
    }

    private async Task TestOmniVoiceAsync()
    {
        if(OmniTtsBusy)return;
        try {VerifyOmniVoiceEvaluation();}
        catch(Exception ex){LocalTtsStatus=ex.Message;return;}
        if(SelectedOmniProfile is null)
        {
            LocalTtsStatus="Select an authorized voice profile first.";
            return;
        }
        if(OmniTestText.Trim().Length<10)
        {
            LocalTtsStatus="Enter at least 10 characters of test narration.";
            return;
        }
        OmniTtsBusy=true;
        try
        {
            var folder=Path.Combine(OmniVoiceService.Root,"Previews");
            Directory.CreateDirectory(folder);
            var destination=Path.Combine(folder,
                "preview-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".wav");
            await _omniVoice.GenerateAsync(OmniTestText.Trim(),destination,
                SelectedOmniProfile,Settings.OmniVoiceSpeed,
                OmniProgress,_cts.Token);
            OmniPreviewPath=destination;
            LocalTtsStatus="Non-commercial OmniVoice evaluation preview saved to "+
                destination+". Click Play Preview to listen. "+
                "Clipchamp remains the default production engine until you "+
                "explicitly change your TTS selection.";
        }
        catch(OperationCanceledException)
        {LocalTtsStatus="OmniVoice test stopped. Existing files were kept.";}
        catch(Exception ex)
        {
            LocalTtsStatus="OmniVoice test failed: "+ex.Message;
            Log(LocalTtsStatus);
        }
        finally{OmniTtsBusy=false;}
    }
}
