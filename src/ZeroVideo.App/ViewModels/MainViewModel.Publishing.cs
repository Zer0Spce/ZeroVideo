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
    public ObservableCollection<int> SmartPostsPerDayOptions { get; } = [1,2,3,4,5,6,7,8,9,10];
    private int _smartPostsPerDay = 3;
    public int SmartPostsPerDay
    {
        get => _smartPostsPerDay;
        set => Set(ref _smartPostsPerDay, Math.Clamp(value,1,10));
    }

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
            PublishStatus = "Wait for the current TikTok publishing batch first.";
            return;
        }
        var chosen = PublishingQueue.Where(x => x.IsChecked).ToList();
        if (chosen.Count == 0)
        {
            PublishStatus = "Check the videos to plan, or use Select All for every available video.";
            return;
        }

        var postsPerDay = SmartPostsPerDay;
        if (postsPerDay < 1 || postsPerDay > 10)
        {
            PublishStatus = "Choose between 1 and 10 posts per day.";
            return;
        }
        // Start tomorrow so the first day's hours are never in the past.
        // The 20-day cap stays inside the one-month range that has actually
        // worked in this user's TikTok Studio, without promising TikTok
        // will accept every slot.
        const int maxDays = 20;
        var neededDays = (chosen.Count + postsPerDay - 1) / postsPerDay;
        if (neededDays > maxDays)
        {
            PublishStatus = $"{chosen.Count} videos at {postsPerDay}x/day need " +
                $"{neededDays} days, which exceeds the safe {maxDays}-day plan window. " +
                "Select fewer videos or increase posts per day.";
            return;
        }

        var unsuitable = chosen.FirstOrDefault(x =>
            x.TikTokStatus.Equals("Published",StringComparison.OrdinalIgnoreCase) ||
            x.TikTokStatus.Equals("Scheduled",StringComparison.OrdinalIgnoreCase) ||
            x.TikTokStatus.StartsWith("Submission attempted",StringComparison.OrdinalIgnoreCase));
        if (unsuitable is not null)
        {
            PublishStatus = "Uncheck videos already posted/scheduled or with an uncertain " +
                "submission before creating a new Smart Schedule: " + unsuitable.Title;
            return;
        }

        var now = DateTime.Now;
        var startDate = now.Date.AddDays(1);
        var reserved = PublishingQueue.Where(x => !x.IsChecked && x.IsScheduled &&
            x.ScheduledAt.HasValue).Select(x => x.ScheduledAt!.Value.LocalDateTime).ToList();
        try
        {
            PublishStatus = $"AI is planning {chosen.Count} videos at {postsPerDay}x/day " +
                $"across {neededDays} calendar day(s)...";
            // Only a compact sample is required for AI's daily time-of-day
            // recommendations. The actual distribution is deterministic so
            // it always respects the requested frequency even with many videos.
            static string SafeTitle(string value)
            {
                var clean = (value ?? "").Replace('\r',' ').Replace('\n',' ').Trim();
                return clean[..Math.Min(90,clean.Length)];
            }
            var titles = string.Join("\n",chosen.Take(24).Select((x,i) =>
                $"{i+1}. {SafeTitle(x.Title)}"));
            var prompt = "Suggest practical local posting TIMES of day for a TikTok short-form video channel. " +
                $"You must suggest EXACTLY {postsPerDay} different times for EACH day, in chronological order, " +
                "using general viewer engagement heuristics, never falsely claiming account analytics. " +
                $"Local timezone is {TimeZoneInfo.Local.Id}. " +
                "Consider the themes in this sample of titles, evening peak viewing and reasonable spacing. " +
                "Use 24-hour HH:mm times between 08:00 and 22:30 inclusive, on 5-minute increments, " +
                "with at least 60 minutes between times. " +
                "Do not specify calendar dates. The app will distribute the entire queue into " +
                "consecutive days and assign these recommended times in queue order. " +
                "Return ONLY one JSON object with schema " +
                "{\"response_type\":\"zerovideo_daily_slots_v1\",\"times\":[\"12:00\",\"18:00\"]}. " +
                "The example is illustrative; your times array MUST contain exactly " +
                postsPerDay + " values. Do not include commentary or markdown. " +
                "Do not claim the times will go viral. Video titles:\n" + titles;
            var raw = await _chat.GenerateTextAsync(new ZeroVideo.App.Models.VideoProject
            {
                Title = "TikTok daily smart schedule",
                Topic = "Recommended local times for TikTok posts"
            },Settings,prompt,Log,_cts.Token);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.GetProperty("response_type").GetString() != "zerovideo_daily_slots_v1")
                throw new InvalidDataException("AI returned the wrong daily schedule format.");
            var jsonTimes = root.GetProperty("times");
            if (jsonTimes.ValueKind != JsonValueKind.Array ||
                jsonTimes.GetArrayLength() != postsPerDay)
                throw new InvalidDataException($"AI did not provide exactly {postsPerDay} times per day.");
            var dailyTimes = new List<TimeSpan>();
            foreach(var record in jsonTimes.EnumerateArray())
            {
                var value = record.GetString() ?? "";
                if (!TimeSpan.TryParseExact(value,"hh\\:mm",CultureInfo.InvariantCulture,out var time) ||
                    time < TimeSpan.FromHours(8) || time > new TimeSpan(22,30,0) ||
                    time.Minutes%5 != 0)
                    throw new InvalidDataException("AI suggested an invalid HH:mm posting time: " + value);
                if (dailyTimes.Count > 0 && time < dailyTimes[^1]+TimeSpan.FromHours(1))
                    throw new InvalidDataException("AI suggested duplicate or too-close daily times.");
                dailyTimes.Add(time);
            }

            var planned = new List<DateTime>();
            for(var i=0; i<chosen.Count; i++)
            {
                var day = i/postsPerDay;
                var timeIndex = i%postsPerDay;
                var when = startDate.AddDays(day).Add(dailyTimes[timeIndex]);
                if (when <= now.AddHours(2) || when > now.AddDays(21))
                    throw new InvalidDataException("A proposed day exceeds the tested TikTok scheduling window.");
                if (reserved.Any(slot => Math.Abs((slot-when).TotalMinutes) < 60))
                    throw new InvalidDataException("A proposed time conflicts with an already scheduled video: " +
                        when.ToString("yyyy-MM-dd HH:mm") + ". Change checked videos or try another frequency.");
                planned.Add(when);
            }

            var previewLines = chosen.Zip(planned,(item,when)=>
                $"{when:ddd MMM dd, HH:mm} — {SafeTitle(item.Title)}").ToList();
            var preview = string.Join(Environment.NewLine,previewLines.Take(18));
            if(previewLines.Count>18)
                preview += Environment.NewLine + $"...and {previewLines.Count-18} more videos";
            var scheduleMessage = $"SMART SCHEDULE — {postsPerDay}x PER DAY\n" +
                $"{chosen.Count} videos across {neededDays} days, starting {startDate:yyyy-MM-dd}.\n\n" +
                preview + "\n\nAI suggests daily time windows based on content themes and " +
                "general engagement patterns, NOT actual account analytics. " +
                "Dates are distributed in queue order, with up to " +
                postsPerDay + " posts per day. This only fills schedule dates; it does NOT post. " +
                "Apply these times?";
            if(MessageBox.Show(scheduleMessage,"Review Smart Schedule",
                MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)
            {
                PublishStatus = "Smart Schedule canceled. Existing dates unchanged.";
                return;
            }

            // All validation happens before any queue changes: no partial plans.
            for(var i=0;i<chosen.Count;i++)
            {
                var item=chosen[i];
                item.IsScheduled = true;
                item.ScheduleDateText = planned[i].ToString(
                    "yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture);
                item.ScheduledAt = new DateTimeOffset(planned[i]);
            }
            SavePublishingQueue();
            PublishStatus = $"Smart Schedule applied {chosen.Count} videos at " +
                $"{postsPerDay}x/day across {neededDays} days. Review before TikTok publishing.";
            Log(PublishStatus);
        }
        catch(OperationCanceledException)
        {
            PublishStatus = "Smart Schedule canceled; previous dates preserved.";
        }
        catch(Exception ex)
        {
            PublishStatus = "Smart Schedule could not make a valid plan: " + ex.Message;
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
