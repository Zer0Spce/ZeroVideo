using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    private string _testedOmniSource="";
    private string _testedOmniTranscript="";
    private readonly MediaPlayer _omniPreviewPlayer=new();

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
    // Browsing or testing a library voice must not silently switch
    // the narrator used by automated videos. Use Voice in Automation
    // is the explicit commit action.
    public OmniVoiceProfile? SelectedOmniProfile
    {
        get=>_selectedOmniProfile;
        set=>Set(ref _selectedOmniProfile,value);
    }

    public bool IsCustomVoiceActive =>
        Settings.TextToSpeechEngine == "OmniVoice (Local)";

    public string ActiveNarrationVoiceName =>
        IsCustomVoiceActive
            ? OmniVoiceProfiles.FirstOrDefault(p =>
                p.Id.Equals(Settings.OmniVoiceProfileId,
                    StringComparison.OrdinalIgnoreCase))?.Name ??
                "No active saved voice — select one in Voice Studio"
            : string.IsNullOrWhiteSpace(Project.Voice)
                ? "Clipchamp default"
                : Project.Voice;

    public ICommand InstallOmniVoiceCommand {get;private set;}=null!;
    public ICommand BrowseOmniReferenceCommand {get;private set;}=null!;
    public ICommand ImportOmniProfileCommand {get;private set;}=null!;
    public ICommand TestOmniVoiceCommand {get;private set;}=null!;
    public ICommand PreviewNewOmniVoiceCommand {get;private set;}=null!;
    public ICommand RemoveOmniProfileCommand {get;private set;}=null!;
    public ICommand UseOmniProfileCommand {get;private set;}=null!;
    public ICommand StopOmniPreviewCommand {get;private set;}=null!;
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
            _testedOmniSource="";
            LocalTtsStatus="Sample selected. Paste the exact transcript of the audio. "+
                "3–10 seconds of clean speech gives the best initial results.";
        });
        ImportOmniProfileCommand=new AsyncCommand(()=>
            RunManagedJobAsync("Import approved OmniVoice sample",ImportOmniVoiceProfileAsync));
        TestOmniVoiceCommand=new AsyncCommand(()=>
            RunManagedJobAsync("Test local OmniVoice TTS",TestOmniVoiceAsync));
        PreviewNewOmniVoiceCommand=new AsyncCommand(()=>
            RunManagedJobAsync("Preview new voice clone",PreviewNewOmniVoiceAsync));
        RemoveOmniProfileCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "Remove saved voice entry",RemoveSavedOmniProfileAsync));
        UseOmniProfileCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "Select OmniVoice profile",UseOmniProfileAsync));
        StopOmniPreviewCommand=new RelayCommand(()=>
        {
            _omniPreviewPlayer.Stop();
            LocalTtsStatus="Stopped audio preview.";
        });
        PlayOmniPreviewCommand=new RelayCommand(()=>
        {
            if(!File.Exists(OmniPreviewPath))
            {
                LocalTtsStatus="Generate a clone preview first.";
                return;
            }
            _omniPreviewPlayer.Stop();
            _omniPreviewPlayer.Open(new Uri(Path.GetFullPath(OmniPreviewPath)));
            _omniPreviewPlayer.Volume=0.85;
            _omniPreviewPlayer.Play();
            LocalTtsStatus="Playing voice preview inside ZeroVideo.";
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
        RefreshNarrationVoiceDisplay();
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
        if(_testedOmniSource!=OmniReferenceSource ||
           _testedOmniTranscript!=OmniReferenceTranscript)
        {
            LocalTtsStatus="Click Preview New Clone and listen to the voice "+
                "before saving it. If you change the sample or transcript, "+
                "test again.";
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
            // Save only the library entry here; activating a voice is an
            // explicit separate action (Use Voice in Automation).
            RefreshNarrationVoiceDisplay();
            await _settingsService.SaveAsync(Settings);
            LocalTtsStatus="Saved cloned voice '"+profile.Name+
                "' to the local Voice Library, and selected it for OmniVoice. "+
                "Choose Use in Automation to enable it for Shorts and Long Form. "+
                "Existing audio files are not overwritten.";
        }
        catch(OperationCanceledException)
        {LocalTtsStatus="Sample import canceled; no profile was saved.";}
        catch(Exception ex){LocalTtsStatus="Voice import failed: "+ex.Message;}
        finally{OmniTtsBusy=false;}
    }

    private async Task PreviewNewOmniVoiceAsync()
    {
        if(OmniTtsBusy)return;
        try{VerifyOmniVoiceEvaluation();}
        catch(Exception ex){LocalTtsStatus=ex.Message;return;}
        if(!OmniVoiceSampleAuthorized)
        {
            LocalTtsStatus="Confirm you own or have permission to clone the sample voice.";
            return;
        }
        if(string.IsNullOrWhiteSpace(OmniReferenceSource)||
           string.IsNullOrWhiteSpace(OmniReferenceTranscript))
        {
            LocalTtsStatus="Select a sample and enter the exact words spoken.";
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
            _testedOmniSource="";
            _testedOmniTranscript="";
            _omniPreviewPlayer.Stop();
            var output=await _omniVoice.PreviewNewSampleAsync(
                OmniReferenceSource,OmniReferenceTranscript,OmniTestText.Trim(),
                Settings,OmniProgress,_cts.Token);
            OmniPreviewPath=output;
            _testedOmniSource=OmniReferenceSource;
            _testedOmniTranscript=OmniReferenceTranscript;
            LocalTtsStatus="New clone preview ready. Play it, then "+
                "click Save Clone to keep the voice in your library. "+
                "This preview is for non-commercial evaluation only.";
        }
        catch(OperationCanceledException)
        {
            LocalTtsStatus="Voice preview stopped. Existing saved profiles remain intact.";
        }
        catch(Exception ex)
        {
            LocalTtsStatus="Voice preview failed: "+ex.Message;
            Log(LocalTtsStatus);
        }
        finally{OmniTtsBusy=false;}
    }

    private async Task UseOmniProfileAsync()
    {
        if(SelectedOmniProfile is null)
        {
            LocalTtsStatus="Select a saved voice in the Voice Library first.";
            return;
        }
        try{VerifyOmniVoiceEvaluation();}
        catch(Exception ex){LocalTtsStatus=ex.Message;return;}
        Settings.OmniVoiceProfileId=SelectedOmniProfile.Id;
        Settings.TextToSpeechEngine="OmniVoice (Local)";
        RefreshNarrationVoiceDisplay();
        await _settingsService.SaveAsync(Settings);
        Raise(nameof(Settings));
        LocalTtsStatus="Selected '"+SelectedOmniProfile.Name+
            "' for new Short Form and Long Form chapter narration. "+
            "This is NON-COMMERCIAL EVALUATION ONLY. Existing voiceovers "+
            "are reused and not replaced. Switch back to Clipchamp at any time.";
    }

    private async Task RemoveSavedOmniProfileAsync()
    {
        if(SelectedOmniProfile is null)
        {
            LocalTtsStatus="Select a saved voice to remove.";
            return;
        }
        if(MessageBox.Show(
            "Remove '"+SelectedOmniProfile.Name+"' from the saved Voice Library?\n\n"+
            "Only its library entry is removed. The original sample and "+
            "previously generated narration WAVs remain on disk.",
            "Remove saved voice",MessageBoxButton.YesNo,
            MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        var profile=SelectedOmniProfile;
        _omniVoice.RemoveProfileFromLibrary(profile.Id);
        OmniVoiceProfiles.Remove(profile);
        SelectedOmniProfile=OmniVoiceProfiles.FirstOrDefault();
        // Removing the active entry returns to the safe original engine
        // rather than silently assigning a different cloned voice.
        if (Settings.OmniVoiceProfileId.Equals(profile.Id,
            StringComparison.OrdinalIgnoreCase))
        {
            Settings.OmniVoiceProfileId="";
            Settings.TextToSpeechEngine="Clipchamp";
        }
        RefreshNarrationVoiceDisplay();
        LocalTtsStatus="Removed the voice profile from the library. "+
            "Existing reference and narration files were preserved.";
        await _settingsService.SaveAsync(Settings);
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
