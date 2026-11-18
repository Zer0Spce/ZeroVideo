using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;
using ZeroVideo.App.Models;
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
    private string _gamingNewsTestVideoPath = "";
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
    public string GamingNewsTestVideoPath { get => _gamingNewsTestVideoPath; private set => Set(ref _gamingNewsTestVideoPath, value); }
    public int GamingNewsGenerated { get => _gamingNewsGenerated; private set => Set(ref _gamingNewsGenerated, value); }
    public int GamingNewsScheduled { get => _gamingNewsScheduled; private set => Set(ref _gamingNewsScheduled, value); }
    public ObservableCollection<double> GamingNewsDbOptions { get; } = [0,-5,-10,-15,-20,-25,-30,-35,-40];
    public ObservableCollection<int> GamingNewsCounts { get; } = [1,3,5,10,12,24,48,96];
    public ICommand GamingNewsChooseMusicCommand { get; private set; } = null!;
    public ICommand GamingNewsStartBatchCommand { get; private set; } = null!;
    public ICommand GamingNewsTestVideoCommand { get; private set; } = null!;
    public ICommand GamingNewsOpenTestVideoCommand { get; private set; } = null!;

    private void InitializeGamingNewsCommands()
    {
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
            RunManagedJobAsync("Gaming News — One Click Batch", () => RunGamingNewsBatchAsync(false), publishing:true));
        GamingNewsTestVideoCommand = new AsyncCommand(() =>
            RunManagedJobAsync("Gaming News — Test Video", () => RunGamingNewsBatchAsync(true)));
        GamingNewsOpenTestVideoCommand = new RelayCommand(() =>
        {
            if (File.Exists(GamingNewsTestVideoPath))
                Process.Start(new ProcessStartInfo(GamingNewsTestVideoPath) { UseShellExecute = true });
        });
    }

    private sealed class GamingNewsPreferences
    {
        public int VideoCount { get; set; } = 48;
        public string Focus { get; set; } = "";
        public string ChannelName { get; set; } = "";
        public string FirstSlot { get; set; } = "";
        public string MusicPath { get; set; } = "";
        public double MusicDb { get; set; } = -15;
        public bool MusicEnabled { get; set; }
        public bool AutoSubmit { get; set; } = true;
    }

    private static string GamingNewsPreferencesPath =>
        Path.Combine(AppPaths.DataDirectory, "gaming-news-batch-settings.json");

    private void LoadGamingNewsPreferences()
    {
        try
        {
            if (!File.Exists(GamingNewsPreferencesPath))
            {
                GamingNewsMusicPath = Settings.BackgroundMusicPath;
                GamingNewsMusicDb = Settings.BackgroundMusicDb is >= -40 and <= 0
                    ? Settings.BackgroundMusicDb : -15;
                GamingNewsMusicEnabled = !string.IsNullOrWhiteSpace(GamingNewsMusicPath);
                return;
            }
            var config = JsonSerializer.Deserialize<GamingNewsPreferences>(
                File.ReadAllText(GamingNewsPreferencesPath));
            if (config is null) return;
            GamingNewsVideoCount = config.VideoCount;
            GamingNewsFocus = config.Focus;
            GamingNewsChannelName = config.ChannelName;
            GamingNewsScheduleStart = config.FirstSlot;
            GamingNewsMusicPath = config.MusicPath;
            GamingNewsMusicDb = config.MusicDb;
            GamingNewsMusicEnabled = config.MusicEnabled;
            GamingNewsUploadAutomatically = config.AutoSubmit;
        }
        catch (Exception ex) { Log("Gaming News settings recovery: " + ex.Message); }
    }

    private void SaveGamingNewsPreferences()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var config = new GamingNewsPreferences
        {
            VideoCount = GamingNewsVideoCount,
            Focus = GamingNewsFocus,
            ChannelName = GamingNewsChannelName,
            FirstSlot = GamingNewsScheduleStart,
            MusicPath = GamingNewsMusicPath,
            MusicDb = GamingNewsMusicDb,
            MusicEnabled = GamingNewsMusicEnabled,
            AutoSubmit = GamingNewsUploadAutomatically
        };
        var temporary = GamingNewsPreferencesPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(config,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, GamingNewsPreferencesPath, true);
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

    private async Task<VideoProject> CreateGamingNewsShortProjectAsync(GamingNewsTopic article, int index)
    {
        const int sceneCount = 8;
        var channelName = GamingNewsChannelName.Trim();
        var opening = string.IsNullOrWhiteSpace(channelName)
            ? "Hey guys! Here's the latest gaming news! "
            : "Hey guys! Welcome to " + channelName + "! Here's the latest gaming news! ";
        var project = new VideoProject
        {
            Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + index.ToString("00"),
            Title = article.Title,
            Topic = article.Title,
            AspectRatio = "9:16",
            TargetMinutes = 1,
            Language = "English",
            Style = Style,
            Voice = Project.Voice,
            Sources = [article.Url],
            Options = new ProjectOptions
            {
                HowManyChapters = 1,
                ImagesPerChapter = 24, // 8 narration beats x 3 real-media shots. Never AI-generated images.
                SceneTransitions = true,
                GamingNewsSmoothOnly = true,
                GamingNewsPreserveImageDetail = true,
                TransitionStyle = "Crossfade",
                TransitionDurationSeconds = 0.28,
                AddMotion = true,
                MotionStyle = "Automatic",
                CinematicEffects = false,
                StyleAwareEffects = false,
                GenerateCaptions = true,
                BurnInBoldCaptions = true,
                GenerateMissingVisuals = false, // Gaming News never generates AI images.
                AutoRealPersonIdentity = false,
                AddBackgroundMusic = GamingNewsMusicEnabled,
                BackgroundMusicPath = GamingNewsMusicEnabled ? GamingNewsMusicPath : null,
                BackgroundMusicVolume = Math.Pow(10, GamingNewsMusicDb / 20)
            }
        };
        var metadata = new ProjectChapter
        {
            Number = 1,
            Title = "One-minute gaming news",
            Purpose = "One continuous 60-second gaming news report, not a documentary chapter.",
            TargetSeconds = 60
        };
        project.Chapters.Add(metadata);
        var prompt = "Write a SINGLE standalone 60-second vertical TikTok GAMING NEWS script. " +
            "This is NOT a long-form documentary. DO NOT create a master outline, seven chapters, " +
            "a multi-part series, or 10 scenes per chapter. " +
            "The entire video is ONE continuous narration with EXACTLY 8 visual beats. " +
            "Aim for 115-145 spoken words TOTAL, including the opening and closing, at a natural voiceover pace. " +
            "The first spoken words MUST be EXACTLY: " + opening +
            "Then report the news in an engaging, concise, accurate way. " +
            "End with ONE relevant viewer question, without invented claims or false cliffhangers. " +
            "Use the article title, source and summary strictly as evidence; do not make up facts " +
            "that are not supported by them. Be clear if evidence is thin. " +
            "Article headline: " + article.Title + "\nPublisher: " + article.Publisher +
            "\nPublished: " + article.PublishedAt.ToString("yyyy-MM-dd") +
            "\nOriginal source: " + article.Url +
            "\nRSS summary: " + article.Summary +
            "\nReturn ONE valid fenced JSON object for one ProjectChapter with keys " +
            "number (1), title, purpose, targetSeconds (60), scenes. " +
            "Exactly 8 scenes numbered 1-8; each scene has number, role, narration, " +
            "visualType, visualQuery, imagePrompt, shotDirection, targetSeconds (~7.5). " +
            "Each scene narration is a short continuous segment of the SAME news story. " +
            "Visual directions must identify the specific REAL game and subject named in the report. " +
            "Do not invent game imagery or request AI-generated artwork. Visuals will be sourced " +
            "from official publisher-supplied screenshots, not synthesized. " +
            "Keep 9:16, English, topic-specific visuals and burnt-in Shorts captions in the project. " +
            "No chapter-level narration field is needed.";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            _cts.Token.ThrowIfCancellationRequested();
            var result = await _chat.GenerateChapterAsync(project, metadata, Settings, prompt, Log, _cts.Token);
            var narration = result.Narration.Trim();
            var words = narration.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var firstScene = result.Scenes.FirstOrDefault()?.Narration.TrimStart() ?? "";
            var lastScene = result.Scenes.LastOrDefault()?.Narration.TrimEnd() ?? "";
            if (result.Scenes.Count == sceneCount && words is >= 105 and <= 155 &&
                firstScene.StartsWith(opening.Trim(), StringComparison.OrdinalIgnoreCase) &&
                lastScene.EndsWith("?", StringComparison.Ordinal))
            {
                result.TargetSeconds = 60;
                result.Number = 1;
                result.Narration = narration;
                for (var i = 0; i < result.Scenes.Count; i++)
                {
                    result.Scenes[i].Number = i + 1;
                    result.Scenes[i].TargetSeconds = 60.0 / sceneCount;
                }
                project.Chapters[0] = result;
                return project;
            }
            Log($"Gaming News script invalid: {result.Scenes.Count} scenes, {words} words, " +
                "opening/outro mismatch. Retrying short-form format.");
            prompt += "\nCORRECTION: Exactly EIGHT scenes, 105-155 words in total, " +
                "first spoken line verbatim as supplied, last scene must end with a question mark. " +
                "Never return a long-form outline or seven chapters.";
        }
        throw new InvalidDataException("Gaming News script failed the 60-second structure checks.");
    }

    private async Task RunGamingNewsBatchAsync(bool testOnly)
    {
        GamingNewsGenerated = 0;
        GamingNewsScheduled = 0;
        if (testOnly) GamingNewsTestVideoPath = "";
        SaveGamingNewsPreferences();
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
        try { firstSlot = testOnly ? DateTime.MaxValue : GamingNewsInitialSlot(GamingNewsScheduleStart); }
        catch (Exception ex) { GamingNewsStatus = ex.Message; return; }

        GamingNewsStatus = "Scanning gaming news feeds once (no auto-refresh)...";
        var discovery = new GamingNewsDiscoveryService();
        var candidates = await discovery.DiscoverAsync(96, Log, _cts.Token);
        var relevant = candidates.Where(x => GamingNewsMatches(x.Title,GamingNewsFocus))
            .Take(testOnly ? 1 : GamingNewsVideoCount).ToList();
        if (relevant.Count == 0)
        {
            GamingNewsStatus = "No verified recent headlines match this topic. No videos generated.";
            return;
        }
        if (!testOnly && relevant.Count < GamingNewsVideoCount)
            Log($"Gaming News: only {relevant.Count}/{GamingNewsVideoCount} distinct news headlines found. No invented topics.");
        // Gaming News is a genuine 60-second format: NEVER call the legacy
        // Shorts queue (it still uses long-form outline/chapter prompts).
        var originalProject = Project;
        var originalProjectDirectory = ProjectDirectory;
        var originalSettings = Settings;
        Settings = JsonSerializer.Deserialize<ZeroVideoSettings>(
            JsonSerializer.Serialize(originalSettings))
            ?? throw new InvalidDataException("Could not isolate Gaming News settings.");
        var initialIds = PublishingQueue.Select(x => x.Id).ToHashSet();
        try
        {
            Settings.BackgroundMusicPath = GamingNewsMusicEnabled ? GamingNewsMusicPath : "";
            Settings.BackgroundMusicDb = GamingNewsMusicDb;
            // Gaming News uses only the retrieved genuine photos/videos.
            // Disable LivePortrait/AI animation in this private settings copy
            // without changing the user's ordinary Shorts/Longform settings.
            Settings.AiImageAnimationEngine = "Off";
            GamingNewsStatus = $"Producing {relevant.Count} real 60-second gaming Shorts...";
            foreach (var (topic, index) in relevant.Select((item, i) => (item, i)))
            {
                _cts.Token.ThrowIfCancellationRequested();
                GamingNewsStatus = $"Writing a 60-second script {index+1}/{relevant.Count}: {topic.Title}";
                var project = await CreateGamingNewsShortProjectAsync(topic, index);
                Project = project;
                ProjectDirectory = await _store.SaveAsync(project, Settings.ProjectsRoot);

                GamingNewsStatus = $"Finding official game screenshots {index+1}/{relevant.Count}...";
                try
                {
                    var official = await new OfficialGamingMediaService().AcquireAsync(
                        project, GamingNewsFocus, topic.Summary, _browser, Settings, Log, _cts.Token);
                    await _store.SaveAsync(project, Settings.ProjectsRoot);
                    GamingNewsStatus = $"Rendering real game media {index+1}/{relevant.Count}...";
                    var result = await _pipeline.RunAsync(project, Settings, Log,
                        new Progress<double>(x => Progress = x), _cts.Token);
                    if (!File.Exists(result))
                        throw new InvalidDataException("Render returned no MP4.");
                    var mediaFinishing = new OfficialGamingMediaService();
                    await mediaFinishing.BurnSmallCreditAsync(
                        result, official.Credit, Settings, _runner, Log, _cts.Token);
                    await mediaFinishing.AddNarrationBreathingRoomAsync(
                        result, Settings, _runner, Log, _cts.Token);
                    if (await _ffmpeg.ProbeDurationAsync(result, Settings, _cts.Token) is var seconds &&
                        (seconds < 38 || seconds > 78))
                        throw new InvalidDataException(
                            $"Gaming Short is {seconds:0.0}s including 3 seconds of breathing room " +
                            "(expected roughly 60 seconds plus lead-in/outro).");
                    if (!testOnly)
                    {
                        QueueCompletedVideo(result);
                        var queued = PublishingQueue.FirstOrDefault(x =>
                            x.VideoPath.Equals(result, StringComparison.OrdinalIgnoreCase));
                        if (queued is not null)
                            await TryGenerateMetadataForItemAsync(queued, force:false);
                    }
                    else
                        GamingNewsTestVideoPath = result;
                    GamingNewsGenerated++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log($"Gaming News video {index+1} failed: {ex.Message}");
                    // Never add incomplete MP4s to Publishing.
                    if (testOnly) throw;
                    continue;
                }
            }

            if (GamingNewsGenerated == 0)
            {
                GamingNewsStatus = "No 60-second gaming videos completed. See logs.";
                return;
            }
            var newItems = PublishingQueue.Where(x => !initialIds.Contains(x.Id) &&
                File.Exists(x.VideoPath)).ToList();
            if (testOnly)
            {
                GamingNewsStatus = string.IsNullOrWhiteSpace(GamingNewsTestVideoPath)
                    ? "Test render finished but no output MP4 was found."
                    : "60-second preview ready. Open Test Video to watch; no TikTok scheduling or uploading.";
                return;
            }
            var blocked = PublishingQueue.Where(x => x.ScheduledAt.HasValue)
                .Select(x => x.ScheduledAt!.Value.LocalDateTime).ToHashSet();
            // Auto-selected times are provisional until production is complete.
            // Rendering 48 clips may take hours, so start in the future.
            var slot = string.IsNullOrWhiteSpace(GamingNewsScheduleStart)
                ? GamingNewsInitialSlot("")
                : firstSlot;
            if (slot <= DateTime.Now.AddMinutes(30))
            {
                GamingNewsStatus = "The requested first posting time passed during rendering. " +
                    "Videos remain saved; enter a later time and resume scheduling.";
                return;
            }
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
            Settings = originalSettings;
            Project = originalProject;
            ProjectDirectory = originalProjectDirectory;
        }
    }
}
