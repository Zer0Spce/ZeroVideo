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
    public string TikTokStatus { get; set; } = "Ready";
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
    private PublishQueueItem? _selectedPublishItem;
    private int _publishIntervalHours = 6;
    private string _publishFirstSlot = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd HH:mm");
    public ObservableCollection<int> PublishIntervals { get; } = [6, 12, 24, 48];
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
    private string _publishDestination = "YouTube";
    public string PublishDestination { get => _publishDestination; set { if (Set(ref _publishDestination, value)) LoadSelectedCheckpoint(); } }
    public ObservableCollection<string> PublishDestinations { get; } = ["YouTube", "TikTok"];
    public ICommand RemovePublishQueueCommand { get; private set; } = null!;

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
                    TrackPublishingItem(item);
                    PublishingQueue.Add(item);
                }
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
        if (!DateTime.TryParseExact(PublishFirstSlot, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var first) || first <= DateTime.Now)
        { PublishStatus = "Choose a future first slot: YYYY-MM-DD HH:MM (computer local time)."; return; }
        var interval = PublishIntervalHours;
        if (!PublishIntervals.Contains(interval)) { PublishStatus = "Choose a valid interval."; return; }
        var ready = PublishingQueue.Where(x => x.YouTubeStatus == "Ready" && x.IsScheduled).ToList();
        if (ready.Count == 0) { PublishStatus = "No ready videos to schedule."; return; }
        var slot = new DateTimeOffset(first);
        foreach (var item in ready)
        {
            item.ScheduledAt = slot;
            item.ScheduleDateText = slot.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            slot = slot.AddHours(interval);
        }
        SavePublishingQueue();
        SelectedPublishItem = ready[0];
        PublishStatus = $"Assigned {ready.Count} schedule slots every {interval} hours. Review each in Studio.";
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

        BrowserPublishCommand = new AsyncCommand(async () =>
        {
            if (!File.Exists(PublishFile)) { PublishStatus = "Select a queued MP4 first."; return; }
            if (!PublishConsent) { PublishStatus = "Review the content and check the authorization box first."; return; }
            var youtube = PublishDestination == "YouTube";
            if (youtube && (string.IsNullOrWhiteSpace(YouTubeTitleText) || YouTubeTitleText.Length > 100))
            { PublishStatus = "YouTube title including hashtags must be 1-100 characters."; return; }
            if (!youtube && string.IsNullOrWhiteSpace(TikTokCaptionText))
            { PublishStatus = "Generate or enter the TikTok caption first."; return; }
            var site = youtube ? "YouTube Studio" : "TikTok Studio";
            var warning = youtube
                ? "Open YouTube Studio for this video? Final YouTube submission still requires manual review."
                : "AUTOMATIC TIKTOK SUBMISSION\n\n" +
                  "ZeroVideo will upload this selected video, set its schedule when requested, then CLICK TikTok's final " +
                  (SelectedPublishItem?.IsScheduled == true ? "Schedule" : "Post") +
                  " button automatically, without a second confirmation in TikTok Studio.\n\n" +
                  (SelectedPublishItem?.IsScheduled == true
                    ? "Scheduled local time: " + SelectedPublishItem.ScheduleDateText + "\n\n" :
                      "This video is currently set to post immediately.\n\n") +
                  "Only continue if you authorize this video to be submitted now. Check the caption, privacy, and rights first.\n" +
                  "If TikTok's receipt is unclear, ZeroVideo will not retry automatically.";
            if (MessageBox.Show(warning, youtube ? "Prepare YouTube publishing" : "Confirm automatic TikTok publishing",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                var uploader = new StudioUploadService(_browser);
                PublishStatus = "Preparing " + site + " upload with Playwright...";
                // The scheduled date shown in the queue editor is authoritative.
                // Never silently fall back to a stale ScheduledAt value if editing
                // failed to update the stored model or the typed value is invalid.
                DateTimeOffset? requestedSlot = null;
                if (SelectedPublishItem?.IsScheduled == true)
                {
                    var typed = SelectedPublishItem.ScheduleDateText.Trim();
                    if (!DateTime.TryParseExact(typed, "yyyy-MM-dd HH:mm",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var chosen)
                        || chosen <= DateTime.Now)
                    {
                        PublishStatus = "Invalid schedule: use a future YYYY-MM-DD HH:MM in the visible video row.";
                        return;
                    }
                    requestedSlot = new DateTimeOffset(chosen);
                    SelectedPublishItem.ScheduledAt = requestedSlot;
                    SavePublishingQueue();
                }
                else if (DateTime.TryParseExact(PublishSchedule, "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var fallback)
                    && fallback > DateTime.Now)
                {
                    requestedSlot = new DateTimeOffset(fallback);
                }
                Log("Browser publishing: exact schedule passed to " + site + ": " +
                    (requestedSlot?.ToString("yyyy-MM-dd HH:mm zzz") ?? "no scheduled slot") +
                    "; visible queue input=" + (SelectedPublishItem?.ScheduleDateText ?? "(none)"));
                var result = await uploader.PrepareAsync(Settings, PublishDestination, PublishFile,
                    YouTubeTitleText, YouTubeDescriptionText, TikTokCaptionText, PublishThumbnail,
                    requestedSlot, message =>
                    {
                        Log(message);
                        Application.Current.Dispatcher.Invoke(() => PublishStatus = message);
                    }, _cts.Token);
                if (!youtube && SelectedPublishItem is { } item &&
                    result.StartsWith("TikTok: SUBMISSION_", StringComparison.Ordinal))
                {
                    var confirmed = result.StartsWith("TikTok: SUBMISSION_CONFIRMED",
                        StringComparison.Ordinal);
                    item.TikTokStatus = confirmed
                        ? (requestedSlot.HasValue ? "Scheduled" : "Published")
                        : "Submission attempted - check Studio";
                    PublishCheckpointStage = confirmed ? "Platform confirmed" : "Upload accepted";
                    PublishCheckpointNote = confirmed
                        ? "TikTok showed explicit confirmation after final submission."
                        : "Final TikTok action clicked once. Outcome not confirmed. Verify in TikTok Studio Posts before retrying.";
                    SavePublishingQueue();
                }
                else
                {
                    PublishCheckpointStage = "Video selected";
                }
                SaveSelectedCheckpoint();
                PublishStatus = result;
            }
            catch (Exception ex) { PublishStatus = "Browser publishing: " + ex.Message; }
            finally { PublishConsent = false; }
        });

        OpenYouTubeStudioCommand = new AsyncCommand(async () =>
        {
            try { await _browser.OpenManualAsync(Settings, "https://studio.youtube.com/"); PublishStatus = "YouTube Studio opened. Sign in and use Create > Upload videos. Review metadata and scheduling."; }
            catch (Exception ex) { PublishStatus = "YouTube Studio: " + ex.Message; }
        });
        OpenTikTokStudioCommand = new AsyncCommand(async () =>
        {
            try { await _browser.OpenManualAsync(Settings, "https://www.tiktok.com/tiktokstudio/upload"); PublishStatus = "TikTok Studio opened. Sign in and review cover and scheduling controls."; }
            catch (Exception ex) { PublishStatus = "TikTok Studio: " + ex.Message; }
        });
        GeneratePublishMetadataCommand = new AsyncCommand(async () =>
        {
            if (SelectedPublishItem is null) { PublishStatus = "Select a queued video first."; return; }
            try
            {
                var item = SelectedPublishItem;
                PublishStatus = "Generating SEO metadata in ChatGPT...";
                var subject = string.IsNullOrWhiteSpace(item.Description) ? item.Title : item.Description;
                var prompt = "Generate platform-specific accurate metadata for a finished video. Return ONLY one valid JSON object with " +
                    "response_type zerovideo_publish_v2, youtube_title (one string including 1-2 relevant #hashtags, total <=100 characters), " +
                    "youtube_description (separate detailed description and optional relevant hashtags), " +
                    "tiktok_caption (ONE combined string: compelling hook, short description, 3-6 relevant hashtags). " +
                    "Avoid false facts, spam, misleading clickbait, promises of views. " +
                    "Video title: " + item.Title + ". Subject: " + subject;
                var raw = await _chat.GenerateTextAsync(Project, Settings, prompt, Log, _cts.Token);
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.GetProperty("response_type").GetString() != "zerovideo_publish_v2")
                    throw new InvalidDataException("Unexpected metadata response.");
                var youtubeTitle = root.GetProperty("youtube_title").GetString()?.Trim() ?? "";
                var youtubeDescription = root.GetProperty("youtube_description").GetString()?.Trim() ?? "";
                var tiktokCaption = root.GetProperty("tiktok_caption").GetString()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(youtubeTitle) || string.IsNullOrWhiteSpace(youtubeDescription) ||
                    string.IsNullOrWhiteSpace(tiktokCaption) || youtubeTitle.Length > 100)
                    throw new InvalidDataException("Invalid metadata: YouTube title must be 1-100 characters including hashtags, and both descriptions must be nonempty. Regenerate.");
                item.YouTubeTitle = youtubeTitle;
                item.YouTubeDescription = youtubeDescription;
                item.TikTokCaption = tiktokCaption;
                item.Hashtags = string.Join(" ", tiktokCaption.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(tag => tag.StartsWith("#")));
                item.RaiseSeoChanged();
                YouTubeTitleText = youtubeTitle;
                YouTubeDescriptionText = youtubeDescription;
                TikTokCaptionText = tiktokCaption;
                SavePublishingQueue();
                Raise(nameof(PublishingQueue));
                PublishStatus = "YouTube and TikTok metadata ready in separate fields. Review before publishing.";
            }
            catch (Exception ex) { PublishStatus = "Metadata generation: " + ex.Message; }
        });

        SchedulePublishQueueCommand = new RelayCommand(SchedulePublishingQueue);
        RemovePublishQueueCommand = new RelayCommand(() =>
        {
            if (SelectedPublishItem is null) return;
            PublishingQueue.Remove(SelectedPublishItem);
            SelectedPublishItem = null;
            SavePublishingQueue();
        });
        BrowsePublishFileCommand = new RelayCommand(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "MP4 videos|*.mp4", CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            PublishFile = dialog.FileName;
            PublishTitle = Path.GetFileNameWithoutExtension(dialog.FileName);
            QueueCompletedVideoFromBrowse(dialog.FileName);
        });
        PublishYouTubeCommand = new AsyncCommand(async () =>
        {
            if (!ValidatePublish()) return;
            try
            {
                DateTimeOffset? scheduled = null;
                if (!string.IsNullOrWhiteSpace(PublishSchedule))
                {
                    if (!DateTimeOffset.TryParse(PublishSchedule, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var date))
                        throw new FormatException("Schedule format invalid. Use e.g. 2026-11-01 18:00 (local time).");
                    scheduled = date;
                }
                if (YouTubeTitleText.Length > 100 || string.IsNullOrWhiteSpace(YouTubeTitleText)) throw new InvalidDataException("YouTube title plus hashtags must be 1-100 characters.");
                PublishStatus = "Uploading to YouTube...";
                PublishStatus = await _publishing.UploadYouTubeAsync(PublishFile, YouTubeTitleText, YouTubeDescriptionText,
                    YouTubeAccessToken, YouTubePrivacy, scheduled, _cts.Token);
                if (SelectedPublishItem is not null) { SelectedPublishItem.YouTubeStatus = scheduled.HasValue ? "Scheduled" : "Uploaded"; SavePublishingQueue(); Raise(nameof(PublishingQueue)); }
            }
            catch (Exception ex) { PublishStatus = "YouTube: " + ex.Message; }
            finally { PublishConsent = false; }
        });
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
