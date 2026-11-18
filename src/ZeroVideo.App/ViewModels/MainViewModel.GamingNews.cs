using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

/// <summary>
/// Manual one-click batch: discover, generate with the existing Shorts pipeline,
/// assign half-hour slots, and submit using the existing TikTok Studio uploader.
/// No polling, no per-story dialogs, no changes to the original Shorts or Publish controls.
/// </summary>
public sealed partial class MainViewModel
{
    private int _gamingNewsVideoCount = 48;
    private string _gamingNewsFocus = "";
    private string _gamingNewsChannelName = "";
    private string _gamingNewsScheduleStart = "";
    private string _gamingNewsStatus = "Ready. Configure the batch and select Generate & Schedule.";
    private string _gamingNewsMusicPath = "";
    private double _gamingNewsMusicDb = -15;
    private bool _gamingNewsMusicEnabled;
    private bool _gamingNewsUploadAutomatically = true;
    private int _gamingNewsGenerated;
    private int _gamingNewsScheduled;
    public int GamingNewsVideoCount { get => _gamingNewsVideoCount; set => Set(ref _gamingNewsVideoCount, Math.Clamp(value, 1, 96)); }
    public string GamingNewsFocus { get => _gamingNewsFocus; set => Set(ref _gamingNewsFocus, value); }
    public string GamingNewsChannelName { get => _gamingNewsChannelName; set => Set(ref _gamingNewsChannelName, value); }
    public string GamingNewsScheduleStart { get => _gamingNewsScheduleStart; set => Set(ref _gamingNewsScheduleStart, value); }
    public string GamingNewsStatus { get => _gamingNewsStatus; private set => Set(ref _gamingNewsStatus, value); }
    public string GamingNewsMusicPath { get => _gamingNewsMusicPath; set => Set(ref _gamingNewsMusicPath, value); }
    public double GamingNewsMusicDb { get => _gamingNewsMusicDb; set => Set(ref _gamingNewsMusicDb, Math.Clamp(value, -40, 0)); }
    public bool GamingNewsMusicEnabled { get => _gamingNewsMusicEnabled; set => Set(ref _gamingNewsMusicEnabled, value); }
    public bool GamingNewsUploadAutomatically { get => _gamingNewsUploadAutomatically; set => Set(ref _gamingNewsUploadAutomatically, value); }
    public int GamingNewsGenerated { get => _gamingNewsGenerated; private set => Set(ref _gamingNewsGenerated, value); }
    public int GamingNewsScheduled { get => _gamingNewsScheduled; private set => Set(ref _gamingNewsScheduled, value); }
    public ObservableCollection<double> GamingNewsDbOptions { get; } = [0,-5,-10,-15,-20,-25,-30,-35,-40];
    public ObservableCollection<int> GamingNewsCounts { get; } = [1,3,5,10,12,24,48,96];
    public ICommand GamingNewsChooseMusicCommand { get; private set; } = null!;
    public ICommand GamingNewsStartBatchCommand { get; private set; } = null!;

    private void InitializeGamingNewsCommands()
    {
        GamingNewsMusicPath = Settings.BackgroundMusicPath;
        GamingNewsMusicEnabled = !string.IsNullOrWhiteSpace(GamingNewsMusicPath);
        GamingNewsMusicDb = Settings.BackgroundMusicDb is >= -40 and <= 0
            ? Settings.BackgroundMusicDb : -15;
        GamingNewsChooseMusicCommand = new RelayCommand(() =>
        {
            var picker = new OpenFileDialog
            {
                Title = "Select background music for Gaming News",
                Filter = "Audio files|*.mp3;*.wav;*.m4a;*.flac|All files|*.*"
            };
            if (picker.ShowDialog() == true)
            {
                GamingNewsMusicPath = picker.FileName;
                GamingNewsMusicEnabled = true;
            }
        });
        GamingNewsStartBatchCommand = new AsyncCommand(() =>
            RunManagedJobAsync("Gaming News — One Click Batch", RunGamingNewsBatchAsync, publishing:true));
    }

    private static DateTime GamingNewsInitialSlot(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal,
                out var selected) || selected <= DateTime.Now.AddMinutes(30) ||
                selected.Minute % 30 != 0)
                throw new ArgumentException("First slot must be a future YYYY-MM-DD HH:00 or HH:30, at least 30 minutes ahead.");
            return selected;
        }
        var now = DateTime.Now.AddHours(4);
        return new DateTime(now.Year,now.Month,now.Day,now.Hour,0,0)
            .AddMinutes(now.Minute < 30 ? 30 : 60);
    }

    private static bool GamingNewsMatches(string headline, string focus)
    {
        if (string.IsNullOrWhiteSpace(focus)) return true;
        var normalized = System.Text.RegularExpressions.Regex.Replace(headline.ToLowerInvariant(),
            @"[^a-z0-9]+", " ");
        var requested = System.Text.RegularExpressions.Regex.Replace(focus.ToLowerInvariant(),
            @"[^a-z0-9]+", " ").Trim();
        if (normalized.Contains(requested, StringComparison.Ordinal)) return true;
        // Common alternative name for GTA VI.
        return (requested is "gta vi" or "gta 6" or "grand theft auto vi") &&
            System.Text.RegularExpressions.Regex.IsMatch(normalized,
                @"\b(gta\s*(vi|6)|grand theft auto\s*(vi|6))\b");
    }

    private async Task RunGamingNewsBatchAsync()
    {
        GamingNewsGenerated = 0;
        GamingNewsScheduled = 0;
        if (Settings.TextToSpeechEngine != "OmniVoice (Local)" ||
            !_omniVoice.IsInstalled ||
            !OmniVoiceProfiles.Any(x => x.Id == Settings.OmniVoiceProfileId))
        {
            GamingNewsStatus = "One-click narration needs your existing installed OmniVoice profile selected in Voice Studio.";
            return;
        }
        if (!Settings.OmniVoiceNonCommercialAcknowledged)
        {
            GamingNewsStatus = "OmniVoice weights are non-commercial. Use only in accordance with its license.";
            return;
        }
        if (GamingNewsMusicEnabled && !File.Exists(GamingNewsMusicPath))
        {
            GamingNewsStatus = "Selected background music is missing: " + GamingNewsMusicPath;
            return;
        }
        DateTime firstSlot;
        try { firstSlot = GamingNewsInitialSlot(GamingNewsScheduleStart); }
        catch (Exception ex) { GamingNewsStatus = ex.Message; return; }

        GamingNewsStatus = "Scanning gaming news feeds once (no auto-refresh)...";
        var discovery = new GamingNewsDiscoveryService();
        var candidates = await discovery.DiscoverAsync(96, Log, _cts.Token);
        var relevant = candidates.Where(x => GamingNewsMatches(x.Title,GamingNewsFocus))
            .Take(GamingNewsVideoCount).ToList();
        if (relevant.Count == 0)
        {
            GamingNewsStatus = "No verified recent headlines match this topic. No videos generated.";
            return;
        }
        if (relevant.Count < GamingNewsVideoCount)
            Log($"Gaming News: only {relevant.Count}/{GamingNewsVideoCount} distinct news headlines found. No invented topics.");
        var originalShortQueue = ShortQueue.ToList();
        var originalShortDuration = ShortDuration;
        var previousMusicPath = Settings.BackgroundMusicPath;
        var previousMusicDb = Settings.BackgroundMusicDb;
        var previousMusicEnabled = Project.Options.AddBackgroundMusic;
        var initialIds = PublishingQueue.Select(x => x.Id).ToHashSet();
        try
        {
            GamingNewsStatus = $"Creating {relevant.Count} vertical Shorts with existing voice and captions...";
            ShortQueue.Clear();
            foreach (var topic in relevant)
            {
                var channelName = GamingNewsChannelName.Trim();
                var opening = string.IsNullOrWhiteSpace(channelName)
                    ? "Hey guys! Here is the latest gaming news about " + topic.Title + "."
                    : "Hey guys! Welcome to " + channelName + "! Here is the latest news about " + topic.Title + ".";
                var detail = "Gaming news report: " + topic.Title +
                    ". Original report: " + topic.Url +
                    ". Publisher: " + topic.Publisher +
                    ". Publication date: " + topic.PublishedAt.ToString("yyyy-MM-dd") +
                    ". Verify claims from the cited source, do not fabricate details, " +
                    "use only topic-relevant visuals with appropriate rights. " +
                    "Make an original 9:16 English news explainer with burned-in Shorts captions. " +
                    "IMPORTANT SCRIPT FORMAT for this Gaming News video only: " +
                    "The very first spoken words of chapter 1 MUST be this natural channel greeting, " +
                    "verbatim (without quotation marks): " + opening + " " +
                    "After this introduction, immediately report the verified facts with smooth " +
                    "conversational transitions. Do not repeat the greeting in later chapters. " +
                    "The very last spoken sentence of the final chapter MUST be a short, engaging " +
                    "on-topic audience question or a factually justified cliffhanger, inviting comments. " +
                    "Do not fabricate upcoming announcements, teasers or unresolved stories for suspense. " +
                    "The greeting and ending MUST be part of spoken narration rather than captions, " +
                    "stage directions or a separate unspoken prompt.";
                ShortQueue.Add(new ShortQueueEntry { Idea = detail });
            }
            ShortDuration = 1;
            Settings.BackgroundMusicPath = GamingNewsMusicEnabled ? GamingNewsMusicPath : "";
            Settings.BackgroundMusicDb = GamingNewsMusicDb;
            Project.Options.AddBackgroundMusic = GamingNewsMusicEnabled;
            await RunShortsQueueAsync(); // Proven local TTS, 9:16, captions, rendering and checkpoints.
            var completed = ShortQueue.Where(x => x.Status == "Complete").ToList();
            GamingNewsGenerated = completed.Count;
            if (completed.Count == 0)
            {
                GamingNewsStatus = "No complete videos rendered; inspect Shorts recovery logs.";
                return;
            }
            var newItems = PublishingQueue.Where(x => !initialIds.Contains(x.Id) &&
                File.Exists(x.VideoPath)).ToList();
            var blocked = PublishingQueue.Where(x => x.ScheduledAt.HasValue)
                .Select(x => x.ScheduledAt!.Value.LocalDateTime).ToHashSet();
            var slot = firstSlot;
            foreach (var item in newItems)
            {
                while (blocked.Contains(slot)) slot = slot.AddMinutes(30);
                item.IsScheduled = true;
                item.ScheduleDateText = slot.ToString("yyyy-MM-dd HH:mm");
                item.ScheduledAt = new DateTimeOffset(slot);
                item.TikTokCaption = string.IsNullOrWhiteSpace(item.TikTokCaption)
                    ? item.Title + " #GamingNews #VideoGames"
                    : item.TikTokCaption;
                item.IsChecked = true;
                blocked.Add(slot);
                slot = slot.AddMinutes(30);
            }
            SavePublishingQueue();
            GamingNewsScheduled = newItems.Count;
            GamingNewsStatus = $"Generated {GamingNewsGenerated}, assigned {GamingNewsScheduled} half-hour TikTok slots.";
            // Pressing the one-click action with the auto-submit checkbox checked
            // explicitly authorizes this batch. Use the SAME StudioUploadService
            // as the original publishing tab, without modifying that workflow.
            // Never retry an uncertain TikTok submission.
            if (GamingNewsUploadAutomatically && newItems.Count > 0)
            {
                var uploader = new StudioUploadService(_browser);
                var sent = 0;
                for (var index = 0; index < newItems.Count; index++)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var item = newItems[index];
                    // Rendering a full batch can take hours. A time slot that
                    // has already passed cannot be submitted to TikTok Studio.
                    if (!item.ScheduledAt.HasValue ||
                        item.ScheduledAt.Value <= DateTimeOffset.Now.AddMinutes(25))
                    {
                        GamingNewsStatus = "Scheduling paused: a posting slot is no longer in the future. " +
                            "Completed MP4s remain in the Publish queue.";
                        break;
                    }
                    GamingNewsStatus = $"TikTok submission {index + 1}/{newItems.Count}: {item.Title}";
                    item.TikTokStatus = "Uploading";
                    SavePublishingQueue();
                    string outcome;
                    try
                    {
                        outcome = await uploader.PrepareAsync(
                            Settings, "TikTok", item.VideoPath,
                            item.YouTubeTitle, item.YouTubeDescription, item.TikTokCaption,
                            item.ThumbnailPath, item.ScheduledAt, Log, _cts.Token,
                            freshTikTokUploadPage: index > 0);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        item.TikTokStatus = "Failed - review Studio";
                        GamingNewsStatus = "TikTok stopped: " + ex.Message +
                            ". No automatic retry; check Studio first.";
                        SavePublishingQueue();
                        break;
                    }
                    if (outcome.StartsWith("TikTok: SUBMISSION_CONFIRMED",
                        StringComparison.Ordinal))
                    {
                        item.TikTokStatus = "Scheduled";
                        item.IsChecked = false;
                        sent++;
                    }
                    else if (outcome.StartsWith("TikTok: SUBMISSION_ATTEMPTED",
                        StringComparison.Ordinal))
                    {
                        item.TikTokStatus = "Submission attempted - check Studio";
                        item.IsChecked = false;
                        sent++;
                        SavePublishingQueue();
                        if (!outcome.Contains("left the upload page",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            GamingNewsStatus = "TikTok returned an uncertain submission. " +
                                "Stopped to prevent duplicates. Verify in Studio.";
                            break;
                        }
                    }
                    else
                    {
                        item.TikTokStatus = "Needs review";
                        GamingNewsStatus = "TikTok paused: " + outcome;
                        SavePublishingQueue();
                        break;
                    }
                    SavePublishingQueue();
                }
                if (sent == newItems.Count)
                    GamingNewsStatus = $"Finished: {GamingNewsGenerated} Shorts, {sent} TikTok " +
                        "submission attempts. Check TikTok Studio for final platform status.";
            }
        }
        finally
        {
            Settings.BackgroundMusicPath = previousMusicPath;
            Settings.BackgroundMusicDb = previousMusicDb;
            Project.Options.AddBackgroundMusic = previousMusicEnabled;
            ShortQueue.Clear();
            foreach (var entry in originalShortQueue) ShortQueue.Add(entry);
            ShortDuration = originalShortDuration;
        }
    }
}
