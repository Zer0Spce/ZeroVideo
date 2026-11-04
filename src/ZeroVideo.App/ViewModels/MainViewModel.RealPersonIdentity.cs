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
    public ICommand AddRealPersonCommand { get; private set; } = null!;
    public ICommand ImportRealPersonPhotoCommand { get; private set; } = null!;
    public ICommand ClearRealPersonPhotosCommand { get; private set; } = null!;
    public ICommand RemoveRealPersonCommand { get; private set; } = null!;
    public ICommand SaveIdentityLockCommand { get; private set; } = null!;
    public ICommand TestIdentityAdapterCommand { get; private set; } = null!;
    public ICommand DownloadPuLidIdentityModelsCommand { get; private set; } = null!;

    private void InitializeRealPersonIdentityCommands()
    {
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
