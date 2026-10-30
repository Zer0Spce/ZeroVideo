using System.Collections.ObjectModel;
using System.Text.Json;
using System.ComponentModel;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Models;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed class ShortQueueEntry : ObservableObject
{
    private string _idea = "";
    private string _status = "Pending";
    private string _projectFile = "";
    public string Idea { get => _idea; set => Set(ref _idea, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string ProjectFile { get => _projectFile; set => Set(ref _projectFile, value); }
}

public sealed partial class MainViewModel
{
    private int _shortDuration = 1;
    private bool _shortsRunning;
    public ObservableCollection<int> ShortDurations { get; } = [1, 2, 3, 4, 5, 10];
    public ObservableCollection<ShortQueueEntry> ShortQueue { get; } = [new()];
    public int ShortDuration { get => _shortDuration; set => Set(ref _shortDuration, value); }
    public bool ShortsRunning { get => _shortsRunning; private set => Set(ref _shortsRunning, value); }
    public ICommand AddShortRowCommand { get; private set; } = null!;
    public ICommand RemoveShortRowCommand { get; private set; } = null!;
    public ICommand StartShortsCommand { get; private set; } = null!;
    public ICommand ResumeShortsCommand { get; private set; } = null!;
    private static readonly string ShortsCheckpointFile =
        Path.Combine(AppPaths.DataDirectory,"shorts-recovery.json");
    private bool _loadingShortsCheckpoint;
    private string _shortsRecoveryStatus = "Automation recovery: a checkpoint is saved after each stage.";
    public string ShortsRecoveryStatus
    {
        get => _shortsRecoveryStatus; private set => Set(ref _shortsRecoveryStatus,value);
    }


    private void InitializeShortsCommands()
    {
        AddShortRowCommand = new RelayCommand(() => ShortQueue.Add(new ShortQueueEntry()));
        RemoveShortRowCommand = new RelayCommand(() =>
        {
            if (!ShortsRunning && ShortQueue.Count > 1) ShortQueue.RemoveAt(ShortQueue.Count - 1);
        });
        StartShortsCommand = new AsyncCommand(RunShortsQueueAsync, () => !ShortsRunning);
        ResumeShortsCommand = new AsyncCommand(RunShortsQueueAsync, () => !ShortsRunning);
        ShortQueue.CollectionChanged += (_,args) =>
        {
            if(args.NewItems is not null)
                foreach(ShortQueueEntry entry in args.NewItems)
                    entry.PropertyChanged += OnShortRecoveryEntryChanged;
            SaveShortsRecovery();
        };
        foreach(var entry in ShortQueue)
            entry.PropertyChanged += OnShortRecoveryEntryChanged;
    }

    private void OnShortRecoveryEntryChanged(object? sender, PropertyChangedEventArgs e)
        => SaveShortsRecovery();

    private void SaveShortsRecovery()
    {
        if(_loadingShortsCheckpoint)return;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var temp=ShortsCheckpointFile+".tmp";
            File.WriteAllText(temp,JsonSerializer.Serialize(ShortQueue.ToList(),
                new JsonSerializerOptions{WriteIndented=true}));
            File.Move(temp,ShortsCheckpointFile,true);
        }
        catch(Exception ex) { Log("Automation recovery save failed: "+ex.Message); }
    }

    private void LoadShortsRecovery()
    {
        if(!File.Exists(ShortsCheckpointFile))return;
        try
        {
            var restored=JsonSerializer.Deserialize<List<ShortQueueEntry>>(
                File.ReadAllText(ShortsCheckpointFile))??[];
            if(restored.Count==0)return;
            _loadingShortsCheckpoint=true;
            try
            {
                ShortQueue.Clear();
                foreach(var row in restored.Take(500))
                {
                    // On startup, a stage left interrupted is resumable.
                    // Never mark it completed unless previously finished.
                    if(row.Status is "Writing" or "Rendering" or "Thumbnail")
                        row.Status="Interrupted (" + row.Status + ") - ready to resume";
                    ShortQueue.Add(row);
                }
                if(ShortQueue.Count==0)ShortQueue.Add(new ShortQueueEntry());
            }
            finally { _loadingShortsCheckpoint=false; }
            SaveShortsRecovery();
            ShortsRecoveryStatus=$"Recovered {ShortQueue.Count} automation row(s) from disk. " +
                "Press Resume Unfinished to reuse saved scripts, visuals and chapter audio.";
        }
        catch(Exception ex)
        {
            ShortsRecoveryStatus="Automation checkpoint couldn't be loaded: "+ex.Message;
        }
    }

    private async Task RunShortsQueueAsync()
    {
        if(ShortsRunning)return;
        var entries=ShortQueue.Where(x=>
            !string.IsNullOrWhiteSpace(x.Idea) && x.Status!="Complete").ToList();
        if(entries.Count==0)
        {
            Status="No unfinished Automation Shorts entries. Add another idea to start.";
            return;
        }
        ShortsRunning=true;
        _shortsTtsConfirmed=false;
        (StartShortsCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (ResumeShortsCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        var previousTopic=Topic;
        var previousDuration=Duration;
        var previousAspect=AspectRatio;
        var previousStyle=Style;
        var previousNewChat=NewChat;
        try
        {
            for(var i=0;i<entries.Count;i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var entry=entries[i];
                var recovered=false;
                Topic=entry.Idea.Trim();
                Duration=ShortDuration;
                AspectRatio="9:16";
                Style=previousStyle;
                NewChat=true;
                if(!string.IsNullOrWhiteSpace(entry.ProjectFile) &&
                    File.Exists(entry.ProjectFile))
                {
                    try
                    {
                        Project=await _store.LoadAsync(entry.ProjectFile);
                        ProjectDirectory=Project.ProjectDirectory;
                        NewChat=false; // Resume the original project conversation.
                        recovered=Project.Chapters.Count>0;
                        ShortsRecoveryStatus="Resuming "+entry.Idea+
                            " from saved project; validated assets will be reused.";
                        Log(ShortsRecoveryStatus);
                    }
                    catch(Exception ex)
                    {
                        Log("Recovery project couldn't be loaded. Starting fresh: "+ex.Message);
                        recovered=false;
                    }
                }
                Log($"Shorts queue {i+1}/{entries.Count}: {Topic}"+
                    (recovered?" (recovered)":" (new)"));
                if(!recovered)
                {
                    entry.Status="Writing";
                    await GeneratePlanAsync();
                    if(!string.IsNullOrWhiteSpace(Project.ProjectDirectory) &&
                        Project.Topic.Equals(Topic,StringComparison.OrdinalIgnoreCase))
                        entry.ProjectFile=Path.Combine(Project.ProjectDirectory,"project.json");
                    if(Status.StartsWith("Stopped:",StringComparison.Ordinal))
                    {
                        entry.Status="Failed (writing) - resume available";
                        ShortsRecoveryStatus="Writing failed; completed chapters remain saved. " +
                            "Press Resume Unfinished when ready.";
                        break;
                    }
                }
                else if(!CanBuildVideo())
                {
                    entry.Status="Writing";
                    await WriteMissingChaptersAsync();
                    if(Status.StartsWith("Stopped:",StringComparison.Ordinal))
                    {
                        entry.Status="Failed (writing) - resume available";
                        break;
                    }
                }

                if(!CanBuildVideo())
                {
                    entry.Status="Failed (writing) - resume available";
                    ShortsRecoveryStatus="Some script chapters are missing; use Resume Unfinished.";
                    break;
                }
                entry.ProjectFile=Path.Combine(Project.ProjectDirectory,"project.json");
                // If a prior run rendered the MP4 and stopped at thumbnail,
                // don't waste time rendering or change its audio/captions.
                var existingOutput=PublishingQueue.FirstOrDefault(x=>
                    x.VideoPath.StartsWith(Project.ProjectDirectory+
                        Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(x.VideoPath))?.VideoPath;
                var alreadyRendered=existingOutput is not null &&
                    (entry.Status.Contains("Thumbnail",StringComparison.OrdinalIgnoreCase) ||
                     entry.Status.Contains("video complete",StringComparison.OrdinalIgnoreCase));
                if(!alreadyRendered)
                {
                    entry.Status="Rendering";
                    await RunPipelineAsync();
                    if(Status.StartsWith("Stopped:",StringComparison.Ordinal))
                    {
                        entry.Status="Failed (render) - resume available";
                        ShortsRecoveryStatus="Render failed; existing scenes and audio were preserved. " +
                            "Press Resume Unfinished to retry only missing work.";
                        break;
                    }
                }
                else Log("Recovery: existing completed MP4 retained. Resuming thumbnail only.");
                entry.Status="Thumbnail";
                try
                {
                    var thumbnail=Path.Combine(Project.ProjectDirectory,"thumbnail.png");
                    if(!File.Exists(thumbnail))
                        await GenerateShortThumbnailAsync();
                    else
                        UpdateQueuedThumbnail();
                    entry.Status="Complete";
                    ShortsRecoveryStatus="Completed "+entry.Idea+
                        ". Queue checkpoints saved for the remaining videos.";
                }
                catch(Exception ex)
                {
                    Log("Thumbnail could not be generated: "+ex.Message);
                    entry.Status="Video complete; thumbnail failed";
                    ShortsRecoveryStatus="Video is finished; retry resumes thumbnail only.";
                    break;
                }
            }
        }
        catch(OperationCanceledException)
        {
            Status="Shorts queue cancelled";
            ShortsRecoveryStatus="Automation interrupted. Run Resume Unfinished to continue.";
        }
        catch(Exception ex)
        {
            Status="Stopped: "+ex.Message;
            ShortsRecoveryStatus="Automation stopped; progress saved. "+ex.Message;
            Log(ShortsRecoveryStatus);
        }
        finally
        {
            SaveShortsRecovery();
            Topic=previousTopic;
            Duration=previousDuration;
            AspectRatio=previousAspect;
            Style=previousStyle;
            NewChat=previousNewChat;
            _shortsTtsConfirmed=false;
            ShortsRunning=false;
            (StartShortsCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (ResumeShortsCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task GenerateShortThumbnailAsync()
    {
        var chapter = new ProjectChapter { Number = 999, Title = "Thumbnail" };
        var scene = new ProjectScene
        {
            Number = 1,
            ImagePrompt = $"Create a striking, readable 9:16 vertical video cover image specifically for the title: {Project.Title}. " +
                          $"Match the project's visual style: {Project.Style}. One compelling focal point, bold composition, " +
                          "high contrast, clear visual storytelling, no unrelated topics."
        };
        var path = await _chat.GenerateSceneImageAsync(Project, chapter, scene, Settings, Log, _cts.Token);
        if (!File.Exists(path)) throw new IOException("Generated thumbnail image was not saved.");
        var destination = Path.Combine(Project.ProjectDirectory, "thumbnail.png");
        File.Copy(path, destination, true);
        Log($"Shorts thumbnail: {destination}");
        UpdateQueuedThumbnail();
    }
}
