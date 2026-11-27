using System.Windows.Input;
using Microsoft.Win32;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Models;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed partial class MainViewModel
{
    private ProjectCharacter? _selectedCharacter;
    private string _newCharacterName="";
    private string _newCharacterAppearance="";
    private string _characterStatus="Character Bible is OFF by default. Use it only for visible recurring people; buildings and vehicles never need identity locking.";
    public string NewCharacterName
    {
        get=>_newCharacterName;set=>Set(ref _newCharacterName,value);
    }
    public string NewCharacterAppearance
    {
        get=>_newCharacterAppearance;set=>Set(ref _newCharacterAppearance,value);
    }
    public ProjectCharacter? SelectedCharacter
    {
        get=>_selectedCharacter;set=>Set(ref _selectedCharacter,value);
    }
    public string CharacterStatus
    {
        get=>_characterStatus;private set=>Set(ref _characterStatus,value);
    }
    public ICommand DetectCharactersCommand {get;private set;}=null!;
    public ICommand AddCharacterCommand {get;private set;}=null!;
    public ICommand SaveCharactersCommand {get;private set;}=null!;
    public ICommand ImportCharacterImageCommand {get;private set;}=null!;
    public ICommand DeleteCharacterCommand {get;private set;}=null!;

    private void InitializeCharacterCommands()
    {
        DetectCharactersCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "Detect recurring characters",DetectCharactersAsync));
        AddCharacterCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "Add character profile",AddCharacterAsync));
        SaveCharactersCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "Save character profiles",SaveCharactersAsync));
        ImportCharacterImageCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "Import character reference",ImportCharacterImageAsync));
        DeleteCharacterCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "Delete character profile",DeleteCharacterAsync));
    }

    private async Task DetectCharactersAsync()
    {
        if(Project.Chapters.Count==0)
        {
            CharacterStatus="Generate or load a script before detecting recurring characters.";
            return;
        }
        if(!Project.Options.EnableCharacterConsistency)
        {
            CharacterStatus="Character Bible is OFF. Enable it above if your video has visible recurring people; leave it OFF for scenery or vehicles.";
            return;
        }
        var before=Project.Characters?.Count??0;
        CharacterConsistencyService.EnsureProfiles(Project);
        await _store.SaveAsync(Project,Settings.ProjectsRoot);
        Raise(nameof(Project));
        CharacterStatus=$"Character Bible has {Project.Characters.Count} profile(s). "+
            $"{Project.Characters.Count-before} visibly recurring person(s) detected. "+
            "Only identity descriptions and deliberately imported portraits are reused, "+
            "never automatic copies of earlier complete scenes.";
    }

    private async Task AddCharacterAsync()
    {
        if(Project.Chapters.Count==0)
        {
            CharacterStatus="Generate or load a script before adding characters.";
            return;
        }
        var name=NewCharacterName.Trim();
        if(name.Length<2||name.Length>70)
        {
            CharacterStatus="Enter a character name of 2–70 characters.";
            return;
        }
        Project.Characters??=[];
        if(Project.Characters.Any(c=>c.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))
        {
            CharacterStatus="This character already exists in the project.";
            return;
        }
        var character=new ProjectCharacter
        {
            Name=name,
            Appearance=NewCharacterAppearance.Trim(),
            Seed=CharacterConsistencyService.StableSeed(Project.Id+"|"+name),
            Enabled=true
        };
        Project.Characters.Add(character);
        SelectedCharacter=character;
        await _store.SaveAsync(Project,Settings.ProjectsRoot);
        Raise(nameof(Project));
        CharacterStatus="Added "+name+". This profile is only used when a person "+
            "is visibly depicted in a scene. You can optionally import a cropped "+
            "character portrait; no previous full-scene images will be reused.";
    }

    private async Task ImportCharacterImageAsync()
    {
        if(SelectedCharacter is not { } profile)
        {
            CharacterStatus="Select a character profile first.";
            return;
        }
        if(string.IsNullOrWhiteSpace(Project.ProjectDirectory))
        {
            CharacterStatus="Save your project before importing a reference image.";
            return;
        }
        var picker=new OpenFileDialog
        {
            Title="Choose an approved character reference",
            Filter="Image files|*.png;*.jpg;*.jpeg;*.webp",
            CheckFileExists=true
        };
        if(picker.ShowDialog()!=true)return;
        var info=new FileInfo(picker.FileName);
        if(info.Length>25*1024*1024||info.Length<512)
        {
            CharacterStatus="Reference image must be between 512 bytes and 25 MB.";
            return;
        }
        var folder=CharacterConsistencyService.AnchorDirectory(Project);
        Directory.CreateDirectory(folder);
        var slug=System.Text.RegularExpressions.Regex.Replace(profile.Name,@"[^A-Za-z0-9_-]","_");
        var target=Path.Combine(folder,"user-"+slug+"-"+Guid.NewGuid().ToString("N")[..8]+
            Path.GetExtension(picker.FileName).ToLowerInvariant());
        File.Copy(picker.FileName,target);
        profile.ReferenceImagePath=target;
        await _store.SaveAsync(Project,Settings.ProjectsRoot);
        Raise(nameof(Project));
        CharacterStatus="Reference imported locally for "+profile.Name+
            ". ZeroVideo will guide the person's identity while generating new "+
            "backgrounds and camera angles. A cropped character-only photo works best.";
    }

    private async Task DeleteCharacterAsync()
    {
        if(SelectedCharacter is not { } profile)return;
        var name=profile.Name;
        Project.Characters.Remove(profile);
        SelectedCharacter=null;
        // Do not delete any reference image: it may be used by other projects.
        await _store.SaveAsync(Project,Settings.ProjectsRoot);
        Raise(nameof(Project));
        CharacterStatus="Removed profile "+name+"; all image files remain untouched.";
    }

    private async Task SaveCharactersAsync()
    {
        if(Project.Chapters.Count==0)
        {
            CharacterStatus="Load or generate a project first.";
            return;
        }
        foreach(var c in Project.Characters??[])
        {
            if(c.Seed<=0)c.Seed=CharacterConsistencyService.StableSeed(Project.Id+"|"+c.Name);
        }
        await _store.SaveAsync(Project,Settings.ProjectsRoot);
        Raise(nameof(Project));
        CharacterStatus="Character Bible "+(Project.Options.EnableCharacterConsistency?
            "ON":"OFF")+" and saved for this project. Local images now use "+
            "independent scene seeds; only approved character portraits may "+
            "guide recurring people's identity. Already saved PNGs are unchanged.";
    }
}
