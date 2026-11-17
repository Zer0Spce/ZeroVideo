using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Models;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

/// <summary>
/// Isolated one-tab production workflow: ideas -> script -> voice -> Pixabay -> FFmpeg.
/// Does not change legacy Shorts, Long Form or Publishing implementations.
/// </summary>
public sealed partial class MainViewModel
{
    private string _youTubeFlowNiche = "Documentary facts";
    private int _youTubeFlowIdeaCount = 6;
    private VideoIdea? _youTubeFlowSelectedIdea;
    private string _youTubeFlowTitle = "";
    private string _youTubeFlowScript = "";
    private int _youTubeFlowMinutes = 5;
    private string _youTubeFlowRatio = "16:9";
    private string _youTubeFlowStatus = "Enter a niche or paste a script to begin.";
    private string _youTubeFlowOutput = "";

    public ObservableCollection<int> YouTubeFlowIdeaCounts { get; } = [3, 6, 10];
    public ObservableCollection<int> YouTubeFlowDurations { get; } = [1, 3, 5, 10, 15];
    public ObservableCollection<VideoIdea> YouTubeFlowIdeas { get; } = [];
    public string YouTubeFlowNiche { get => _youTubeFlowNiche; set => Set(ref _youTubeFlowNiche, value); }
    public int YouTubeFlowIdeaCount { get => _youTubeFlowIdeaCount; set => Set(ref _youTubeFlowIdeaCount, value); }
    public VideoIdea? YouTubeFlowSelectedIdea { get => _youTubeFlowSelectedIdea; set => Set(ref _youTubeFlowSelectedIdea, value); }
    public string YouTubeFlowTitle { get => _youTubeFlowTitle; set => Set(ref _youTubeFlowTitle, value); }
    public string YouTubeFlowScript { get => _youTubeFlowScript; set => Set(ref _youTubeFlowScript, value); }
    public int YouTubeFlowMinutes { get => _youTubeFlowMinutes; set => Set(ref _youTubeFlowMinutes, value); }
    public string YouTubeFlowRatio { get => _youTubeFlowRatio; set => Set(ref _youTubeFlowRatio, value); }
    public string YouTubeFlowStatus { get => _youTubeFlowStatus; private set => Set(ref _youTubeFlowStatus, value); }
    public string YouTubeFlowOutput { get => _youTubeFlowOutput; private set => Set(ref _youTubeFlowOutput, value); }

    public ICommand GenerateYouTubeFlowIdeasCommand { get; private set; } = null!;
    public ICommand UseYouTubeFlowIdeaCommand { get; private set; } = null!;
    public ICommand GenerateYouTubeFlowScriptCommand { get; private set; } = null!;
    public ICommand BuildYouTubeFlowCommand { get; private set; } = null!;
    public ICommand SaveYouTubeFlowSettingsCommand { get; private set; } = null!;
    public ICommand OpenYouTubeFlowOutputCommand { get; private set; } = null!;

    private void InitializeYouTubeFlowCommands()
    {
        GenerateYouTubeFlowIdeasCommand = new AsyncCommand(() =>
            RunManagedJobAsync("YouTube Flow — Ideas", GenerateYouTubeFlowIdeasAsync));
        UseYouTubeFlowIdeaCommand = new RelayCommand(() =>
        {
            if (YouTubeFlowSelectedIdea is null) return;
            YouTubeFlowTitle = YouTubeFlowSelectedIdea.Title;
            YouTubeFlowStatus = "Idea selected. Generate a script or paste your own.";
        });
        GenerateYouTubeFlowScriptCommand = new AsyncCommand(() =>
            RunManagedJobAsync("YouTube Flow — Script", GenerateYouTubeFlowScriptAsync));
        SaveYouTubeFlowSettingsCommand = new AsyncCommand(async () =>
        {
            await _settingsService.SaveAsync(Settings);
            YouTubeFlowStatus = "Pixabay key/settings saved locally.";
        });
        BuildYouTubeFlowCommand = new AsyncCommand(() =>
            RunManagedJobAsync("YouTube Flow — Stock Video", BuildYouTubeFlowAsync));
        OpenYouTubeFlowOutputCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(YouTubeFlowOutput)) return;
            var folder = Path.GetDirectoryName(YouTubeFlowOutput);
            if (folder is not null && Directory.Exists(folder))
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"")
                    { UseShellExecute = true });
        });
    }

    private async Task GenerateYouTubeFlowIdeasAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(YouTubeFlowNiche))
                throw new InvalidOperationException("Enter a niche before generating ideas.");
            YouTubeFlowStatus = "Generating YouTube Flow ideas with ChatGPT...";
            var response = await _chat.GenerateVideoIdeasAsync(
                YouTubeFlowNiche.Trim(), YouTubeFlowIdeaCount, Settings, Log, _cts.Token);
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.GetProperty("response_type").GetString() != "zerovideo_ideas_v1")
                throw new InvalidDataException("Unexpected idea response type.");
            YouTubeFlowIdeas.Clear();
            foreach (var idea in root.GetProperty("ideas").EnumerateArray())
            {
                var title = GetString(idea, "title");
                var hook = GetString(idea, "hook");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(hook)) continue;
                YouTubeFlowIdeas.Add(new VideoIdea
                {
                    Title = title, Hook = hook, Niche = YouTubeFlowNiche.Trim()
                });
            }
            YouTubeFlowSelectedIdea = YouTubeFlowIdeas.FirstOrDefault();
            YouTubeFlowStatus = YouTubeFlowIdeas.Count +
                " ideas ready. Select an idea and choose Use selected.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { YouTubeFlowStatus = "Ideas failed: " + ex.Message; Fail(ex); }
    }

    private async Task GenerateYouTubeFlowScriptAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(YouTubeFlowTitle))
                throw new InvalidOperationException("Choose a video idea or enter a title first.");
            YouTubeFlowStatus = "Writing narration script...";
            var words = YouTubeFlowMinutes * 135;
            var prompt =
                "Write an original, complete, accurate voiceover narration script for a faceless " +
                "YouTube documentary video titled: " + YouTubeFlowTitle + ". " +
                "Aim for approximately " + words +
                " spoken words for " + YouTubeFlowMinutes + " minutes. " +
                "Use an immediate hook, clear narrative flow, strong transitions and a meaningful ending. " +
                "Avoid production directions, captions, timestamps, headings and stock footage instructions " +
                "inside the spoken narration. Avoid unfounded claims or fabricated real events. " +
                "Return ONLY one valid JSON object in a fenced json code block with exactly the schema " +
                "{\"response_type\":\"youtube_flow_script_v1\",\"script\":\"Full spoken narration text\"}.";
            var result = await _chat.GenerateTextAsync(
                new VideoProject { Title = YouTubeFlowTitle, Topic = YouTubeFlowTitle },
                Settings, prompt, Log, _cts.Token);
            using var doc = JsonDocument.Parse(result);
            if (GetString(doc.RootElement, "response_type") != "youtube_flow_script_v1")
                throw new InvalidDataException("Script response was not in the expected JSON format.");
            var script = GetString(doc.RootElement, "script");
            if (script.Length < 100)
                throw new InvalidDataException("Generated script was too short. Retry script generation.");
            YouTubeFlowScript = script;
            YouTubeFlowStatus = "Script ready. Review/edit it, then select Create video.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { YouTubeFlowStatus = "Script failed: " + ex.Message; Fail(ex); }
    }

    private async Task BuildYouTubeFlowAsync()
    {
        try
        {
            if (YouTubeFlowScript.Trim().Length < 100)
                throw new InvalidOperationException("Paste a full narration script or generate one first.");
            if (string.IsNullOrWhiteSpace(Settings.PixabayApiKey))
                throw new InvalidOperationException("Enter your Pixabay API key and click Save Settings.");

            // Separate workspace: never change the current Create/Shorts project.
            var safeName = new string((string.IsNullOrWhiteSpace(YouTubeFlowTitle)
                    ? "YouTube Flow" : YouTubeFlowTitle.Trim())
                .Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).Take(52).ToArray()).Trim();
            if (safeName.Length == 0) safeName = "YouTube Flow";
            var root = Path.Combine(Settings.ProjectsRoot, "YouTube Flow");
            Directory.CreateDirectory(root);
            var expectedTarget = Math.Clamp(YouTubeFlowMinutes * PixabayVideoService.ClipsPerMinute,
                1, PixabayVideoService.MaxClipsPerVideo);
            // A retry after Pixabay HTTP 429 must resume the existing voiceover
            // instead of generating (and billing for) the narration again.
            // Never put the API key in project IDs or checkpoint filenames.
            var identity = string.Join("\n", YouTubeFlowTitle, YouTubeFlowScript,
                YouTubeFlowRatio, Settings.TextToSpeechEngine,
                Settings.OmniVoiceProfileId, Settings.ClipchampVoice);
            var resumeKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
            var resumeFile = Path.Combine(root, ".resume-" + resumeKey + ".txt");
            var folder = "";
            try
            {
                if (File.Exists(resumeFile))
                {
                    var candidate = Path.GetFullPath(await File.ReadAllTextAsync(resumeFile, _cts.Token));
                    var permittedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                        Path.DirectorySeparatorChar;
                    if (candidate.StartsWith(permittedRoot, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(Path.Combine(candidate, "script.txt")) &&
                        !File.Exists(Path.Combine(candidate, "youtube-flow-complete.mp4")) &&
                        (!File.Exists(Path.Combine(candidate, "youtube-flow.mp4")) ||
                         IsIncompletePixabayPreview(candidate, expectedTarget)) &&
                        await File.ReadAllTextAsync(Path.Combine(candidate, "script.txt"), _cts.Token) ==
                            YouTubeFlowScript)
                    {
                        folder = candidate;
                        Log("YouTube Flow: resuming previous unfinished project without repeating completed stages.");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log("YouTube Flow: previous resume marker was not usable; starting a new folder.");
            }
            // Recover a project written by the older portable build, before the
            // dedicated resume marker existed. Match the *exact* narration, limit
            // discovery to recent unfinished folders and do not touch completed MP4s.
            if (string.IsNullOrWhiteSpace(folder))
            {
                foreach (var previous in Directory.EnumerateDirectories(root)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc).Take(30))
                {
                    var savedScript = Path.Combine(previous, "script.txt");
                    var savedAudio = Path.Combine(previous, "audio", "voiceover.wav");
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(previous) > TimeSpan.FromDays(7))
                        continue;
                    if (!File.Exists(savedScript) || !File.Exists(savedAudio) ||
                        File.Exists(Path.Combine(previous, "youtube-flow-complete.mp4")) ||
                        (File.Exists(Path.Combine(previous, "youtube-flow.mp4")) &&
                            !IsIncompletePixabayPreview(previous, expectedTarget)))
                        continue;
                    try
                    {
                        if (await File.ReadAllTextAsync(savedScript, _cts.Token) == YouTubeFlowScript)
                        {
                            folder = previous;
                            Log("YouTube Flow: found matching unfinished project from an older build; checking existing voiceover.");
                            break;
                        }
                    }
                    catch (IOException) { /* Unreadable prior project is left unchanged. */ }
                }
            }
            if (string.IsNullOrWhiteSpace(folder))
                folder = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmssfff") +
                    "-" + safeName);
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(folder, "audio"));
            // Keep existing #338 three-clip previews intact. A recovered, fully
            // populated video is rendered to a *new* filename in that folder.
            var existingPreview = Path.Combine(folder, "youtube-flow.mp4");
            var output = File.Exists(existingPreview) && IsIncompletePixabayPreview(folder, expectedTarget)
                ? Path.Combine(folder, "youtube-flow-complete.mp4")
                : existingPreview;
            YouTubeFlowOutput = output; // Open Output Folder works after a failed job.
            await File.WriteAllTextAsync(
                Path.Combine(folder, "script.txt"), YouTubeFlowScript, _cts.Token);
            await File.WriteAllTextAsync(resumeFile, folder, _cts.Token);

            // Split longer scripts at sentence boundaries so Clipchamp/OmniVoice
            // receive manageable narration chunks rather than a single giant TTS request.
            var chapters = SplitYouTubeFlowNarration(YouTubeFlowScript)
                .Select((narration, index) => new ProjectChapter
                {
                    Number = index + 1, Title = YouTubeFlowTitle,
                    Narration = narration
                }).ToList();
            var project = new VideoProject
            {
                Title = YouTubeFlowTitle, Topic = YouTubeFlowTitle,
                ProjectDirectory = folder, AspectRatio = YouTubeFlowRatio,
                Chapters = chapters,
                Options = new ProjectOptions { GenerateCaptions = false, BurnInBoldCaptions = false }
            };
            Status = YouTubeFlowStatus = "Preparing narration voiceover...";
            Progress = 10;
            var renderer = new YouTubeFlowRenderService(_runner, _ffmpeg);
            var voiceover = Path.Combine(folder, "audio", "voiceover.wav");
            var reuseVoiceover = false;
            if (File.Exists(voiceover))
            {
                try
                {
                    await _ffmpeg.ValidateNarrationAudioAsync(
                        voiceover, Settings, "YouTube Flow saved voiceover",
                        YouTubeFlowScript, Log, _cts.Token);
                    reuseVoiceover = true;
                    Log("YouTube Flow: reusing the already-complete voiceover from this project.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log("YouTube Flow: saved voiceover is not valid (" + ex.Message +
                        "); regenerating audio.");
                }
            }
            if (!reuseVoiceover)
            {
            var localVoice = Settings.TextToSpeechEngine == "OmniVoice (Local)";
            if (localVoice)
            {
                if (!Settings.OmniVoiceNonCommercialAcknowledged)
                    throw new InvalidOperationException(
                        "OmniVoice is non-commercial/evaluation-only. Acknowledge the license in Voice Studio.");
            }
            else
            {
                // One manual checkpoint for the whole session, not per chunk.
                _ttsContinueGate = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                WaitingForTtsContinue = true;
                Status = YouTubeFlowStatus = "Select your Clipchamp voice, then click Continue TTS here.";
                await _clip.PrepareManualVoiceSelectionAsync(Settings, Log, _cts.Token);
                await _ttsContinueGate.Task.WaitAsync(_cts.Token);
                WaitingForTtsContinue = false;
                _ttsContinueGate = null;
                _clip.ConfirmManualVoiceSelection();
            }
            var narrationFiles = new List<string>();
            for (var index = 0; index < chapters.Count; index++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var chapter = chapters[index];
                Status = YouTubeFlowStatus =
                    $"Narrating section {index + 1}/{chapters.Count}...";
                var generated = localVoice
                    ? await _omniVoice.GenerateChapterAsync(chapter, project, Settings, Log, _cts.Token)
                    : await _clip.GenerateChapterAsync(chapter, project, Settings, Log, _cts.Token);
                if (string.IsNullOrWhiteSpace(generated))
                    throw new InvalidDataException("Voice generation did not return audio.");
                // The very last TTS chunk can be under eight seconds; the
                // complete merged narration receives the stronger duration check.
                if (chapter.Narration.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 40)
                    await _ffmpeg.ValidateAudibleAudioAsync(
                        generated, Settings, $"YouTube Flow short part {index + 1}",
                        Log, _cts.Token);
                else
                    await _ffmpeg.ValidateNarrationAudioAsync(
                        generated, Settings, $"YouTube Flow part {index + 1}",
                        chapter.Narration, Log, _cts.Token);
                narrationFiles.Add(generated);
            }
            _ = await renderer.CombineNarrationAsync(
                narrationFiles, voiceover,
                Settings, Log, _cts.Token);
            await _ffmpeg.ValidateNarrationAudioAsync(
                voiceover, Settings, "YouTube Flow complete voiceover",
                YouTubeFlowScript, Log, _cts.Token);
            }
            // Scale footage to the real narration duration, not merely the
            // duration dropdown (a hand-edited script can be shorter/longer).
            var narrationSeconds = await _ffmpeg.ProbeDurationAsync(voiceover, Settings, _cts.Token);
            var targetClips = PixabayVideoService.TargetClipCount(
                TimeSpan.FromSeconds(narrationSeconds));
            var desiredQueries = PixabayVideoService.SuggestedQueryCount(targetClips);
            Log($"YouTube Flow: narration {narrationSeconds:0}s; aiming for {targetClips} unique six-second stock clips from ~{desiredQueries} specific searches.");

            Progress = 35;
            Status = YouTubeFlowStatus = $"Storyboarding {desiredQueries} script scenes and their visual tone...";
            var scenePlanFile = Path.Combine(folder, "pixabay-scenes-v2.json");
            List<PixabayFootageScene>? scenes = null;
            if (File.Exists(scenePlanFile))
            {
                try
                {
                    scenes = JsonSerializer.Deserialize<List<PixabayFootageScene>>(
                        await File.ReadAllTextAsync(scenePlanFile, _cts.Token));
                    if (scenes?.Count != desiredQueries ||
                        scenes.Any(x => string.IsNullOrWhiteSpace(x.SearchQuery)))
                        scenes = null;
                    else Log("YouTube Flow: reusing storyboard and scene moods from previous attempt.");
                }
                catch (JsonException) { scenes = null; }
            }
            if (scenes is null)
            {
                scenes = await GenerateYouTubeFlowScenesAsync(desiredQueries);
                await File.WriteAllTextAsync(scenePlanFile,
                    JsonSerializer.Serialize(scenes, new JsonSerializerOptions { WriteIndented = true }),
                    _cts.Token);
            }
            var queries = scenes.Select(scene => scene.SearchQuery).ToList();
            Log($"YouTube Flow: {scenes.Count} narration-matched scenes ready. Incompatible visual moods or unrelated tags will be rejected.");

            Progress = 45;
            Status = YouTubeFlowStatus = $"Selecting {targetClips} script-relevant Pixabay clips...";
            var pixabay = new PixabayVideoService();
            var clips = await pixabay.AcquireAsync(
                queries, Settings.PixabayApiKey, YouTubeFlowRatio, Log, _cts.Token,
                targetClips,
                (ready, total) =>
                {
                    Progress = 45 + (int)Math.Round(15d * ready / total);
                    Status = YouTubeFlowStatus = $"Scene-matched footage: {ready}/{total} distinct clips...";
                },
                scenes);
            var manifest = new
            {
                title = YouTubeFlowTitle,
                aspect_ratio = YouTubeFlowRatio,
                provider = "Pixabay",
                captions = false,
                footage_complete = true,
                target_clip_count = targetClips,
                narration_seconds = narrationSeconds,
                searches = queries,
                scenes = scenes.Select(scene => new
                {
                    scene.SceneNumber, scene.NarrationExcerpt, scene.SearchQuery, scene.Mood,
                    start_seconds = Math.Round((scene.SceneNumber - 1d) * narrationSeconds / scenes.Count, 2),
                    end_seconds = Math.Round(scene.SceneNumber * narrationSeconds / scenes.Count, 2),
                    matching_clips = clips.Count(c => c.SearchQuery == scene.SearchQuery)
                }).ToArray(),
                clips = clips.Select(c => new
                {
                    c.Id, c.SourcePage, c.SearchQuery, c.Tags,
                    local_file = c.FilePath
                }).ToArray()
            };
            await File.WriteAllTextAsync(Path.Combine(folder, "stock-sources.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                _cts.Token);
            Progress = 65;
            Status = YouTubeFlowStatus = "FFmpeg is stitching the stock footage...";
            await renderer.RenderAsync(clips, voiceover, output,
                YouTubeFlowRatio, Settings, Log, _cts.Token, scenes);

            Progress = 100;
            YouTubeFlowOutput = output;
            Status = YouTubeFlowStatus =
                $"Complete with {clips.Count} distinct Pixabay footage clips: " + output;
            Log("YouTube Flow finished without captions. Output: " + output);
            try { File.Delete(resumeFile); } catch { /* Finished output remains safe. */ }
            await _settingsService.SaveAsync(Settings);
        }
        catch (OperationCanceledException) { YouTubeFlowStatus = "Stopped. Completed files remain saved."; throw; }
        catch (Exception ex) { YouTubeFlowStatus = "Video failed: " + ex.Message; Fail(ex); }
        finally
        {
            WaitingForTtsContinue = false;
            _ttsContinueGate = null;
        }
    }

    private static bool IsIncompletePixabayPreview(string folder, int desiredClipCount)
    {
        // Older builds considered three clips "complete" after HTTP 429.
        // Detect that old partial output by its existing source manifest,
        // without touching or overwriting the original MP4.
        var manifest = Path.Combine(folder, "stock-sources.json");
        if (!File.Exists(manifest)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            if (!root.TryGetProperty("clips", out var clips) ||
                clips.ValueKind != JsonValueKind.Array)
                return false;
            // Even a "complete" old 6-10 clip video is only a preview under
            // the newer duration-based one-clip-per-six-seconds requirement.
            return clips.GetArrayLength() < desiredClipCount;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<List<PixabayFootageScene>> GenerateYouTubeFlowScenesAsync(int desiredQueries)
    {
        var narrationSegments = SplitNarrationIntoScenes(YouTubeFlowScript, desiredQueries);
        // Scary/horror titles have a genre-wide dark art direction. Other
        // narratives can shift mood naturally with each scene.
        var scaryTitle = Regex.IsMatch(YouTubeFlowTitle,
            @"\b(horror|haunted|haunting|scary|creepy|ghost|exorcism|demon|nightmare|curse|darkest|terrifying|paranormal|sinister|chilling)\b",
            RegexOptions.IgnoreCase);
        var storyboard = string.Join("\n", narrationSegments.Select((segment, i) =>
            $"SCENE {i + 1} (narration in this exact order): {segment}"));
        var prompt =
            "You are a documentary stock-footage storyboard editor. Produce EXACTLY " +
            desiredQueries + " chronological Pixabay VIDEO searches, ONE for each numbered script scene. " +
            "Each search MUST depict a visible person/object/action/place actually appropriate to its " +
            "corresponding narration segment. Keep the video's genre and EMOTIONAL MOOD consistent. " +
            "For scary, haunted, creepy or suspenseful material use genuinely dark/eerie/ominous " +
            "visuals (night, shadows, fog, abandoned places, etc.). Never choose bright, happy, " +
            "sunny vacation or party footage for a scary narration. For positive stories, use " +
            "positive imagery. Match changes in mood only when the narration changes. " +
            "Every search must be 2–5 ordinary searchable English words, concrete and specific, " +
            "with visual mood where helpful (e.g. 'dark abandoned hallway', not 'interesting history'). " +
            "Make all queries DIFFERENT while keeping each relevant to its numbered segment. " +
            "Use one mood from: ominous, tense, somber, neutral, calm, upbeat. " +
            "Do NOT invent eyewitness videos, archival recordings, or authentic footage of " +
            "historical crimes. Symbolic B-roll is symbolic, not evidence. " +
            "Return ONLY one valid fenced JSON object: " +
            "{\"response_type\":\"youtube_flow_scenes_v2\",\"scenes\":[" +
            "{\"query\":\"dark empty hallway\",\"mood\":\"ominous\"}]}." +
            " The scenes array MUST contain exactly " + desiredQueries + " entries in " +
            "the SAME ORDER as the numbered narration. No extra keys or explanation. " +
            "VIDEO TITLE: " + YouTubeFlowTitle +
            (scaryTitle ? "\nGLOBAL VISUAL TONE: DARK / OMINOUS across all scenes." : "") +
            "\nNUMBERED NARRATION SCENES:\n" + storyboard;
        var raw = await _chat.GenerateTextAsync(
            new VideoProject { Title = YouTubeFlowTitle, Topic = YouTubeFlowTitle },
            Settings, prompt, Log, _cts.Token);
        using var doc = JsonDocument.Parse(raw);
        if (GetString(doc.RootElement, "response_type") != "youtube_flow_scenes_v2" ||
            !doc.RootElement.TryGetProperty("scenes", out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() != desiredQueries)
            throw new InvalidDataException(
                $"Storyboard must return exactly {desiredQueries} ordered scenes. Please retry.");

        var allowedMoods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ominous", "tense", "somber", "neutral", "calm", "upbeat" };
        var usedQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scenes = new List<PixabayFootageScene>(desiredQueries);
        for (int i = 0; i < desiredQueries; i++)
        {
            var entry = array[i];
            var query = Regex.Replace(GetString(entry, "query"), @"\s+", " ").Trim();
            var mood = GetString(entry, "mood").ToLowerInvariant();
            if (scaryTitle) mood = "ominous";
            if (query.Length < 5 || query.Length > 100 ||
                query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is < 2 or > 6 ||
                !usedQueries.Add(query) || !allowedMoods.Contains(mood))
                throw new InvalidDataException(
                    $"Storyboard scene {i + 1} has an invalid/repeated visual query or mood. Retry the storyboard.");
            scenes.Add(new PixabayFootageScene(i + 1, narrationSegments[i], query, mood));
        }
        return scenes;
    }

    private static List<string> SplitNarrationIntoScenes(string narration, int sceneCount)
    {
        var words = Regex.Split(narration.Trim(), @"\s+")
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (words.Length < sceneCount * 2)
            throw new InvalidDataException("Not enough narration to assign distinct stock-footage scenes.");
        var segments = new List<string>(sceneCount);
        for (var i = 0; i < sceneCount; i++)
        {
            var start = i * words.Length / sceneCount;
            var end = (i + 1) * words.Length / sceneCount;
            segments.Add(string.Join(" ", words[start..end]));
        }
        return segments;
    }

    private static List<string> SplitYouTubeFlowNarration(string script)
    {
        const int maxCharacters = 1700;
        var chunks = new List<string>();
        var current = new StringBuilder();
        var sentences = Regex.Split(script.Trim(), @"(?<=[.!?])\s+|\r?\n+");
        foreach (var sentence in sentences.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            // Keep even unusually long sentences within browser TTS limits.
            var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                if (current.Length > 0 && current.Length + word.Length + 1 > maxCharacters)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                }
                if (word.Length > maxCharacters)
                {
                    for (var pos = 0; pos < word.Length; pos += maxCharacters)
                        chunks.Add(word.Substring(pos, Math.Min(maxCharacters, word.Length - pos)));
                    continue;
                }
                if (current.Length > 0) current.Append(' ');
                current.Append(word);
            }
        }
        if (current.Length > 0) chunks.Add(current.ToString().Trim());
        return chunks;
    }

    private static string GetString(JsonElement node, string property)
        => node.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
}
