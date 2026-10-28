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
    public DateTimeOffset? ScheduledAt { get; set; }
    public string YouTubeStatus { get; set; } = "Ready";
    public string TikTokStatus { get; set; } = "Ready";
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
            PublishTitle = value.Title;
            PublishDescription = value.Description;
            PublishHashtags = value.Hashtags;
            PublishSchedule = value.ScheduledAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
            PublishThumbnail = value.ThumbnailPath;
        }
    }
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
    public ICommand RemovePublishQueueCommand { get; private set; } = null!;

    private void LoadPublishingQueue()
    {
        try
        {
            if (!File.Exists(QueueFile)) return;
            foreach (var item in JsonSerializer.Deserialize<List<PublishQueueItem>>(File.ReadAllText(QueueFile)) ?? [])
                if (File.Exists(item.VideoPath) && !PublishingQueue.Any(x => x.VideoPath.Equals(item.VideoPath, StringComparison.OrdinalIgnoreCase)))
                    PublishingQueue.Add(item);
        }
        catch (Exception ex) { Log("Publishing queue could not be loaded: " + ex.Message); }
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
        var ready = PublishingQueue.Where(x => x.YouTubeStatus == "Ready").ToList();
        if (ready.Count == 0) { PublishStatus = "No ready videos to schedule."; return; }
        var slot = new DateTimeOffset(first);
        foreach (var item in ready)
        {
            item.ScheduledAt = slot;
            slot = slot.AddHours(interval);
        }
        SavePublishingQueue();
        SelectedPublishItem = ready[0];
        PublishStatus = $"Assigned {ready.Count} YouTube schedule slots every {interval} hours. Review and upload each video to confirm with YouTube.";
    }

    private readonly PublishingService _publishing = new();
    private string _publishFile = "";
    private string _publishTitle = "";
    private string _publishDescription = "";
    private string _youtubePrivacy = "private";
    private string _tikTokPrivacy = "SELF_ONLY";
    private string _publishSchedule = "";
    private bool _publishConsent;
    private string _publishStatus = "Select an exported MP4, enter authorized credentials and review before posting.";
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
                var prompt = "Generate accurate, curiosity-driven SEO metadata for a finished YouTube Shorts / TikTok video. " +
                    "Return ONLY valid JSON with response_type zerovideo_publish_v1 and string properties title, description, hashtags. " +
                    "Hashtags is a space-separated string of 3-6 relevant #tags. No fabricated facts, clickbait or guaranteed views. " +
                    "Title maximum 100 characters. Video title: " + item.Title + ". Subject: " + subject;
                var raw = await _chat.GenerateTextAsync(Project, Settings, prompt, Log, _cts.Token);
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.GetProperty("response_type").GetString() != "zerovideo_publish_v1")
                    throw new InvalidDataException("Unexpected metadata response.");
                var title = root.GetProperty("title").GetString()?.Trim() ?? "";
                var description = root.GetProperty("description").GetString()?.Trim() ?? "";
                var tags = root.GetProperty("hashtags").GetString()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
                    throw new InvalidDataException("ChatGPT returned empty metadata.");
                item.Title = title.Length > 100 ? title[..100] : title;
                item.Description = description;
                item.Hashtags = tags;
                PublishTitle = item.Title;
                PublishDescription = item.Description;
                PublishHashtags = item.Hashtags;
                SavePublishingQueue();
                Raise(nameof(PublishingQueue));
                PublishStatus = "SEO metadata ready. Review before publishing; views are not guaranteed.";
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
                PublishStatus = "Uploading to YouTube...";
                PublishStatus = await _publishing.UploadYouTubeAsync(PublishFile, PublishTitle, PublishDescription,
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
                PublishStatus = await _publishing.UploadTikTokAsync(PublishFile, PublishTitle,
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
