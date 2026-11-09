using System.Collections.ObjectModel;
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
            var folder = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss") +
                "-" + safeName);
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(folder, "audio"));
            var output = Path.Combine(folder, "youtube-flow.mp4");
            YouTubeFlowOutput = "";
            await File.WriteAllTextAsync(
                Path.Combine(folder, "script.txt"), YouTubeFlowScript, _cts.Token);

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
            Status = YouTubeFlowStatus = "Generating narration voiceover...";
            Progress = 10;
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
            var renderer = new YouTubeFlowRenderService(_runner, _ffmpeg);
            var voiceover = await renderer.CombineNarrationAsync(
                narrationFiles, Path.Combine(folder, "audio", "voiceover.wav"),
                Settings, Log, _cts.Token);
            await _ffmpeg.ValidateNarrationAudioAsync(
                voiceover, Settings, "YouTube Flow complete voiceover",
                YouTubeFlowScript, Log, _cts.Token);
            Progress = 35;
            Status = YouTubeFlowStatus = "Planning stock video queries...";
            var queries = await GenerateYouTubeFlowQueriesAsync();

            Progress = 45;
            Status = YouTubeFlowStatus = "Searching Pixabay and preparing clips...";
            var pixabay = new PixabayVideoService();
            var clips = await pixabay.AcquireAsync(
                queries, Settings.PixabayApiKey, YouTubeFlowRatio, Log, _cts.Token);
            var manifest = new
            {
                title = YouTubeFlowTitle,
                aspect_ratio = YouTubeFlowRatio,
                provider = "Pixabay",
                captions = false,
                searches = queries,
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
                YouTubeFlowRatio, Settings, Log, _cts.Token);

            Progress = 100;
            YouTubeFlowOutput = output;
            Status = YouTubeFlowStatus = "Complete: " + output;
            Log("YouTube Flow finished without captions. Output: " + output);
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

    private async Task<List<string>> GenerateYouTubeFlowQueriesAsync()
    {
        var prompt = "You are a stock-footage editor. Based on the following narrated " +
            "script, produce 6 to 10 distinct, specific, visually searchable, " +
            "non-misleading Pixabay VIDEO search queries. Focus on visible people, " +
            "places, objects and actions. Queries should be 2-5 common English words. " +
            "Order queries according to the narration's progression. " +
            "No fabricated archival or crime-scene claims; generic symbolic B-roll " +
            "must never be presented as authentic historical footage. " +
            "Return ONLY a valid fenced JSON object shaped " +
            "{\"response_type\":\"youtube_flow_footage_v1\",\"queries\":[\"search words\"]}. " +
            "TITLE: " + YouTubeFlowTitle + "\nSCRIPT:\n" + YouTubeFlowScript;
        var raw = await _chat.GenerateTextAsync(
            new VideoProject { Title = YouTubeFlowTitle, Topic = YouTubeFlowTitle },
            Settings, prompt, Log, _cts.Token);
        using var doc = JsonDocument.Parse(raw);
        if (GetString(doc.RootElement, "response_type") != "youtube_flow_footage_v1" ||
            !doc.RootElement.TryGetProperty("queries", out var array) ||
            array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("ChatGPT did not provide a valid Pixabay search plan.");
        var queries = array.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()?.Trim() ?? "")
            .Where(x => x.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(PixabayVideoService.MaxDownloadsPerVideo)
            .ToList();
        if (queries.Count == 0)
            throw new InvalidDataException("No usable Pixabay queries were generated.");
        return queries;
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
