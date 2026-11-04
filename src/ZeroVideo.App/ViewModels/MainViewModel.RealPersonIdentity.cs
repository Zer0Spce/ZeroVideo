using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Models;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed partial class MainViewModel
{
    private RealPersonIdentity? _selectedRealPerson;
    private string _newRealPersonName = "";
    private int _identityPortraitCount = 2;
    public int IdentityPortraitCount
    {
        get => _identityPortraitCount;
        set => Set(ref _identityPortraitCount, Math.Clamp(value, 1, 2));
    }
    public System.Collections.ObjectModel.ObservableCollection<int>
        IdentityPortraitCountOptions { get; } = [1,2];
    private string _identityLockStatus =
        "Off by default. Add a real person's name and 1–3 approved clear face photos, " +
        "then enable the project toggle. FLUX PuLID adapter must be installed separately.";

    public RealPersonIdentity? SelectedRealPerson
    {
        get => _selectedRealPerson;
        set => Set(ref _selectedRealPerson, value);
    }
    public string NewRealPersonName
    {
        get => _newRealPersonName;
        set => Set(ref _newRealPersonName, value);
    }
    public string IdentityLockStatus
    {
        get => _identityLockStatus;
        private set => Set(ref _identityLockStatus, value);
    }
    public ICommand GenerateChatGptIdentityGuidesCommand { get; private set; } = null!;
    public ICommand InstallPuLidNodesCommand { get; private set; } = null!;
    public ICommand AddRealPersonCommand { get; private set; } = null!;
    public ICommand ImportRealPersonPhotoCommand { get; private set; } = null!;
    public ICommand ClearRealPersonPhotosCommand { get; private set; } = null!;
    public ICommand RemoveRealPersonCommand { get; private set; } = null!;
    public ICommand SaveIdentityLockCommand { get; private set; } = null!;
    public ICommand TestIdentityAdapterCommand { get; private set; } = null!;
    public ICommand DownloadPuLidIdentityModelsCommand { get; private set; } = null!;

    private void InitializeRealPersonIdentityCommands()
    {
        GenerateChatGptIdentityGuidesCommand = new AsyncCommand(() =>
            RunManagedJobAsync("Auto-generate identity reference portraits",
                GenerateChatGptIdentityGuidesAsync));
        InstallPuLidNodesCommand = new AsyncCommand(() =>
            RunManagedJobAsync("One-click PuLID-FLUX setup",InstallPuLidNodesAsync));
        AddRealPersonCommand = new AsyncCommand(() => RunManagedJobAsync(
            "Add real-person identity", AddRealPersonAsync));
        ImportRealPersonPhotoCommand = new AsyncCommand(() => RunManagedJobAsync(
            "Import real-person face reference", ImportRealPersonPhotoAsync));
        ClearRealPersonPhotosCommand = new AsyncCommand(() => RunManagedJobAsync(
            "Clear real-person references", ClearRealPersonPhotosAsync));
        RemoveRealPersonCommand = new AsyncCommand(() => RunManagedJobAsync(
            "Remove real-person identity", RemoveRealPersonAsync));
        SaveIdentityLockCommand = new AsyncCommand(() => RunManagedJobAsync(
            "Save real-person identity profiles", SaveIdentityLockAsync));
        TestIdentityAdapterCommand = new AsyncCommand(() => RunManagedJobAsync(
            "Check local PuLID identity adapter", TestIdentityAdapterAsync));
        DownloadPuLidIdentityModelsCommand = new AsyncCommand(() =>
            RunManagedJobAsync("Download PuLID identity weights",
                DownloadIdentityAssetsAsync));
    }

    private string? DetectMainPersonFromProject()
    {
        // Conservative extraction: only clear "Firstname Lastname" title
        // prefixes. Never guess that a location/organization is a real person.
        var text = !string.IsNullOrWhiteSpace(Project.Title) &&
                   !Project.Title.StartsWith("Untitled ZeroVideo", StringComparison.OrdinalIgnoreCase)
            ? Project.Title : Project.Topic;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var prefix = System.Text.RegularExpressions.Regex.Split(
            text, @"\s+[—–-]\s+|:\s+|\s+\|\s+")[0].Trim();
        prefix=System.Text.RegularExpressions.Regex.Replace(
            prefix, @"\s+(story|documentary|biography)$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var matches = System.Text.RegularExpressions.Regex.Match(prefix,
            @"^(?<name>[A-Za-z][A-Za-z.'-]*\s+[A-Za-z][A-Za-z.'-]*(?:\s+[A-Za-z][A-Za-z.'-]*)?)\s*(?:story|documentary|biography)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!matches.Success) return null;
        var name=matches.Groups["name"].Value.Trim();
        var components=name.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(components.Length<2 || components.Length>3 ||
           new [] {"The","A","An","How","Why","Inside","Story","What","When","Ghost"}
                .Contains(components[0],StringComparer.OrdinalIgnoreCase))
            return null;
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo
            .ToTitleCase(name.ToLowerInvariant());
    }

    private async Task GenerateChatGptIdentityGuidesAsync()
    {
        Project.RealPeople ??= [];
        var person = SelectedRealPerson;
        if(person is null)
        {
            // If the person isn't selected, use the entered name or the
            // clearly named headline subject. Do not infer random names from
            // the narration or create a reference for an ambiguous title.
            var name = !string.IsNullOrWhiteSpace(NewRealPersonName)
                ? NewRealPersonName.Trim() : DetectMainPersonFromProject();
            if(string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException(
                    "Select an existing identity or type the real person's full name. " +
                    "For example: Zak Bagans.");
            person=Project.RealPeople.FirstOrDefault(p=>
                p.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            if(person is null)
            {
                person=new RealPersonIdentity { Name=name };
                Project.RealPeople.Add(person);
            }
            SelectedRealPerson=person;
            NewRealPersonName="";
        }
        person.ReferencePaths ??= [];
        person.AiApproximateReferencePaths ??= [];
        var count=Math.Min(Math.Clamp(IdentityPortraitCount,1,2),
            3-person.ReferencePaths.Count);
        if(count<1)
            throw new InvalidOperationException(
                "This identity has three references already. Use Clear Photos " +
                "or select a different person first.");

        if(string.IsNullOrWhiteSpace(Project.ProjectDirectory))
            await SaveIdentityProjectAsync();
        var text =
            "Generate "+count+" ChatGPT illustration(s) of "+person.Name+
            " using your project's current '"+Project.Style+"' style. " +
            "They are AI-created APPROXIMATIONS, not authenticated photographs " +
            "or guarantees of facial likeness. For documentaries requiring " +
            "historical accuracy, replace them with actual approved reference " +
            "photographs. They will be saved locally, attached automatically, " +
            "and used only if Identity Lock is turned on. Continue?";
        if (MessageBox.Show(text,"ZeroVideo — AI approximation notice",
                MessageBoxButton.YesNo, MessageBoxImage.Information)
            != MessageBoxResult.Yes)
        {
            IdentityLockStatus="Automatic identity reference generation canceled.";
            return;
        }

        var startVariant=person.AiApproximateReferencePaths.Count;
        for(var i=0;i<count;i++)
        {
            _cts.Token.ThrowIfCancellationRequested();
            var nextIndex=startVariant+1+i;
            IdentityLockStatus="ChatGPT generating "+person.Name+
                " portrait "+(i+1)+" of "+count+
                " with "+Project.Style+"...";
            Log(IdentityLockStatus);
            var saved=await _chat.GenerateIdentityReferenceAsync(
                Project,person.Name,Math.Clamp(nextIndex,1,3),
                Settings,Log,_cts.Token);
            // Only attach successfully captured, nonempty original images.
            if(!File.Exists(saved) || new FileInfo(saved).Length<1024)
                throw new InvalidDataException(
                    "ChatGPT returned an incomplete portrait; it was not attached.");
            person.ReferencePaths.Add(saved);
            person.AiApproximateReferencePaths.Add(saved);
            await SaveIdentityProjectAsync();
        }
        Project.Options.EnableRealPersonIdentityLock=true;
        await SaveIdentityProjectAsync();
        IdentityLockStatus=person.Name+": "+person.ReferencePaths.Count+
            " reference(s) now attached. "+count+
            " new AI-generated approximate portrait(s) saved; Identity Lock enabled. " +
            "For accurate likeness, replace with approved real photos. " +
            "Next: Install & Setup PuLID-FLUX and Test Adapter.";
        Log(IdentityLockStatus);
    }

    private async Task InstallPuLidNodesAsync()
    {
        if(MessageBox.Show(
            "Install the lldacing PuLID-FLUX ComfyUI extension and the two " +
            "official identity model files (~2 GB) into ZeroVideo's existing " +
            "ComfyUI installation? This also installs Python packages into " +
            "ComfyUI's bundled Python (not your system Python). It may change " +
            "ComfyUI package versions; close existing generation jobs first. " +
            "On an RTX 3060 8 GB the FLUX Schnell/PuLID combination is " +
            "experimental and may exceed GPU memory. Continue?",
            "ZeroVideo — Install PuLID-FLUX",
            MessageBoxButton.YesNo,MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        IdentityLockStatus="Installing PuLID nodes and verified model weights...";
        var installer = new PuLidSetupService(_runner,_localImages);
        await installer.InstallAndVerifyAsync(message =>
        {
            IdentityLockStatus=message;
            Log(message);
        },_cts.Token);
        IdentityLockStatus="PuLID files installed. Starting/restarting the " +
            "ZeroVideo-managed ComfyUI server to register new nodes...";
        Log(IdentityLockStatus);
        var serverStatus=await _localImages.RestartForLivePortraitAsync(
            Log,ConfirmComfyUiProcessRestart,_cts.Token);
        Log(serverStatus);
        await _localImages.TestPulidIdentityAdapterAsync(Log,_cts.Token);
        IdentityLockStatus="PuLID nodes are registered and model files exist. " +
            "You can test generating a recognizable named person using FLUX. " +
            "Identity quality and GPU memory on 8 GB are not guaranteed.";
        Log(IdentityLockStatus);
    }

    private async Task SaveIdentityProjectAsync()
    {
        Project.RealPeople ??= [];
        await _store.SaveAsync(Project, Settings.ProjectsRoot);
        Raise(nameof(Project));
    }

    private async Task AddRealPersonAsync()
    {
        var name = (NewRealPersonName ?? "").Trim();
        if (name.Length < 3 || name.Length > 90)
            throw new InvalidOperationException(
                "Enter the real person's full name (3–90 characters).");
        Project.RealPeople ??= [];
        if (Project.RealPeople.Any(x => x.Name.Equals(
            name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "This person already has a reference profile.");
        var identity = new RealPersonIdentity { Name = name };
        Project.RealPeople.Add(identity);
        SelectedRealPerson = identity;
        NewRealPersonName = "";
        await SaveIdentityProjectAsync();
        IdentityLockStatus = "Added " + name +
            ". Import 1–3 clear face photos, then enable Real Person Identity Lock.";
        Log(IdentityLockStatus);
    }

    private async Task ImportRealPersonPhotoAsync()
    {
        if (SelectedRealPerson is not { } identity)
            throw new InvalidOperationException(
                "Select a real person in the identity list first.");
        identity.ReferencePaths ??= [];
        if (identity.ReferencePaths.Count >= 3)
            throw new InvalidOperationException(
                "This identity already has three references. Clear references " +
                "before replacing them.");

        var dialog = new OpenFileDialog
        {
            Title = "Choose a clear, approved face photo of " + identity.Name,
            Filter = "Face photos|*.png;*.jpg;*.jpeg",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        var info = new FileInfo(dialog.FileName);
        if (info.Length < 1024 || info.Length > 25_000_000)
            throw new InvalidDataException(
                "The face photo must be between 1 KB and 25 MB.");

        // Decode image locally and reject unreadable/very small portraits.
        try
        {
            using var check = File.OpenRead(dialog.FileName);
            var bitmap = BitmapDecoder.Create(check,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (bitmap.Frames.Count == 0 ||
                bitmap.Frames[0].PixelWidth < 128 ||
                bitmap.Frames[0].PixelHeight < 128)
                throw new InvalidDataException(
                    "Choose a readable photo at least 128×128 pixels.");
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidDataException(
                "ZeroVideo cannot read this PNG/JPEG. Choose another photo.", ex);
        }

        if (string.IsNullOrWhiteSpace(Project.ProjectDirectory))
            await SaveIdentityProjectAsync();

        var folder = RealPersonIdentityService.ReferenceFolder(Project);
        Directory.CreateDirectory(folder);
        var name = System.Text.RegularExpressions.Regex.Replace(
            identity.Name, "[^A-Za-z0-9_-]", "_");
        var target = Path.Combine(folder, "approved-" + name + "-" +
            Guid.NewGuid().ToString("N")[..10] +
            Path.GetExtension(dialog.FileName).ToLowerInvariant());
        File.Copy(dialog.FileName, target, overwrite: false);
        identity.ReferencePaths.Add(target);
        await SaveIdentityProjectAsync();
        IdentityLockStatus = identity.Name + ": imported " +
            identity.ReferencePaths.Count + " of 3 reference photos. " +
            "Reference is local and never uploaded to ChatGPT or a third-party image search.";
        Log(IdentityLockStatus);
    }

    private async Task ClearRealPersonPhotosAsync()
    {
        if (SelectedRealPerson is not { } person) return;
        // Detach references without deleting files used by saved projects.
        person.ReferencePaths.Clear();
        person.AiApproximateReferencePaths?.Clear();
        await SaveIdentityProjectAsync();
        IdentityLockStatus = "References detached from " + person.Name +
            ". Previously copied files were not deleted.";
        Log(IdentityLockStatus);
    }

    private async Task RemoveRealPersonAsync()
    {
        if (SelectedRealPerson is not { } person) return;
        Project.RealPeople.Remove(person);
        SelectedRealPerson = null;
        await SaveIdentityProjectAsync();
        IdentityLockStatus = "Removed identity profile for " + person.Name +
            ". No source photos or generated images were deleted.";
        Log(IdentityLockStatus);
    }

    private async Task SaveIdentityLockAsync()
    {
        Project.Options.RealPersonIdentityStrength =
            Math.Clamp(Project.Options.RealPersonIdentityStrength, 0.35, 1.1);
        await SaveIdentityProjectAsync();
        IdentityLockStatus = "Identity Lock " +
            (Project.Options.EnableRealPersonIdentityLock ? "ENABLED" : "DISABLED") +
            " for this project. Only scenes visibly depicting the approved named " +
            "person use identity conditioning; all other scenes remain independent.";
        Log(IdentityLockStatus);
    }

    private async Task DownloadIdentityAssetsAsync()
    {
        IdentityLockStatus = "Downloading verified PuLID/FaceNet identity weight files...";
        await _localImages.InstallPulidIdentityAssetsAsync(message =>
        {
            IdentityLockStatus = message;
            Log(message);
        }, _cts.Token);
        IdentityLockStatus = "PuLID and EVA-CLIP identity models verified. " +
            "Install the lldacing ComfyUI_PuLID_Flux_ll extension with its Python " +
            "dependencies through ComfyUI Manager, restart ComfyUI, then click Test.";
        Log(IdentityLockStatus);
    }

    private async Task TestIdentityAdapterAsync()
    {
        await _localImages.TestPulidIdentityAdapterAsync(Log, _cts.Token);
        IdentityLockStatus = "Local PuLID-FLUX nodes and model weights detected. " +
            "On an RTX 3060 8 GB, actual synthesis may still exceed VRAM.";
        Log(IdentityLockStatus);
    }
}
