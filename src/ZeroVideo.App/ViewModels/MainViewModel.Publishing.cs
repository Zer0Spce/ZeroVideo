using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed class PublishQueueItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string VideoPath { get; set; } = "";
    public string ThumbnailPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Hashtags { get; set; } = "";
    public string YouTubeTitle { get; set; } = "";
    public string YouTubeDescription { get; set; } = "";
    public string TikTokCaption { get; set; } = "";
    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
    private string _metadataStatus = "Pending";
    public string MetadataStatus { get => _metadataStatus; set => Set(ref _metadataStatus, value); }
    private bool _isScheduled;
    public bool IsScheduled { get => _isScheduled; set { if (Set(ref _isScheduled, value)) Raise(nameof(IsInstant)); } }
    public bool IsInstant { get => !IsScheduled; set { if (value) IsScheduled = false; } }
    private string _scheduleDateText = "";
    public string ScheduleDateText { get => _scheduleDateText; set => Set(ref _scheduleDateText, value); }
    public string SeoTitle => string.IsNullOrWhiteSpace(YouTubeTitle) ? Title : YouTubeTitle;
    public string SeoDescription => string.IsNullOrWhiteSpace(YouTubeDescription) ? Description : YouTubeDescription;
    public string SeoHashtags => !string.IsNullOrWhiteSpace(Hashtags) ? Hashtags : string.Join(" ", TikTokCaption.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(tag => tag.StartsWith("#")));
    public DateTimeOffset? ScheduledAt { get; set; }
    public string YouTubeCheckpoint { get; set; } = "Not started";
    public string TikTokCheckpoint { get; set; } = "Not started";
    public string YouTubeCheckpointNote { get; set; } = "";
    public string TikTokCheckpointNote { get; set; } = "";
    public DateTimeOffset? YouTubeCheckpointUpdated { get; set; }
    public DateTimeOffset? TikTokCheckpointUpdated { get; set; }
    public string YouTubeStatus { get; set; } = "Ready";
    private string _tikTokStatus = "Ready";
    public string TikTokStatus { get => _tikTokStatus; set => Set(ref _tikTokStatus, value); }
    public void RaiseSeoChanged()
    {
        Raise(nameof(SeoTitle));
        Raise(nameof(SeoDescription));
        Raise(nameof(SeoHashtags));
    }
    public string Display => $"{Title}  |  YouTube: {YouTubeStatus}  |  TikTok: {TikTokStatus}" +
        (ScheduledAt.HasValue ? $"  |  {ScheduledAt.Value:yyyy-MM-dd HH:mm zzz}" : "");
}

public sealed partial class MainViewModel
{
    private static readonly string QueueFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeroVideo", "publishing-queue.json");
    public ObservableCollection<PublishQueueItem> PublishingQueue { get; } = [];
    // Select All is computed from individual checkbox state.
    public bool AllPublishItemsChecked
    {
        get => PublishingQueue.Count > 0 && PublishingQueue.All(x => x.IsChecked);
        set
        {
            if (IsBatchPublishing) return;
            foreach (var item in PublishingQueue) item.IsChecked = value;
            Raise(nameof(AllPublishItemsChecked));
        }
    }
    private PublishQueueItem? _selectedPublishItem;
    private int _publishIntervalHours = 6;
    private string _publishFirstSlot = DateTime.Now.Date.AddDays(1).AddHours(12).ToString("yyyy-MM-dd HH:mm");
    public ObservableCollection<int> PublishIntervals { get; } = [1, 2, 3, 6, 12, 24, 48];
    public PublishQueueItem? SelectedPublishItem
    {
        get => _selectedPublishItem;
        set
        {
            if (!Set(ref _selectedPublishItem, value) || value is null) return;
            PublishFile = value.VideoPath;
            LoadSelectedCheckpoint();
            PublishTitle = value.Title;
            PublishDescription = value.Description;
            PublishHashtags = value.Hashtags;
            YouTubeTitleText = string.IsNullOrWhiteSpace(value.YouTubeTitle) ? value.Title : value.YouTubeTitle;
            YouTubeDescriptionText = string.IsNullOrWhiteSpace(value.YouTubeDescription) ? value.Description : value.YouTubeDescription;
            TikTokCaptionText = value.TikTokCaption;
            PublishSchedule = value.ScheduledAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
            PublishThumbnail = value.ThumbnailPath;
        }
    }
    private string _youTubeTitleText = "";
    private string _youTubeDescriptionText = "";
    private string _tikTokCaptionText = "";
    public string YouTubeTitleText
    {
        get => _youTubeTitleText;
        set { if (Set(ref _youTubeTitleText, value)) { Raise(nameof(YouTubeTitleCount)); if (SelectedPublishItem is not null) { SelectedPublishItem.YouTubeTitle = value; SavePublishingQueue(); } } }
    }
    public string YouTubeDescriptionText
    {
        get => _youTubeDescriptionText;
        set { if (Set(ref _youTubeDescriptionText, value) && SelectedPublishItem is not null) { SelectedPublishItem.YouTubeDescription = value; SavePublishingQueue(); } }
    }
    public string TikTokCaptionText
    {
        get => _tikTokCaptionText;
        set { if (Set(ref _tikTokCaptionText, value) && SelectedPublishItem is not null) { SelectedPublishItem.TikTokCaption = value; SavePublishingQueue(); } }
    }
    public string YouTubeTitleCount => $"{YouTubeTitleText.Length}/100 characters" + (YouTubeTitleText.Length > 100 ? " — TOO LONG" : "");
    private string _publishHashtags = "";
    public string PublishHashtags { get => _publishHashtags; set => Set(ref _publishHashtags, value); }
    private string _publishThumbnail = "";
    public string PublishThumbnail { get => _publishThumbnail; set => Set(ref _publishThumbnail, value); }
    public int PublishIntervalHours { get => _publishIntervalHours; set => Set(ref _publishIntervalHours, value); }
    public string PublishFirstSlot { get => _publishFirstSlot; set => Set(ref _publishFirstSlot, value); }
    public ICommand SchedulePublishQueueCommand { get; private set; } = null!;
    public ICommand SmartSchedulePublishQueueCommand { get; private set; } = null!;
    public ICommand GeneratePublishMetadataCommand { get; private set; } = null!;
    public ICommand OpenYouTubeStudioCommand { get; private set; } = null!;
    public ICommand OpenTikTokStudioCommand { get; private set; } = null!;
    public ICommand BrowserPublishCommand { get; private set; } = null!;
    public ICommand SavePublishCheckpointCommand { get; private set; } = null!;
    public ICommand ResumePublishCheckpointCommand { get; private set; } = null!;
    public ObservableCollection<string> PublishCheckpointStages { get; } =
        ["Not started", "Studio opened", "Video selected", "Upload accepted", "Metadata entered", "Schedule entered", "Platform confirmed"];
    private string _publishCheckpointStage = "Not started";
    private string _publishCheckpointNote = "";
    public string PublishCheckpointStage { get => _publishCheckpointStage; set => Set(ref _publishCheckpointStage, value); }
    public string PublishCheckpointNote { get => _publishCheckpointNote; set => Set(ref _publishCheckpointNote, value); }
    private void LoadSelectedCheckpoint()
    {
        if (SelectedPublishItem is null) return;
        var youtube = PublishDestination == "YouTube";
        PublishCheckpointStage = youtube ? SelectedPublishItem.YouTubeCheckpoint : SelectedPublishItem.TikTokCheckpoint;
        PublishCheckpointNote = youtube ? SelectedPublishItem.YouTubeCheckpointNote : SelectedPublishItem.TikTokCheckpointNote;
    }
    private void SaveSelectedCheckpoint()
    {
        var item = SelectedPublishItem;
        if (item is null) { PublishStatus = "Select a video first."; return; }
        if (!PublishCheckpointStages.Contains(PublishCheckpointStage))
        { PublishStatus = "Choose a valid checkpoint."; return; }
        if (PublishDestination == "YouTube")
        {
            item.YouTubeCheckpoint = PublishCheckpointStage;
            item.YouTubeCheckpointNote = PublishCheckpointNote;
            item.YouTubeCheckpointUpdated = DateTimeOffset.Now;
        }
        else
        {
            item.TikTokCheckpoint = PublishCheckpointStage;
            item.TikTokCheckpointNote = PublishCheckpointNote;
            item.TikTokCheckpointUpdated = DateTimeOffset.Now;
        }
        SavePublishingQueue();
        PublishStatus = "Checkpoint saved locally. This is user-confirmed progress, not platform verification.";
    }
    private string _publishDestination = "TikTok";
    public string PublishDestination { get => _publishDestination; set { if (Set(ref _publishDestination, value)) LoadSelectedCheckpoint(); } }
    public ObservableCollection<string> PublishDestinations { get; } = ["TikTok"];
    public ICommand RemovePublishQueueCommand { get; private set; } = null!;
    public ICommand SelectAllPublishQueueCommand { get; private set; } = null!;
    public ICommand ClearPublishQueueSelectionCommand { get; private set; } = null!;
    private bool _isBatchPublishing;
    public bool IsBatchPublishing { get => _isBatchPublishing; private set => Set(ref _isBatchPublishing, value); }

    private void LoadPublishingQueue()
    {
        try
        {
            if (!File.Exists(QueueFile)) return;
            foreach (var item in JsonSerializer.Deserialize<List<PublishQueueItem>>(File.ReadAllText(QueueFile)) ?? [])
                if (File.Exists(item.VideoPath) && !PublishingQueue.Any(x => x.VideoPath.Equals(item.VideoPath, StringComparison.OrdinalIgnoreCase)))
                {
                    item.IsScheduled = item.ScheduledAt.HasValue;
                    item.ScheduleDateText = item.ScheduledAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
                    if (!string.IsNullOrWhiteSpace(item.YouTubeTitle) && !string.IsNullOrWhiteSpace(item.TikTokCaption))
                        item.MetadataStatus = "Ready";
                    TrackPublishingItem(item);
                    PublishingQueue.Add(item);
                }
            Raise(nameof(AllPublishItemsChecked));
        }
        catch (Exception ex) { Log("Publishing queue could not be loaded: " + ex.Message); }
    }

    private void TrackPublishingItem(PublishQueueItem item)
    {
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PublishQueueItem.ScheduleDateText) && item.IsScheduled &&
                DateTime.TryParseExact(item.ScheduleDateText, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var scheduled) && scheduled > DateTime.Now)
                item.ScheduledAt = new DateTimeOffset(scheduled);
            if (args.PropertyName == nameof(PublishQueueItem.IsChecked))
                Raise(nameof(AllPublishItemsChecked));
            if (args.PropertyName == nameof(PublishQueueItem.IsScheduled) && !item.IsScheduled)
                item.ScheduledAt = null;
            SavePublishingQueue();
        };
    }

    private void SavePublishingQueue()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(QueueFile)!);
            var temporary = QueueFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(PublishingQueue.ToList()));
            File.Move(temporary, QueueFile, true);
        }
        catch (Exception ex) { Log("Publishing queue could not be saved: " + ex.Message); }
    }

    private void QueueCompletedVideo(string output)
    {
        if (!File.Exists(output)) { Log("Publishing queue: rendered file missing, not added."); return; }
        if (PublishingQueue.Any(x => x.VideoPath.Equals(output, StringComparison.OrdinalIgnoreCase))) return;
        var thumbnail = Path.Combine(Project.ProjectDirectory, "thumbnail.png");
        var item = new PublishQueueItem
        {
            VideoPath = output,
            ThumbnailPath = File.Exists(thumbnail) ? thumbnail : "",
            Title = string.IsNullOrWhiteSpace(Project.Title) ? Path.GetFileNameWithoutExtension(output) : Project.Title,
            Description = Project.Topic
        };
        TrackPublishingItem(item);
        PublishingQueue.Add(item);
        Raise(nameof(AllPublishItemsChecked));
        SavePublishingQueue();
        Log("Publishing queue: added " + item.Title);
    }

    private void UpdateQueuedThumbnail()
    {
        var thumbnail = Path.Combine(Project.ProjectDirectory, "thumbnail.png");
        if (!File.Exists(thumbnail)) return;
        foreach (var item in PublishingQueue.Where(x => x.VideoPath.StartsWith(Project.ProjectDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            item.ThumbnailPath = thumbnail;
            if (ReferenceEquals(item, SelectedPublishItem)) PublishThumbnail = thumbnail;
        }
        SavePublishingQueue();
    }

    private void SchedulePublishingQueue()
    {
        if (IsBatchPublishing) { PublishStatus = "Wait for the current TikTok batch to finish."; return; }
        if (!DateTime.TryParseExact(PublishFirstSlot.Trim(), "yyyy-MM-dd HH:mm",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var first) || first <= DateTime.Now)
        { PublishStatus = "Enter a future local start date: YYYY-MM-DD HH:MM."; return; }
        if (first.Minute % 5 != 0)
        { PublishStatus = "TikTok schedules in 5-minute increments. Choose minutes 00, 05, 10, etc."; return; }
        var interval = PublishIntervalHours;
        if (!PublishIntervals.Contains(interval)) { PublishStatus = "Choose a valid interval."; return; }
        var chosen = PublishingQueue.Where(x => x.IsChecked).ToList();
        if (chosen.Count == 0) { PublishStatus = "Check the videos you want to schedule first."; return; }
        for (var i = 0; i < chosen.Count; i++)
        {
            var localSlot = first.AddHours((double)i * interval);
            chosen[i].IsScheduled = true;
            chosen[i].ScheduleDateText = localSlot.ToString("yyyy-MM-dd HH:mm");
            chosen[i].ScheduledAt = new DateTimeOffset(localSlot);
        }
        SavePublishingQueue();
        PublishStatus = $"Assigned {chosen.Count} TikTok schedule slots at {interval}-hour intervals in queue order.";
    }

    private async Task SmartSchedulePublishingQueueAsync()
    {
        if (IsBatchPublishing)
        {
            PublishStatus = "Wait until the current TikTok publishing batch finishes.";
            return;
        }
        var checkedVideos = PublishingQueue.Where(x => x.IsChecked).ToList();
        if (checkedVideos.Count == 0)
        {
            PublishStatus = "Check the videos you want AI to schedule.";
            return;
        }
        if (checkedVideos.Count > 20)
        {
            PublishStatus = "Smart Schedule supports up to 20 videos per run.";
            return;
        }

        // TikTok's available schedule window is account-dependent. The user
        // confirmed only short-range scheduling works in their Studio, so use
        // a conservative 21-day window and validate all proposed slots.
        var now = DateTime.Now;
        var earliest = now.AddHours(3);
        var latest = now.AddDays(21);
        var reserved = PublishingQueue.Where(x => !x.IsChecked && x.IsScheduled &&
            x.ScheduledAt.HasValue).Select(x => x.ScheduledAt!.Value.LocalDateTime)
            .ToList();
        var originals = checkedVideos.Select(x => new
        {
            x.IsScheduled, x.ScheduleDateText, x.ScheduledAt
        }).ToList();

        try
        {
            PublishStatus = $"AI is estimating suitable TikTok slots for {checkedVideos.Count} checked videos...";
            static string SafeSnippet(string value, int maxLength)
            {
                var cleaned = (value ?? "").Replace('\n',' ').Replace('\r',' ').Trim();
                return cleaned[..Math.Min(maxLength, cleaned.Length)];
            }
            var items = string.Join("\n", checkedVideos.Select((x,i) =>
                $"{i+1}. Title: {SafeSnippet(x.Title, 110)}; " +
                $"Description: {SafeSnippet(x.Description, 140)}; " +
                $"Caption: {SafeSnippet(x.TikTokCaption, 170)}"));
            var prompt = "You are an editorial TikTok publishing schedule planner. " +
                $"Current local date and time: {now:yyyy-MM-dd HH:mm}. " +
                $"Local timezone: {TimeZoneInfo.Local.Id}. " +
                $"Choose EXACTLY {checkedVideos.Count} realistic local posting dates/times for " +
                "the videos in the listed order using topic/niche, time-of-day and weekday " +
                "engagement heuristics. These are educated estimates, NOT actual audience analytics. " +
                "Do not pretend you measured account follower activity, engagement, or TikTok trend data. " +
                $"All slots MUST be after {earliest:yyyy-MM-dd HH:mm} and before " +
                $"{latest:yyyy-MM-dd HH:mm}. They must be strictly chronological, " +
                "at least 60 minutes apart, and multiples of 5 minutes. " +
                "Prefer realistic afternoon/evening windows for a general audience " +
                "but vary slots where topic relevance suggests. " +
                "Avoid these already-booked slots (+/- 60 minutes): " +
                string.Join(", ",reserved.Take(30).Select(x=>x.ToString("yyyy-MM-dd HH:mm"))) +
                ". Return ONLY one valid JSON object with the EXACT schema " +
                "{\"response_type\":\"zerovideo_smart_schedule_v1\",\"schedule\":" +
                "[{\"index\":1,\"local_time\":\"yyyy-MM-dd HH:mm\",\"reason\":\"Short rationale\"}]}. " +
                "Use consecutive 1-based indices starting at 1. " +
                "No markdown. Never claim viral success. VIDEOS:\n" + items;

            var raw = await _chat.GenerateTextAsync(new ZeroVideo.App.Models.VideoProject
            {
                Title = "TikTok smart schedule",
                Topic = "Publishing time planning"
            }, Settings, prompt, Log, _cts.Token);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.GetProperty("response_type").GetString() != "zerovideo_smart_schedule_v1")
                throw new InvalidDataException("AI schedule response was not in the requested format.");
            var slots = root.GetProperty("schedule");
            if (slots.ValueKind != JsonValueKind.Array ||
                slots.GetArrayLength() != checkedVideos.Count)
                throw new InvalidDataException("AI returned a different number of schedule slots.");

            var planned = new List<(DateTime Slot, string Reason)>();
            for (var i = 0; i < checkedVideos.Count; i++)
            {
                var record = slots[i];
                if (record.GetProperty("index").GetInt32() != i+1)
                    throw new InvalidDataException("AI schedule video indices are out of order.");
                var rawTime = record.GetProperty("local_time").GetString() ?? "";
                if (!DateTime.TryParseExact(rawTime, "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
                    time <= earliest || time >= latest || time.Minute%5 != 0)
                    throw new InvalidDataException("AI schedule provided an out-of-range or invalid time: " + rawTime);
                if (planned.Count>0 && time < planned[^1].Slot.AddHours(1))
                    throw new InvalidDataException("AI schedule slots are out of order or closer than one hour.");
                if (reserved.Any(x => Math.Abs((x-time).TotalMinutes)<60))
                    throw new InvalidDataException("AI schedule collides with an existing video: " + rawTime);
                var reason = record.TryGetProperty("reason",out var reasonJson) &&
                    reasonJson.ValueKind==JsonValueKind.String
                    ? (reasonJson.GetString() ?? "") : "";
                planned.Add((time,reason));
            }

            var preview = string.Join(Environment.NewLine,
                checkedVideos.Zip(planned, (item, slot) =>
                    $"{slot.Slot:MMM dd HH:mm} — {item.Title} (" +
                    slot.Reason[..Math.Min(80,slot.Reason.Length)] + ")"));
            if (MessageBox.Show("AI-SUGGESTED TIKTOK SCHEDULE (LOCAL TIME)\n\n" +
                    preview + "\n\nThese are estimates based on content and typical " +
                    "engagement patterns, NOT your account's private analytics. " +
                    "Apply these schedule slots? This does NOT post videos.",
                    "Review Smart Schedule", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                PublishStatus = "AI schedule suggestions canceled. Existing dates unchanged.";
                return;
            }

            for (var i = 0; i < checkedVideos.Count; i++)
            {
                checkedVideos[i].IsScheduled = true;
                checkedVideos[i].ScheduleDateText = planned[i].Slot.ToString(
                    "yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture);
                checkedVideos[i].ScheduledAt = new DateTimeOffset(planned[i].Slot);
            }
            SavePublishingQueue();
            PublishStatus = $"AI proposed and assigned {checkedVideos.Count} slots " +
                "within 21 days. Review the dates before publishing.";
            Log(PublishStatus);
        }
        catch (OperationCanceledException)
        {
            PublishStatus = "Smart Schedule canceled; existing dates preserved.";
        }
        catch (Exception ex)
        {
            // Validation finishes before any mutation: failure leaves every
            // previously chosen slot untouched.
            PublishStatus = "Smart Schedule could not create a valid plan: " + ex.Message;
            Log(PublishStatus);
        }
    }

    private readonly PublishingService _publishing = new();
    private string _publishFile = "";
    private string _publishTitle = "";
    private string _publishDescription = "";
    private string _youtubePrivacy = "private";
    private string _tikTokPrivacy = "SELF_ONLY";
    private string _publishSchedule = "";
    private bool _publishConsent;
    private string _publishStatus = "Select a finished video to prepare browser publishing.";
    // Access tokens remain in process memory and are never written to ZeroVideo settings or logs.
    public string YouTubeAccessToken { private get; set; } = "";
    public string TikTokAccessToken { private get; set; } = "";
    public string PublishFile { get => _publishFile; set => Set(ref _publishFile, value); }
    public string PublishTitle { get => _publishTitle; set => Set(ref _publishTitle, value); }
    public string PublishDescription { get => _publishDescription; set => Set(ref _publishDescription, value); }
    public string YouTubePrivacy { get => _youtubePrivacy; set => Set(ref _youtubePrivacy, value); }
    public string TikTokPrivacy { get => _tikTokPrivacy; set => Set(ref _tikTokPrivacy, value); }
    public string PublishSchedule { get => _publishSchedule; set => Set(ref _publishSchedule, value); }
    public bool PublishConsent { get => _publishConsent; set => Set(ref _publishConsent, value); }
    public string PublishStatus { get => _publishStatus; set => Set(ref _publishStatus, value); }
    public ObservableCollection<string> YouTubePrivacyOptions { get; } = ["private", "unlisted", "public"];
    public ObservableCollection<string> TikTokPrivacyOptions { get; } = ["SELF_ONLY", "MUTUAL_FOLLOW_FRIENDS", "FOLLOWER_OF_CREATOR", "PUBLIC_TO_EVERYONE"];
    public ICommand BrowsePublishFileCommand { get; private set; } = null!;
    public ICommand PublishYouTubeCommand { get; private set; } = null!;
    public ICommand PublishTikTokCommand { get; private set; } = null!;

    private void InitializePublishingCommands()
    {
        LoadPublishingQueue();
        SavePublishCheckpointCommand = new RelayCommand(SaveSelectedCheckpoint);
        ResumePublishCheckpointCommand = new AsyncCommand(async () =>
        {
            if (SelectedPublishItem is null) { PublishStatus = "Select a video first."; return; }
            var stage = PublishCheckpointStage;
            if (stage == "Platform confirmed")
            {
                PublishStatus = "Marked confirmed. Check the post in Studio before attempting another upload.";
                return;
            }
            if (!File.Exists(SelectedPublishItem.VideoPath))
            { PublishStatus = "The video file is missing; cannot resume."; return; }
            try
            {
                await _browser.OpenManualAsync(Settings, PublishDestination == "YouTube" ?
                    "https://studio.youtube.com/" : "https://www.tiktok.com/tiktokstudio/upload");
                PublishStatus = "Saved checkpoint: " + stage +
                    ". Inspect existing drafts/uploads before retrying to prevent duplicate posts. A browser upload cannot be restored automatically.";
            }
            catch (Exception ex) { PublishStatus = "Resume browser: " + ex.Message; }
        });

        BrowserPublishCommand = new AsyncCommand(RunTikTokBatchAsync);

        // YouTube publishing is disabled until explicitly re-enabled.
        OpenYouTubeStudioCommand = new RelayCommand(() =>
            PublishStatus = "YouTube publishing is disabled; metadata is saved for later.");
        OpenTikTokStudioCommand = new AsyncCommand(async () =>
        {
            try { await _browser.OpenManualAsync(Settings, "https://www.tiktok.com/tiktokstudio/upload"); PublishStatus = "TikTok Studio opened. Sign in and review cover and scheduling controls."; }
            catch (Exception ex) { PublishStatus = "TikTok Studio: " + ex.Message; }
        });
        GeneratePublishMetadataCommand = new AsyncCommand(RetryCheckedMetadataAsync);

        SchedulePublishQueueCommand = new RelayCommand(SchedulePublishingQueue);
        SmartSchedulePublishQueueCommand = new AsyncCommand(SmartSchedulePublishingQueueAsync);
        SelectAllPublishQueueCommand = new RelayCommand(() =>
        {
            AllPublishItemsChecked = true;
            PublishStatus = "Checked all finished videos.";
        });
        ClearPublishQueueSelectionCommand = new RelayCommand(() =>
        {
            AllPublishItemsChecked = false;
            PublishStatus = "Cleared the video checkboxes.";
        });
        RemovePublishQueueCommand = new RelayCommand(() =>
        {
            if (IsBatchPublishing) { PublishStatus = "Cannot remove while publishing."; return; }
            var chosen = PublishingQueue.Where(x => x.IsChecked).ToList();
            if (chosen.Count == 0)
            { PublishStatus = "Check the videos you want to remove."; return; }
            if (MessageBox.Show($"Remove {chosen.Count} checked video(s) from this queue? Files will remain on disk.",
                "Remove from queue", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            foreach (var item in chosen)
            {
                PublishingQueue.Remove(item);
                if (ReferenceEquals(SelectedPublishItem, item)) SelectedPublishItem = null;
            }
            SavePublishingQueue();
            Raise(nameof(AllPublishItemsChecked));
            PublishStatus = $"Removed {chosen.Count} queue entry/entries; video files were not deleted.";
        });
        BrowsePublishFileCommand = new RelayCommand(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "MP4 videos|*.mp4", CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            PublishFile = dialog.FileName;
            PublishTitle = Path.GetFileNameWithoutExtension(dialog.FileName);
            QueueCompletedVideoFromBrowse(dialog.FileName);
        });
        PublishYouTubeCommand = new RelayCommand(() =>
            PublishStatus = "YouTube publishing is disabled for now.");

        PublishTikTokCommand = new AsyncCommand(async () =>
        {
            if (!ValidatePublish()) return;
            try
            {
                PublishStatus = "Uploading to TikTok...";
                PublishStatus = await _publishing.UploadTikTokAsync(PublishFile, TikTokCaptionText,
                    TikTokAccessToken, TikTokPrivacy, _cts.Token);
                if (SelectedPublishItem is not null) { SelectedPublishItem.TikTokStatus = "Processing"; SavePublishingQueue(); Raise(nameof(PublishingQueue)); }
            }
            catch (Exception ex) { PublishStatus = "TikTok: " + ex.Message; }
            finally { PublishConsent = false; }
        });
    }

    // TikTok-only sequential publishing. The snapshot is taken once in visual
    // queue order. A video never gets submitted twice within this batch.
    private async Task RunTikTokBatchAsync()
    {
        if (IsBatchPublishing) return;
        var chosen = PublishingQueue.Where(x => x.IsChecked).ToList();
        if (chosen.Count == 0)
        {
            PublishStatus = "Check one or more finished videos to publish.";
            return;
        }
        if (!PublishConsent)
        {
            PublishStatus = "Check the publishing authorization box before submitting TikTok videos.";
            return;
        }
        var prepared = new List<(PublishQueueItem Item, DateTimeOffset? Slot)>();
        foreach (var item in chosen)
        {
            if (!File.Exists(item.VideoPath))
            { PublishStatus = "Video file missing: " + item.Title; return; }
            if (string.IsNullOrWhiteSpace(item.TikTokCaption))
            {
                PublishStatus = "TikTok metadata missing for '" + item.Title +
                    "'. Check it and use Retry Metadata.";
                return;
            }
            DateTimeOffset? slot = null;
            if (item.IsScheduled)
            {
                if (!DateTime.TryParseExact(item.ScheduleDateText.Trim(),
                    "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var local) ||
                    local <= DateTime.Now.AddMinutes(20) || local.Minute % 5 != 0)
                {
                    PublishStatus = "Invalid TikTok schedule for '" + item.Title +
                        "'. Use a future YYYY-MM-DD HH:MM on 5-minute increments.";
                    return;
                }
                slot = new DateTimeOffset(local);
            }
            prepared.Add((item, slot));
        }
        var preview = string.Join(Environment.NewLine,
            prepared.Take(8).Select((x, i) => $"{i + 1}. {x.Item.Title} — " +
                (x.Slot.HasValue ? x.Slot.Value.ToString("yyyy-MM-dd HH:mm") : "Post now")));
        var extra = prepared.Count > 8 ? Environment.NewLine + $"...and {prepared.Count - 8} more" : "";
        var consent = $"SUBMIT {prepared.Count} TIKTOK VIDEO(S) IN ORDER?" +
            Environment.NewLine + Environment.NewLine + preview + extra +
            Environment.NewLine + Environment.NewLine +
            "ZeroVideo will upload and submit each video automatically, including " +
            "TikTok's 'Continue to post?' warning if it appears while content " +
            "checks are incomplete. Check your rights, captions, privacy and dates. " +
            "The app will click each final submit button only once per attempt. " +
            "An uncertain platform response will not trigger a retry.";
        if (MessageBox.Show(consent, "Confirm sequential TikTok publishing",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        IsBatchPublishing = true;
        var submitted = 0;
        var uploader = new StudioUploadService(_browser);
        try
        {
            for (var index = 0; index < prepared.Count; index++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var (item, slot) = prepared[index];
                PublishStatus = $"TikTok {index + 1}/{prepared.Count}: uploading {item.Title}";
                item.TikTokStatus = "Uploading";
                SavePublishingQueue();
                string result;
                try
                {
                    result = await uploader.PrepareAsync(Settings, "TikTok", item.VideoPath,
                        item.YouTubeTitle, item.YouTubeDescription, item.TikTokCaption,
                        item.ThumbnailPath, slot, message =>
                        {
                            Log(message);
                            // Progress is displayed but never used to initiate
                            // another submit or interact with TikTok controls.
                            Application.Current.Dispatcher.Invoke(() =>
                                PublishStatus = $"TikTok {index + 1}/{prepared.Count}: " + message);
                        }, _cts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    item.TikTokStatus = "Failed - review Studio";
                    SavePublishingQueue();
                    PublishStatus = $"TikTok {index + 1}/{prepared.Count} stopped: {ex.Message}";
                    Log(PublishStatus);
                    return;
                }
                if (result.StartsWith("TikTok: SUBMISSION_CONFIRMED", StringComparison.Ordinal))
                {
                    item.TikTokStatus = slot.HasValue ? "Scheduled" : "Published";
                    submitted++;
                }
                else if (result.StartsWith("TikTok: SUBMISSION_ATTEMPTED", StringComparison.Ordinal))
                {
                    item.TikTokStatus = "Submission attempted - check Studio";
                    submitted++;
                    // #216 commonly navigates away after a successful submit
                    // without a textual receipt. Allow the next DIFFERENT video
                    // only when the upload form has been left. No retries.
                    if (!result.Contains("left the upload page", StringComparison.OrdinalIgnoreCase))
                    {
                        SavePublishingQueue();
                        PublishStatus = $"TikTok batch paused after {index + 1}/{prepared.Count}: " +
                            result + " Verify the outcome before starting another batch.";
                        return;
                    }
                }
                else
                {
                    item.TikTokStatus = "Needs review";
                    SavePublishingQueue();
                    PublishStatus = $"TikTok batch stopped at {index + 1}/{prepared.Count}: " + result;
                    return;
                }
                item.IsChecked = false; // Completed batch item, not future ones.
                SavePublishingQueue();
                Log($"TikTok batch {index + 1}/{prepared.Count}: {item.TikTokStatus}. " +
                    "No repeat submit attempted.");
            }
            PublishStatus = $"TikTok batch finished: {submitted}/{prepared.Count} submission attempts. " +
                "Review TikTok Studio Posts to verify final platform status.";
        }
        catch (OperationCanceledException)
        {
            PublishStatus = $"TikTok batch canceled after {submitted}/{prepared.Count} attempts. " +
                "Inspect TikTok Studio before retrying.";
        }
        finally
        {
            IsBatchPublishing = false;
            PublishConsent = false;
            SavePublishingQueue();
        }
    }

    private async Task RetryCheckedMetadataAsync()
    {
        if (IsBatchPublishing) { PublishStatus = "Wait for the TikTok batch first."; return; }
        var chosen = PublishingQueue.Where(x => x.IsChecked).ToList();
        if (chosen.Count == 0)
        { PublishStatus = "Check the videos whose metadata you want to regenerate."; return; }
        for (var i = 0; i < chosen.Count; i++)
        {
            PublishStatus = $"Regenerating metadata {i + 1}/{chosen.Count}: {chosen[i].Title}";
            await TryGenerateMetadataForItemAsync(chosen[i], force: true);
        }
        PublishStatus = $"Metadata retry finished for {chosen.Count} checked video(s).";
    }

    private async Task TryGenerateMetadataForItemAsync(PublishQueueItem item, bool force)
    {
        if (!force && !string.IsNullOrWhiteSpace(item.TikTokCaption) &&
            !string.IsNullOrWhiteSpace(item.YouTubeTitle) &&
            !string.IsNullOrWhiteSpace(item.YouTubeDescription))
            return;
        item.MetadataStatus = "Generating";
        SavePublishingQueue();
        try
        {
            var subject = string.IsNullOrWhiteSpace(item.Description) ?
                item.Title : item.Description;
            var prompt = "Generate platform-specific accurate metadata for a FINISHED video. " +
                "Return ONLY one valid JSON object with response_type zerovideo_publish_v2; " +
                "youtube_title (include 1-2 relevant #hashtags, total <=100 characters), " +
                "youtube_description (detailed separate description with optional hashtags), " +
                "tiktok_caption (ONE combined engaging hook, short description, 3-6 hashtags). " +
                "Never invent facts, spam, make false promises, or use misleading clickbait. " +
                "Video title: " + item.Title + ". Subject: " + subject;
            var raw = await _chat.GenerateTextAsync(Project, Settings, prompt, Log, _cts.Token);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.GetProperty("response_type").GetString() != "zerovideo_publish_v2")
                throw new InvalidDataException("Unexpected metadata response_type.");
            var ytTitle = root.GetProperty("youtube_title").GetString()?.Trim() ?? "";
            var ytDescription = root.GetProperty("youtube_description").GetString()?.Trim() ?? "";
            var tikTok = root.GetProperty("tiktok_caption").GetString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(ytTitle) || ytTitle.Length > 100 ||
                string.IsNullOrWhiteSpace(ytDescription) || string.IsNullOrWhiteSpace(tikTok))
                throw new InvalidDataException("Incomplete or invalid platform metadata.");
            item.YouTubeTitle = ytTitle;
            item.YouTubeDescription = ytDescription;
            item.TikTokCaption = tikTok;
            item.Hashtags = string.Join(" ", tikTok.Split(' ',
                StringSplitOptions.RemoveEmptyEntries).Where(tag => tag.StartsWith("#")));
            item.MetadataStatus = "Ready";
            item.RaiseSeoChanged();
            if (ReferenceEquals(item, SelectedPublishItem))
            {
                YouTubeTitleText = ytTitle;
                YouTubeDescriptionText = ytDescription;
                TikTokCaptionText = tikTok;
            }
            SavePublishingQueue();
            Log("Publishing metadata automatically generated for both platforms: " + item.Title);
        }
        catch (OperationCanceledException)
        {
            item.MetadataStatus = "Retry needed";
            SavePublishingQueue();
            throw;
        }
        catch (Exception ex)
        {
            // Metadata failure must never discard a finished MP4 or stop
            // another Automation Shorts job. Retry can be initiated later.
            item.MetadataStatus = "Retry needed";
            SavePublishingQueue();
            Log("Publishing metadata could not be generated for '" + item.Title +
                "': " + ex.Message + ". Check it and use Retry Metadata.");
        }
    }

    private void QueueCompletedVideoFromBrowse(string path)
    {
        if (PublishingQueue.Any(x => x.VideoPath.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        var thumbnail = Path.Combine(Path.GetDirectoryName(path)!, "thumbnail.png");
        var item = new PublishQueueItem { VideoPath = path, Title = Path.GetFileNameWithoutExtension(path),
            ThumbnailPath = File.Exists(thumbnail) ? thumbnail : "" };
        TrackPublishingItem(item);
        PublishingQueue.Add(item);
        SavePublishingQueue();
        SelectedPublishItem = item;
    }

    private bool ValidatePublish()
    {
        if (!PublishConsent)
        {
            PublishStatus = "Review your account, content, privacy and rights, then check the authorization box.";
            return false;
        }
        if (!File.Exists(PublishFile) || !PublishFile.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            PublishStatus = "Select a valid exported MP4 first.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(PublishTitle))
        {
            PublishStatus = "Enter a video title.";
            return false;
        }
        if (MessageBox.Show($"Upload this video?\n\n{PublishTitle}\n{PublishFile}\n\nYou are authorizing a real platform upload.",
            "Confirm video upload", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;
        return true;
    }
}
