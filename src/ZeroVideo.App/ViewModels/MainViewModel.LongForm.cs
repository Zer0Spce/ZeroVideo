using System.Collections.ObjectModel;
using System.Text.Json;
using System.ComponentModel;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Models;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed partial class MainViewModel
{
    private int _longFormDuration = 10;
    private bool _longFormRunning;
    public ObservableCollection<int> LongFormDurations { get; } = [5,10,15,20,25,30,35,40,45,50,55,60,75,90,120,150,180];
    public ObservableCollection<ShortQueueEntry> LongFormQueue { get; } = [new()];
    public int LongFormDuration { get => _longFormDuration; set => Set(ref _longFormDuration, Math.Clamp(value,5,180)); }
    public bool LongFormRunning { get => _longFormRunning; private set => Set(ref _longFormRunning, value); }
    public ICommand AddLongFormRowCommand { get; private set; } = null!;
    public ICommand RemoveLongFormRowCommand { get; private set; } = null!;
    public ICommand StartLongFormCommand { get; private set; } = null!;
    public ICommand ResumeLongFormCommand { get; private set; } = null!;
    public ICommand RepairLongFormCaptionsCommand { get; private set; } = null!;
    public ICommand CheckAllLongFormCommand { get; private set; } = null!;
    public ICommand DeleteCheckedLongFormCommand { get; private set; } = null!;
    public bool AllLongFormChecked
    {
        get => LongFormQueue.Count > 0 && LongFormQueue.All(x => x.IsChecked);
        set
        {
            if (LongFormRunning) return;
            foreach (var row in LongFormQueue) row.IsChecked=value;
            Raise(nameof(AllLongFormChecked));
        }
    }
    private static readonly string LongFormCheckpointFile =
        Path.Combine(AppPaths.DataDirectory,"longform-recovery.json");
    private bool _loadingLongFormCheckpoint;
    private string _longFormRecoveryStatus = "Automation recovery: a checkpoint is saved after each stage.";
    public string LongFormRecoveryStatus
    {
        get => _longFormRecoveryStatus; private set => Set(ref _longFormRecoveryStatus,value);
    }


    // Shared by Idea Generation and Content Series. The two automation
    // queues are completely independent: adding a title here never changes
    // the existing Shorts list or its checkpoint.
    private int AddTitlesToLongFormQueue(IEnumerable<string> titles)
    {
        var known=new HashSet<string>(
            LongFormQueue.Select(x=>x.Idea.Trim()),StringComparer.OrdinalIgnoreCase);
        var added=0;
        foreach(var candidate in titles)
        {
            var title=candidate?.Trim()??"";
            if(title.Length==0||!known.Add(title))continue;
            if(LongFormQueue.Count==1 &&
               string.IsNullOrWhiteSpace(LongFormQueue[0].Idea))
                LongFormQueue[0].Idea=title;
            else LongFormQueue.Add(new ShortQueueEntry{Idea=title});
            added++;
        }
        return added;
    }

    private void InitializeLongFormCommands()
    {
        AddLongFormRowCommand = new RelayCommand(() => LongFormQueue.Add(new ShortQueueEntry()));
        RemoveLongFormRowCommand = new RelayCommand(() =>
        {
            if (!LongFormRunning && LongFormQueue.Count > 1) LongFormQueue.RemoveAt(LongFormQueue.Count - 1);
        });
        StartLongFormCommand = new AsyncCommand(()=>RunManagedJobAsync("Long Form Automation",RunLongFormQueueAsync), () => !LongFormRunning);
        ResumeLongFormCommand = new AsyncCommand(()=>RunManagedJobAsync("Long Form Automation — Resume",RunLongFormQueueAsync), () => !LongFormRunning);
        RepairLongFormCaptionsCommand = new AsyncCommand(()=>
            RunManagedJobAsync("Repair completed Long Form captions",
                RepairSelectedLongFormCaptionsAsync),
            () => !LongFormRunning);
        CheckAllLongFormCommand = new RelayCommand(() =>
        {
            if (LongFormRunning) { LongFormRecoveryStatus="Cannot change selection while automation is running."; return; }
            AllLongFormChecked=true;
            LongFormRecoveryStatus=$"Checked all {LongFormQueue.Count} Long Form Automation rows.";
        });
        DeleteCheckedLongFormCommand = new RelayCommand(() =>
        {
            if (LongFormRunning)
            {
                LongFormRecoveryStatus="Stop Long Form Automation before deleting queue entries.";
                return;
            }
            var selected=LongFormQueue.Where(x=>x.IsChecked).ToList();
            if(selected.Count==0)
            {
                LongFormRecoveryStatus="Check one or more rows to delete.";
                return;
            }
            if(System.Windows.MessageBox.Show(
                $"Remove {selected.Count} checked row(s) from Long Form Automation?\n\n"+
                "Only queue rows are removed. Existing project folders, rendered MP4s, "+
                "PNGs, audio and saved files will NOT be deleted.",
                "Delete checked queue rows",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question)!=
                System.Windows.MessageBoxResult.Yes)return;
            foreach(var row in selected)LongFormQueue.Remove(row);
            if(LongFormQueue.Count==0)LongFormQueue.Add(new ShortQueueEntry());
            Raise(nameof(AllLongFormChecked));
            SaveLongFormRecovery();
            LongFormRecoveryStatus=$"Removed {selected.Count} queue row(s). "+
                "Completed video files and project data were preserved.";
        });
        LongFormQueue.CollectionChanged += (_,args) =>
        {
            if(args.OldItems is not null)
                foreach(ShortQueueEntry entry in args.OldItems)
                    entry.PropertyChanged -= OnLongFormRecoveryEntryChanged;
            if(args.NewItems is not null)
                foreach(ShortQueueEntry entry in args.NewItems)
                    entry.PropertyChanged += OnLongFormRecoveryEntryChanged;
            Raise(nameof(AllLongFormChecked));
            SaveLongFormRecovery();
        };
        foreach(var entry in LongFormQueue)
            entry.PropertyChanged += OnLongFormRecoveryEntryChanged;
    }

    private void OnLongFormRecoveryEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(ShortQueueEntry.IsChecked))
            Raise(nameof(AllLongFormChecked));
        SaveLongFormRecovery();
    }

    private void SaveLongFormRecovery()
    {
        if(_loadingLongFormCheckpoint)return;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var temp=LongFormCheckpointFile+".tmp";
            File.WriteAllText(temp,JsonSerializer.Serialize(LongFormQueue.ToList(),
                new JsonSerializerOptions{WriteIndented=true}));
            File.Move(temp,LongFormCheckpointFile,true);
        }
        catch(Exception ex) { Log("Automation recovery save failed: "+ex.Message); }
    }

    private void LoadLongFormRecovery()
    {
        if(!File.Exists(LongFormCheckpointFile))return;
        try
        {
            var restored=JsonSerializer.Deserialize<List<ShortQueueEntry>>(
                File.ReadAllText(LongFormCheckpointFile))??[];
            if(restored.Count==0)return;
            _loadingLongFormCheckpoint=true;
            try
            {
                LongFormQueue.Clear();
                foreach(var row in restored.Take(500))
                {
                    // On startup, a stage left interrupted is resumable.
                    // Never mark it completed unless previously finished.
                    if(row.Status is "Writing" or "Rendering" or "Thumbnail")
                        row.Status="Interrupted (" + row.Status + ") - ready to resume";
                    LongFormQueue.Add(row);
                }
                if(LongFormQueue.Count==0)LongFormQueue.Add(new ShortQueueEntry());
            }
            finally { _loadingLongFormCheckpoint=false; }
            SaveLongFormRecovery();
            LongFormRecoveryStatus=$"Recovered {LongFormQueue.Count} automation row(s) from disk. " +
                "Press Resume Unfinished to reuse saved scripts, visuals and chapter audio.";
        }
        catch(Exception ex)
        {
            LongFormRecoveryStatus="Automation checkpoint couldn't be loaded: "+ex.Message;
        }
    }

    private async Task RepairSelectedLongFormCaptionsAsync()
    {
        if(LongFormRunning)
            throw new InvalidOperationException(
                "Stop Long Form Automation before repairing existing videos.");
        var selected=LongFormQueue.Where(x=>x.IsChecked &&
            !string.IsNullOrWhiteSpace(x.ProjectFile) &&
            File.Exists(x.ProjectFile)).ToList();
        if(selected.Count==0)
        {
            LongFormRecoveryStatus="Check a completed Longform queue row "+
                "with an existing project file before clicking Repair Captions.";
            Log(LongFormRecoveryStatus);
            return;
        }
        Log("Long Form caption repair: "+selected.Count+
            " selected project(s). Will NOT regenerate scripts, images, "+
            "narration, thumbnails or publish videos.");
        var repaired=0;
        foreach(var row in selected)
        {
            _cts.Token.ThrowIfCancellationRequested();
            var project=await _store.LoadAsync(row.ProjectFile);
            if(project.AspectRatio!="16:9")
                throw new InvalidOperationException(
                    "The selected row is not a 16:9 Longform project: "+
                    project.Title);
            var exports=Path.Combine(project.ProjectDirectory,"exports");
            if(!Directory.Exists(exports))
                throw new DirectoryNotFoundException(
                    "This row has no completed exported video: "+exports);
            var candidates=Directory.GetFiles(exports,"*.mp4",
                SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc).ToList();
            if(candidates.Count==0)
                throw new FileNotFoundException(
                    "No finished MP4 found for the selected Longform project.",
                    exports);
            var path=await _ffmpeg.RepairExistingLongFormCaptionsAsync(
                project,candidates[0],Settings,Log,_cts.Token);
            repaired++;
            LongFormRecoveryStatus="Caption repair "+repaired+"/"+selected.Count+
                " completed: "+Path.GetFileName(path);
            Log(LongFormRecoveryStatus);
        }
        LongFormRecoveryStatus="Captions repaired on "+repaired+
            " completed 16:9 video(s). Original MP4 backups were kept "+
            "inside each project's render folder.";
        Log(LongFormRecoveryStatus);
    }

    private async Task RunLongFormQueueAsync()
    {
        if(LongFormRunning)return;
        var entries=LongFormQueue.Where(x=>
            !string.IsNullOrWhiteSpace(x.Idea) && x.Status!="Complete").ToList();
        if(entries.Count==0)
        {
            Status="No unfinished Long Form Automation entries. Add another idea to start.";
            return;
        }
        LongFormRunning=true;
        _shortsTtsConfirmed=false;
        (StartLongFormCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (ResumeLongFormCommand as AsyncCommand)?.RaiseCanExecuteChanged();
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
                Duration=LongFormDuration;
                AspectRatio="16:9";
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
                        if(Project.AspectRatio!="16:9")
                            throw new InvalidDataException(
                                "Long Form recovery only accepts a saved 16:9 project.");
                        recovered=Project.Chapters.Count>0;
                        if(recovered)
                        {
                            Duration=Project.TargetMinutes;
                            Log("Recovery preserves the saved "+
                                Project.TargetMinutes+"-minute 16:9 outline.");
                        }
                        LongFormRecoveryStatus="Resuming "+entry.Idea+
                            " from saved project; validated assets will be reused.";
                        Log(LongFormRecoveryStatus);
                    }
                    catch(Exception ex)
                    {
                        Log("Recovery project couldn't be loaded. Starting fresh: "+ex.Message);
                        recovered=false;
                    }
                }
                Log($"Long form queue {i+1}/{entries.Count}: {Topic}"+
                    (recovered?" (recovered)":" (new)"));
                if(!recovered)
                {
                    entry.Status="Writing";
                    await GeneratePlanAsync();
                    // Use the requested format even if the AI outline
                    // includes a mismatched aspect-ratio field.
                    if(Project.Chapters.Count>0 &&
                       Project.Topic.Equals(Topic,StringComparison.OrdinalIgnoreCase))
                    {
                        Project.AspectRatio="16:9";
                        Project.TargetMinutes=LongFormDuration;
                        await _store.SaveAsync(Project,Settings.ProjectsRoot);
                    }
                    if(!string.IsNullOrWhiteSpace(Project.ProjectDirectory) &&
                        Project.Topic.Equals(Topic,StringComparison.OrdinalIgnoreCase))
                        entry.ProjectFile=Path.Combine(Project.ProjectDirectory,"project.json");
                    if(Status.StartsWith("Stopped:",StringComparison.Ordinal))
                    {
                        entry.Status="Failed (writing) - resume available";
                        LongFormRecoveryStatus="Writing failed; completed chapters remain saved. " +
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
                    LongFormRecoveryStatus="Some script chapters are missing; use Resume Unfinished.";
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
                        LongFormRecoveryStatus="Render failed; existing scenes and audio were preserved. " +
                            "Press Resume Unfinished to retry only missing work.";
                        break;
                    }
                }
                else
                {
                    Log("Recovery: existing completed MP4 retained. Resuming thumbnail only.");
                    // The previous process could have exited after FFmpeg
                    // finished but before temporary portrait cleanup.
                    // Only tracked auto-generated reference PNGs are deleted.
                    if ((Project.RealPeople ?? []).Any(p =>
                        p.AutoTemporaryReferencePaths?.Count > 0) ||
                        Project.AutoIdentityEnabledByVideo)
                    {
                        AutoRealPersonIdentityService.Cleanup(Project, Log);
                        await _store.SaveAsync(Project,Settings.ProjectsRoot);
                    }
                }
                entry.Status="Thumbnail";
                try
                {
                    var thumbnail=Path.Combine(Project.ProjectDirectory,"thumbnail.png");
                    if(!File.Exists(thumbnail))
                        await GenerateLongFormThumbnailAsync();
                    else
                        UpdateQueuedThumbnail();
                    entry.Status="Complete";
                    LongFormRecoveryStatus="Completed "+entry.Idea+
                        ". Queue checkpoints saved for the remaining videos.";
                }
                catch(Exception ex)
                {
                    Log("Thumbnail could not be generated: "+ex.Message);
                    entry.Status="Video complete; thumbnail failed";
                    LongFormRecoveryStatus="Video is finished; retry resumes thumbnail only.";
                    break;
                }
            }
        }
        catch(OperationCanceledException)
        {
            Status="Long form queue cancelled";
            LongFormRecoveryStatus="Automation interrupted. Run Resume Unfinished to continue.";
        }
        catch(Exception ex)
        {
            Status="Stopped: "+ex.Message;
            LongFormRecoveryStatus="Automation stopped; progress saved. "+ex.Message;
            Log(LongFormRecoveryStatus);
        }
        finally
        {
            SaveLongFormRecovery();
            Topic=previousTopic;
            Duration=previousDuration;
            AspectRatio=previousAspect;
            Style=previousStyle;
            NewChat=previousNewChat;
            _shortsTtsConfirmed=false;
            LongFormRunning=false;
            (StartLongFormCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (ResumeLongFormCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        }
    }

    private async Task GenerateLongFormThumbnailAsync()
    {
        // Full-size 16:9/9:16 artwork is generated in the existing ChatGPT
        // workflow, then a separate local renderer prints a large, uniform,
        // correctly spelled 2–5-word hook. This guarantees readable text
        // on newly created thumbnails as well as later regenerations.
        var service = new ThumbnailArtworkService(_chat);
        var saved = await service.RegenerateAsync(
            Project, Settings, Log, _cts.Token, variation: 0);
        Log("Thumbnail ready: " + saved);
        UpdateQueuedThumbnail();
    }}
