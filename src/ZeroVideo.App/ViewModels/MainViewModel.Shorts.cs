using System.Collections.ObjectModel;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Models;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed class ShortQueueEntry : ObservableObject
{
    private string _idea = "";
    private string _status = "Pending";
    public string Idea { get => _idea; set => Set(ref _idea, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
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

    private void InitializeShortsCommands()
    {
        AddShortRowCommand = new RelayCommand(() => ShortQueue.Add(new ShortQueueEntry()));
        RemoveShortRowCommand = new RelayCommand(() =>
        {
            if (!ShortsRunning && ShortQueue.Count > 1) ShortQueue.RemoveAt(ShortQueue.Count - 1);
        });
        StartShortsCommand = new AsyncCommand(RunShortsQueueAsync, () => !ShortsRunning);
    }

    private async Task RunShortsQueueAsync()
    {
        if (ShortsRunning) return;
        var entries = ShortQueue.Where(x => !string.IsNullOrWhiteSpace(x.Idea)).ToList();
        if (entries.Count == 0)
        {
            Status = "Add at least one Shorts idea or link.";
            return;
        }

        ShortsRunning = true;
        _shortsTtsConfirmed = false;
        (StartShortsCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        var previousTopic = Topic;
        var previousDuration = Duration;
        var previousAspect = AspectRatio;
        var previousStyle = Style;
        var previousNewChat = NewChat;
        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var entry = entries[i];
                entry.Status = "Writing";
                Topic = entry.Idea.Trim();
                Duration = ShortDuration;
                AspectRatio = "9:16";
                Style = previousStyle;
                NewChat = true;
                Log($"Shorts queue {i + 1}/{entries.Count}: {Topic}");
                await GeneratePlanAsync();
                if (Status.StartsWith("Stopped:", StringComparison.Ordinal))
                {
                    entry.Status = "Failed (writing)";
                    break;
                }

                entry.Status = "Rendering";
                await RunPipelineAsync();
                if (Status.StartsWith("Stopped:", StringComparison.Ordinal))
                {
                    entry.Status = "Failed (render)";
                    break;
                }

                entry.Status = "Thumbnail";
                try
                {
                    await GenerateShortThumbnailAsync();
                    entry.Status = "Complete";
                }
                catch (Exception ex)
                {
                    Log($"Thumbnail could not be generated: {ex.Message}");
                    entry.Status = "Video complete; thumbnail failed";
                }
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Shorts queue cancelled";
        }
        finally
        {
            Topic = previousTopic;
            Duration = previousDuration;
            AspectRatio = previousAspect;
            Style = previousStyle;
            NewChat = previousNewChat;
            _shortsTtsConfirmed = false;
            ShortsRunning = false;
            (StartShortsCommand as AsyncCommand)?.RaiseCanExecuteChanged();
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
