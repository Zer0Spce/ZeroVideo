from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]

def load(path):
    return (ROOT / path).read_text(encoding='utf-8')

def save(path, text):
    (ROOT / path).write_text(text, encoding='utf-8')

def rep(text, old, new, label):
    if old not in text:
        raise SystemExit(f'Missing anchor: {label}')
    return text.replace(old, new, 1)

def sub(text, pattern, replacement, label):
    out, n = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if n != 1:
        raise SystemExit(f'Pattern failed ({n}): {label}')
    return out

# -----------------------------------------------------------------------------
# Visual styles
# -----------------------------------------------------------------------------
p = 'src/ZeroVideo.App/Services/VisualStylePresets.cs'
s = load(p)
s = rep(s,
'''        "Editorial Cartoon Explainer",
        "Hand-drawn explainer",''',
'''        "Editorial Cartoon Explainer",
        "Cinematic Comic Explainer",
        "Comic Book Explainer",
        "Hand-drawn explainer",''', 'style names')
s = rep(s,
'''            "Editorial Cartoon Explainer" =>
                "Polished editorial cartoon documentary style for long-form YouTube storytelling: bold clean black outlines, expressive slightly exaggerated human faces and poses, colorful hand-illustrated environments, simplified but recognizable architecture, technology and props, dynamic perspective, strong foreground/midground/background separation, cinematic composition, warm cel-shaded lighting, and clear story-driven scenes. Keep characters and environments fully illustrated rather than photorealistic or 3D. Do not add thumbnail headlines, captions, logos, UI, watermarks, or readable text unless the scene explicitly requires a historically necessary sign or document.",

            "Hand-drawn explainer" =>''',
'''            "Editorial Cartoon Explainer" =>
                "Polished editorial cartoon documentary style for long-form YouTube storytelling: bold clean black outlines, expressive slightly exaggerated human faces and poses, colorful hand-illustrated environments, simplified but recognizable architecture, technology and props, dynamic perspective, strong foreground/midground/background separation, cinematic composition, warm cel-shaded lighting, and clear story-driven scenes. Keep characters and environments fully illustrated rather than photorealistic or 3D. Do not add thumbnail headlines, captions, logos, UI, watermarks, or readable text unless the scene explicitly requires a historically necessary sign or document.",

            "Cinematic Comic Explainer" =>
                "CINEMATIC COMIC EXPLAINER STYLE. Create a polished high-detail 2D digital comic illustration for long-form YouTube storytelling. Use bold clean black ink outlines around characters and important objects, expressive slightly exaggerated caricature faces, dynamic believable poses, detailed cel shading blended with smooth painted gradient lighting, cinematic rim light, warm highlights, deep colorful shadows, and vivid controlled color grading. Build strong foreground, midground and background depth with dynamic wide-angle compositions similar to premium illustrated documentary artwork or a modern comic-book movie poster. Keep vehicles, architecture, technology, clothing and environments recognizable and detailed. Fully illustrated, never photorealistic, never 3D, never anime, never watercolor. Do not add thumbnail headlines, captions, logos, UI, watermarks or readable text unless historically essential to the scene.",

            "Comic Book Explainer" =>
                "CLASSIC COMIC BOOK EXPLAINER STYLE. Create a fully illustrated traditional comic-book scene with strong black ink contours, confident line weight, graphic cel shading, selective crosshatching, subtle halftone or Ben-Day print texture, dramatic shadows, bold color blocks, energetic perspective and expressive character poses. Favor graphic-novel storytelling and panel-like composition while still producing ONE full-frame scene rather than a collage or multi-panel page. Keep locations, vehicles, clothing and technology recognizable. Avoid photorealism, 3D rendering, anime and children's-cartoon simplification. No speech bubbles, captions, headline text, logos, UI or watermarks unless the narration explicitly requires a historically necessary sign or document.",

            "Hand-drawn explainer" =>''', 'style prompts')
save(p, s)

# -----------------------------------------------------------------------------
# Project model: Phase 2 options + director metadata
# -----------------------------------------------------------------------------
p = 'src/ZeroVideo.App/Models/VideoProject.cs'
s = load(p)
s = rep(s,
'''    public bool GenerateMissingVisuals { get; set; } = true;

    /// <summary>''',
'''    public bool GenerateMissingVisuals { get; set; } = true;

    // Phase 2 smart-editing controls. Defaults are conservative so old projects remain stable.
    public bool SmartSceneTiming { get; set; } = true;
    public bool AiEditingDirector { get; set; } = true;
    public bool RoleAwareEditing { get; set; } = true;
    public double VisualRefreshSeconds { get; set; } = 6.0;
    public bool AudioMastering { get; set; } = true;
    public bool AutoMusicDucking { get; set; } = true;
    public bool RenderCache { get; set; } = true;
    public string RenderQuality { get; set; } = "Final";
    public bool VisualQa { get; set; } = true;
    public bool FinalQc { get; set; } = true;
    public bool ImageConsistencyMode { get; set; } = true;
    public bool BurnInCaptions { get; set; }
    public string CaptionStyle { get; set; } = "Documentary";

    /// <summary>''', 'phase2 options')
s = rep(s,
'''public sealed class ProjectScene
{
    public int Number { get; set; }
    public string Role { get; set; } = "detail";''',
'''public sealed class ProjectScene
{
    public int Number { get; set; }
    public int ChapterNumber { get; set; }
    public string Role { get; set; } = "detail";''', 'chapter number')
s = rep(s,
'''    public string ShotDirection { get; set; } = "slow push in";
    public double TargetSeconds { get; set; } = 6;''',
'''    public string ShotDirection { get; set; } = "slow push in";
    // Optional AI Editing Director decisions. "Automatic" lets ZeroVideo decide locally.
    public string EditMotion { get; set; } = "Automatic";
    public string EditTransition { get; set; } = "Automatic";
    public bool? UseParallax { get; set; }
    public string EditIntensity { get; set; } = "Automatic";
    public string EditorNote { get; set; } = "";
    public double TargetSeconds { get; set; } = 6;''', 'director scene fields')
save(p, s)

# -----------------------------------------------------------------------------
# Prompt builder: stronger continuity + AI edit direction
# -----------------------------------------------------------------------------
p = 'src/ZeroVideo.App/Services/PromptBuilder.cs'
s = load(p)
s = rep(s,
'''AVERAGE SPOKEN WORDS PER SCENE: about {{averageSceneWords}} words, adjusted naturally as needed
PREVIOUS CHAPTER: {{previous}}''',
'''AVERAGE SPOKEN WORDS PER SCENE: about {{averageSceneWords}} words, adjusted naturally as needed
AI EDITING DIRECTOR: {{(p.Options.AiEditingDirector ? "ON" : "OFF")}}
IMAGE CONSISTENCY MODE: {{(p.Options.ImageConsistencyMode ? "ON" : "OFF")}}
PREVIOUS CHAPTER: {{previous}}''', 'prompt flags')
s = rep(s,
'''- Maintain visual continuity between adjacent scenes: recurring subjects should keep recognizable clothing, shapes, props, locations, and era unless the narration changes them.
- If the selected style is Stickman explainer,''',
'''- Maintain visual continuity between adjacent scenes: recurring subjects should keep recognizable clothing, shapes, props, locations, and era unless the narration changes them.
- When IMAGE CONSISTENCY MODE is ON, explicitly preserve stable recurring character traits (hair, face shape, age range, clothing palette and signature props) and recurring location design across imagePrompt fields. Do not randomly redesign the same recurring subject from scene to scene.
- If AI EDITING DIRECTOR is ON, choose editMotion, editTransition, useParallax, editIntensity and editorNote to support the narration beat. Hooks may move faster, evidence should be readable, reveals may use a deliberate push/parallax, transitions can move laterally, and endings should usually settle or pull back. Avoid repeating the same edit choice in consecutive scenes.
- If AI EDITING DIRECTOR is OFF, return Automatic for editMotion/editTransition/editIntensity and null for useParallax.
- editMotion must be one of: Automatic, push-in, pull-back, pan-horizontal, pan-vertical, drift, handheld, parallax, static.
- editTransition must be one of: Automatic, fade, fadeblack, fadewhite, dissolve, smoothleft, smoothright, slideleft, slideright, wipeleft, wipeup, zoomin, hblur.
- editIntensity must be one of: Automatic, Subtle, Medium, Strong.
- editorNote must be a very short editing rationale, not narration.
- If the selected style is Stickman explainer,''', 'director rules')
s = rep(s,
'''      "imagePrompt": "one scene-specific visual that directly illustrates this narration while following the selected style",
      "shotDirection": "slow push in",
      "targetSeconds": {{Math.Max(1, (int)Math.Round(averageSceneSeconds))}}''',
'''      "imagePrompt": "one scene-specific visual that directly illustrates this narration while following the selected style",
      "shotDirection": "slow push in",
      "editMotion": "Automatic",
      "editTransition": "Automatic",
      "useParallax": null,
      "editIntensity": "Automatic",
      "editorNote": "short edit rationale",
      "targetSeconds": {{Math.Max(1, (int)Math.Round(averageSceneSeconds))}}''', 'director json')
save(p, s)

# -----------------------------------------------------------------------------
# Main ViewModel: scene editor + save edits + chapter numbers
# -----------------------------------------------------------------------------
p = 'src/ZeroVideo.App/ViewModels/MainViewModel.cs'
s = load(p)
s = rep(s,
'''        LoadProjectCommand = new AsyncCommand(LoadProjectAsync);
        CancelCommand = new RelayCommand(() => _cts.Cancel());''',
'''        LoadProjectCommand = new AsyncCommand(LoadProjectAsync);
        SaveProjectEditsCommand = new AsyncCommand(SaveProjectEditsAsync, () => Project.Chapters.Count > 0);
        CancelCommand = new RelayCommand(() => _cts.Cancel());''', 'save command ctor')
s = rep(s,
'''    public IEnumerable<ProjectChapter> Chapters => Project.Chapters;
    public ZeroVideoSettings Settings''',
'''    public IEnumerable<ProjectChapter> Chapters => Project.Chapters;
    public IEnumerable<ProjectScene> AllScenes => Project.Chapters.SelectMany(c => c.Scenes);
    public ZeroVideoSettings Settings''', 'all scenes prop')
s = rep(s,
'''    public ICommand LoadProjectCommand { get; }
    public ICommand CancelCommand { get; }''',
'''    public ICommand LoadProjectCommand { get; }
    public ICommand SaveProjectEditsCommand { get; }
    public ICommand CancelCommand { get; }''', 'save command prop')
s = rep(s,
'''            for (var s = 0; s < generatedChapter!.Scenes.Count; s++)
                generatedChapter.Scenes[s].Number = s + 1;''',
'''            for (var s = 0; s < generatedChapter!.Scenes.Count; s++)
            {
                generatedChapter.Scenes[s].Number = s + 1;
                generatedChapter.Scenes[s].ChapterNumber = generatedChapter.Number;
            }''', 'chapter numbers generation')
s = rep(s,
'''            Raise(nameof(Chapters));
            RefreshCommands();
            Log($"Chapter {i + 1} checkpoint saved:''',
'''            Raise(nameof(Chapters));
            Raise(nameof(AllScenes));
            RefreshCommands();
            Log($"Chapter {i + 1} checkpoint saved:''', 'raise scenes after generation')
s = rep(s,
'''        Project = await _store.LoadAsync(dialog.FileName);
        if (string.IsNullOrWhiteSpace(Project.Voice))''',
'''        Project = await _store.LoadAsync(dialog.FileName);
        foreach (var chapter in Project.Chapters)
            foreach (var scene in chapter.Scenes)
                scene.ChapterNumber = chapter.Number;
        if (string.IsNullOrWhiteSpace(Project.Voice))''', 'chapter numbers load')
s = rep(s,
'''        Raise(nameof(Project));
        Raise(nameof(Chapters));
        Status = CanBuildVideo()''',
'''        Raise(nameof(Project));
        Raise(nameof(Chapters));
        Raise(nameof(AllScenes));
        Status = CanBuildVideo()''', 'raise load')
s = rep(s,
'''    private bool CanContinueWriting() => Project.Chapters.Count > 0 && Project.Chapters.Any(c => !IsComplete(c));''',
'''    private async Task SaveProjectEditsAsync()
    {
        if (Project.Chapters.Count == 0) return;
        if (string.IsNullOrWhiteSpace(Project.ProjectDirectory))
            ProjectDirectory = await _store.SaveAsync(Project, Settings.ProjectsRoot);
        else
            await _store.SaveAsync(Project, Settings.ProjectsRoot);
        Status = "Scene edits saved";
        Log("Smart Edit scene overrides saved to project.json.");
    }

    private bool CanContinueWriting() => Project.Chapters.Count > 0 && Project.Chapters.Any(c => !IsComplete(c));''', 'save method')
s = rep(s,
'''        (OpenProjectFolderCommand as AsyncCommand)?.RaiseCanExecuteChanged();
    }''',
'''        (OpenProjectFolderCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (SaveProjectEditsCommand as AsyncCommand)?.RaiseCanExecuteChanged();
    }''', 'refresh save command')
save(p, s)

# -----------------------------------------------------------------------------
# Pipeline visual QA
# -----------------------------------------------------------------------------
p = 'src/ZeroVideo.App/Services/PipelineService.cs'
s = load(p)
s = rep(s,
'''        if (project.Options.GenerateMissingVisuals)
            await GenerateMissingVisualsAsync(project, settings, log, progress, ct);

        var total = project.Chapters.Count + 1.0;''',
'''        if (project.Options.GenerateMissingVisuals)
            await GenerateMissingVisualsAsync(project, settings, log, progress, ct);

        if (project.Options.VisualQa)
            RunVisualQa(project, log);

        var total = project.Chapters.Count + 1.0;''', 'visual qa call')
s = rep(s,
'''    private static void AdoptExistingAudio(VideoProject project, ProjectChapter chapter)
    {''',
'''    private static void RunVisualQa(VideoProject project, Action<string>? log)
    {
        var allScenes = project.Chapters.SelectMany(c => c.Scenes.Select(s => (Chapter: c, Scene: s))).ToList();
        var missing = 0;
        var invalid = 0;
        var duplicateContent = 0;
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (chapter, scene) in allScenes)
        {
            var media = ProjectMedia.FindAll(project, chapter, scene).Where(File.Exists).ToList();
            if (media.Count == 0) { missing++; continue; }
            foreach (var path in media)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length < 2048) { invalid++; continue; }
                    if (!IsImageForQa(path)) continue;
                    using var fs = File.OpenRead(path);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
                    if (!hashes.Add(hash)) duplicateContent++;
                }
                catch { invalid++; }
            }
        }

        log?.Invoke($"Visual QA: {allScenes.Count - missing}/{allScenes.Count} scene(s) have direct media; {missing} missing, {invalid} invalid/tiny file(s), {duplicateContent} duplicate-content visual(s).");
        if (missing > 0) log?.Invoke("Visual QA note: missing scenes will still use ZeroVideo's nearest-real-visual fallback during rendering.");
    }

    private static bool IsImageForQa(string path)
        => new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static void AdoptExistingAudio(VideoProject project, ProjectChapter chapter)
    {''', 'visual qa methods')
save(p, s)

# -----------------------------------------------------------------------------
# FFmpeg Phase 2 engine
# -----------------------------------------------------------------------------
p = 'src/ZeroVideo.App/Services/FfmpegService.cs'
s = load(p)
if 'using System.Security.Cryptography;' not in s:
    s = s.replace('using System.Globalization;\n', 'using System.Globalization;\nusing System.Security.Cryptography;\n')

# Rewrite RenderAsync.
s = sub(s,
r'''    public async Task<string> RenderAsync\(VideoProject project, ZeroVideoSettings settings, Action<string>\? log = null, CancellationToken ct = default\)\n    \{.*?\n    \}\n\n    private async Task<string> RenderChapterVisualAsync''',
'''    public async Task<string> RenderAsync(VideoProject project, ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)
    {
        await ValidateAsync(settings, log, ct);
        var renderDir = Path.Combine(project.ProjectDirectory, "render");
        var exportsDir = Path.Combine(project.ProjectDirectory, "exports");
        Directory.CreateDirectory(renderDir);
        Directory.CreateDirectory(exportsDir);

        var profile = GetRenderProfile(project);
        log?.Invoke($"Render profile: {project.Options.RenderQuality} — {profile.Width}x{profile.Height}, CRF {profile.Crf}, preset {profile.Preset}.");

        var chapterOutputs = new List<string>();
        var effects = new RenderEffectsState();
        foreach (var chapter in project.Chapters)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(chapter.AudioPath) || !File.Exists(chapter.AudioPath))
                throw new FileNotFoundException($"Missing audio for chapter {chapter.Number}.", chapter.AudioPath);

            var audioSeconds = await ProbeDurationAsync(chapter.AudioPath, settings, ct);
            if (audioSeconds <= 0) audioSeconds = Math.Max(1, chapter.TargetSeconds);
            NormalizeSceneDurations(chapter, audioSeconds, project.Options);

            var audioForMux = chapter.AudioPath;
            if (project.Options.AudioMastering)
                audioForMux = await MasterNarrationAsync(chapter.AudioPath, chapter.Number, settings, renderDir, log, ct);

            var visual = await RenderChapterVisualAsync(project, chapter, settings, renderDir, effects, log, ct);
            var output = Path.Combine(renderDir, $"chapter-{chapter.Number:00}-final.mp4");
            var muxArgs = $"-y -i \\\"{visual}\\\" -i \\\"{audioForMux}\\\" -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 192k -shortest \\\"{output}\\\"";
            var mux = await runner.RunAsync(settings.FfmpegPath, muxArgs, renderDir, ct);
            if (mux.ExitCode != 0) throw new InvalidOperationException($"FFmpeg chapter mux failed: {mux.StdErr}");
            chapterOutputs.Add(output);
        }

        var concatList = Path.Combine(renderDir, "chapters.txt");
        await File.WriteAllLinesAsync(concatList, chapterOutputs.Select(p => $"file '{p.Replace("'", "'\\\\''")}'"), ct);
        var voiceFinal = Path.Combine(renderDir, "video-with-voice.mp4");
        var concat = await runner.RunAsync(settings.FfmpegPath, $"-y -f concat -safe 0 -i \\\"{concatList}\\\" -map 0:v:0 -map 0:a:0 -c:v copy -c:a aac -b:a 192k \\\"{voiceFinal}\\\"", renderDir, ct);
        if (concat.ExitCode != 0) throw new InvalidOperationException($"FFmpeg concat failed: {concat.StdErr}");

        var finalPath = Path.Combine(exportsDir, Sanitize(project.Title) + ".mp4");
        if (project.Options.AddBackgroundMusic && !string.IsNullOrWhiteSpace(project.Options.BackgroundMusicPath) && File.Exists(project.Options.BackgroundMusicPath))
        {
            var volume = project.Options.BackgroundMusicVolume.ToString("0.###", CultureInfo.InvariantCulture);
            string filter;
            if (project.Options.AutoMusicDucking)
                filter = $"[1:a]volume={volume}[bg];[bg][0:a]sidechaincompress=threshold=0.025:ratio=10:attack=40:release=500[ducked];[0:a][ducked]amix=inputs=2:duration=first:dropout_transition=2[a]";
            else
                filter = $"[1:a]volume={volume}[bg];[0:a][bg]amix=inputs=2:duration=first:dropout_transition=2[a]";
            var args = $"-y -i \\\"{voiceFinal}\\\" -stream_loop -1 -i \\\"{project.Options.BackgroundMusicPath}\\\" -filter_complex \\\"{filter}\\\" -map 0:v -map \\\"[a]\\\" -c:v copy -c:a aac -b:a 192k -shortest \\\"{finalPath}\\\"";
            var mixed = await runner.RunAsync(settings.FfmpegPath, args, renderDir, ct);
            if (mixed.ExitCode != 0) throw new InvalidOperationException($"Music mix failed: {mixed.StdErr}");
        }
        else File.Copy(voiceFinal, finalPath, true);

        string? captionSource = null;
        if (project.Options.GenerateCaptions || project.Options.BurnInCaptions)
        {
            captionSource = Path.Combine(renderDir, "phase2-captions.srt");
            await WriteSrtAsync(project, captionSource, ct);
            if (project.Options.GenerateCaptions)
                File.Copy(captionSource, Path.ChangeExtension(finalPath, ".srt"), true);
        }

        if (project.Options.BurnInCaptions && captionSource is not null)
        {
            var burned = Path.Combine(renderDir, "video-captioned.mp4");
            var forceStyle = CaptionForceStyle(project.Options.CaptionStyle, project.AspectRatio == "9:16");
            var burnArgs = $"-y -i \\\"{finalPath}\\\" -vf \\\"subtitles='phase2-captions.srt':force_style='{forceStyle}'\\\" -map 0:v:0 -map 0:a:0 -c:v libx264 -preset {profile.Preset} -crf {profile.Crf} -c:a copy \\\"{burned}\\\"";
            var burnedResult = await runner.RunAsync(settings.FfmpegPath, burnArgs, renderDir, ct);
            if (burnedResult.ExitCode == 0)
            {
                File.Copy(burned, finalPath, true);
                log?.Invoke($"Burned-in captions applied using {project.Options.CaptionStyle} style.");
            }
            else log?.Invoke("Burned-in caption pass was not supported by this FFmpeg build; keeping the normal final video and external SRT.");
        }

        await ValidateAudibleAudioAsync(finalPath, settings, "Final video", log, ct);
        if (project.Options.FinalQc)
            await ValidateFinalVideoAsync(finalPath, project, settings, profile, log, ct);
        return finalPath;
    }

    private async Task<string> RenderChapterVisualAsync''', 'RenderAsync')

# Rewrite chapter visual renderer with refresh segments and cache.
s = sub(s,
r'''    private async Task<string> RenderChapterVisualAsync\(VideoProject project, ProjectChapter chapter, ZeroVideoSettings settings, string renderDir, RenderEffectsState effects, Action<string>\? log, CancellationToken ct\)\n    \{.*?\n    \}\n\n    private async Task<string> RenderChapterTimelineAsync''',
'''    private async Task<string> RenderChapterVisualAsync(VideoProject project, ProjectChapter chapter, ZeroVideoSettings settings, string renderDir, RenderEffectsState effects, Action<string>? log, CancellationToken ct)
    {
        var profile = GetRenderProfile(project);
        var (w, h) = (profile.Width, profile.Height);
        var clips = new List<RenderedClip>();
        var configuredTransition = project.Options.SceneTransitions
            ? Math.Clamp(project.Options.TransitionDurationSeconds, 0.15, 0.8)
            : 0.0;

        foreach (var scene in chapter.Scenes)
        {
            var mediaItems = ProjectMedia.FindAll(project, chapter, scene).ToList();
            if (mediaItems.Count == 0)
            {
                var fallback = FindNearestTimelineMedia(project, chapter, scene);
                if (fallback is not null)
                {
                    mediaItems.Add(fallback.Path);
                    log?.Invoke($"Scene {chapter.Number}.{scene.Number}: no direct media; reusing nearby scene {fallback.ChapterNumber}.{fallback.SceneNumber} instead of rendering an empty frame.");
                }
            }

            if (mediaItems.Count == 0)
            {
                var seconds = Math.Max(1.0, scene.TargetSeconds);
                var duration = F(seconds);
                var output = Path.Combine(renderDir, $"c{chapter.Number:00}-s{scene.Number:000}.mp4");
                log?.Invoke($"Scene {chapter.Number}.{scene.Number}: no media exists anywhere in the project; using the branded project backdrop.");
                var args = $"-y -f lavfi -i \\\"color=c=0x241C30:s={w}x{h}:r=30:d={duration}\\\" -vf \\\"drawbox=x=0:y=0:w=iw:h=ih/3:color=0x34254A@0.75:t=fill,drawbox=x=0:y=ih*0.72:w=iw:h=ih*0.28:color=0x17151F@0.82:t=fill\\\" -an -c:v libx264 -preset {profile.Preset} -crf {profile.Crf} -pix_fmt yuv420p \\\"{output}\\\"";
                var result = await runner.RunAsync(settings.FfmpegPath, args, renderDir, ct);
                if (result.ExitCode != 0) throw new InvalidOperationException($"Scene render failed ({chapter.Number}.{scene.Number}): {result.StdErr}");
                clips.Add(new RenderedClip(output, seconds, "fade"));
                continue;
            }

            var perVisualSeconds = Math.Max(1.0, scene.TargetSeconds / mediaItems.Count);
            for (var mediaIndex = 0; mediaIndex < mediaItems.Count; mediaIndex++)
            {
                var media = mediaItems[mediaIndex];
                var segmentCount = IsVideo(media) ? 1 : Math.Clamp((int)Math.Ceiling(perVisualSeconds / Math.Max(2.0, project.Options.VisualRefreshSeconds)), 1, 8);
                var baseSegmentSeconds = perVisualSeconds / segmentCount;

                for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
                {
                    var allowDirector = segmentIndex == 0;
                    var transition = ChooseTransitionPreset(project, scene, effects, allowDirector);
                    var renderSeconds = baseSegmentSeconds + configuredTransition;
                    var duration = F(renderSeconds);
                    var suffix = $"-v{mediaIndex + 1:00}-r{segmentIndex + 1:00}";
                    var output = Path.Combine(renderDir, $"c{chapter.Number:00}-s{scene.Number:000}{suffix}.mp4");
                    string args;
                    string motion;

                    if (IsVideo(media))
                    {
                        motion = "source video";
                        var vf = $"scale={w}:{h}:force_original_aspect_ratio=increase:flags=lanczos,crop={w}:{h},fps=30";
                        var styleFx = BuildStyleEffectFilter(project, scene);
                        if (!string.IsNullOrWhiteSpace(styleFx)) vf += "," + styleFx;
                        args = $"-y -stream_loop -1 -i \\\"{media}\\\" -t {duration} -vf \\\"{vf}\\\" -an -c:v libx264 -preset {profile.Preset} -crf {profile.Crf} -pix_fmt yuv420p \\\"{output}\\\"";
                    }
                    else
                    {
                        motion = ChooseMotionPreset(project, scene, effects, allowDirector);
                        args = BuildStillRenderArgs(media, output, renderSeconds, w, h, motion, project, scene);
                    }

                    var cacheKey = BuildRenderCacheKey(media, renderSeconds, w, h, motion, transition, project, scene);
                    if (project.Options.RenderCache && IsRenderCacheHit(output, cacheKey))
                    {
                        log?.Invoke($"Scene {chapter.Number}.{scene.Number}: render cache hit ({motion}).");
                        clips.Add(new RenderedClip(output, renderSeconds, transition));
                        continue;
                    }

                    log?.Invoke($"Scene {chapter.Number}.{scene.Number}: effect {motion}; transition {transition}; {renderSeconds:0.0}s segment {segmentIndex + 1}/{segmentCount}.");
                    var result = await runner.RunAsync(settings.FfmpegPath, args, renderDir, ct);
                    if (result.ExitCode != 0)
                        throw new InvalidOperationException($"Scene render failed ({chapter.Number}.{scene.Number}, visual {mediaIndex + 1}, refresh {segmentIndex + 1}): {result.StdErr}");

                    if (project.Options.RenderCache) WriteRenderCache(output, cacheKey);
                    clips.Add(new RenderedClip(output, renderSeconds, transition));
                }
            }
        }

        return await RenderChapterTimelineAsync(project, chapter, settings, renderDir, clips, configuredTransition, log, ct);
    }

    private async Task<string> RenderChapterTimelineAsync''', 'RenderChapterVisual')

# Update still args signature and style effects call/profile.
s = s.replace('private static string BuildStillRenderArgs(string media, string output, double seconds, int w, int h, string motion, VideoProject project)',
              'private static string BuildStillRenderArgs(string media, string output, double seconds, int w, int h, string motion, VideoProject project, ProjectScene scene)')
s = rep(s,
'''        if (motion == "parallax" && project.Options.SmartParallax)
            return BuildParallaxArgs(media, output, duration, frames, w, h, project);''',
'''        if (motion == "parallax" && project.Options.SmartParallax)
            return BuildParallaxArgs(media, output, duration, frames, w, h, project, scene);''', 'parallax args call')
s = rep(s,
'''        var styleFx = BuildStyleEffectFilter(project);
        if (!string.IsNullOrWhiteSpace(styleFx)) vf += "," + styleFx;
        return $"-y -loop 1 -i \\\"{media}\\\" -t {duration} -vf \\\"{vf}\\\" -an -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \\\"{output}\\\"";''',
'''        var styleFx = BuildStyleEffectFilter(project, scene);
        if (!string.IsNullOrWhiteSpace(styleFx)) vf += "," + styleFx;
        var profile = GetRenderProfile(project);
        return $"-y -loop 1 -i \\\"{media}\\\" -t {duration} -vf \\\"{vf}\\\" -an -c:v libx264 -preset {profile.Preset} -crf {profile.Crf} -pix_fmt yuv420p \\\"{output}\\\"";''', 'still profile')
s = s.replace('private static string BuildParallaxArgs(string media, string output, string duration, int frames, int w, int h, VideoProject project)',
              'private static string BuildParallaxArgs(string media, string output, string duration, int frames, int w, int h, VideoProject project, ProjectScene scene)')
s = rep(s,
'''        var styleFx = BuildStyleEffectFilter(project);
        var tailFx = string.IsNullOrWhiteSpace(styleFx) ? "" : "," + styleFx;''',
'''        var styleFx = BuildStyleEffectFilter(project, scene);
        var tailFx = string.IsNullOrWhiteSpace(styleFx) ? "" : "," + styleFx;''', 'parallax style fx')
s = rep(s,
'''        return $"-y -loop 1 -i \\\"{media}\\\" -t {duration} -filter_complex \\\"{filter}\\\" -map \\\"[outv]\\\" -an -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \\\"{output}\\\"";''',
'''        var profile = GetRenderProfile(project);
        return $"-y -loop 1 -i \\\"{media}\\\" -t {duration} -filter_complex \\\"{filter}\\\" -map \\\"[outv]\\\" -an -c:v libx264 -preset {profile.Preset} -crf {profile.Crf} -pix_fmt yuv420p \\\"{output}\\\"";''', 'parallax profile')

# Replace style effect method, motion chooser and transition chooser.
s = sub(s,
r'''    private static string BuildStyleEffectFilter\(VideoProject project\)\n    \{.*?\n    \}\n\n    private static string ChooseMotionPreset''',
'''    private static string BuildStyleEffectFilter(VideoProject project, ProjectScene? scene = null)
    {
        if (!project.Options.CinematicEffects) return "";
        var style = (project.Style ?? "").ToLowerInvariant();
        var strength = EffectStrength(project.Options, scene);
        var noise = Math.Clamp((int)Math.Round(1 + strength * 2), 1, 6);
        var vignette = F(0.24 + 0.07 * strength);

        if (!project.Options.StyleAwareEffects)
            return $"eq=contrast={F(1.015 + 0.012 * strength)}:saturation={F(1.0 + 0.01 * strength)},vignette={vignette}";

        if (style.Contains("cinematic comic"))
            return $"eq=contrast={F(1.04 + 0.014 * strength)}:saturation={F(1.08 + 0.02 * strength)}:brightness={F(0.004 * strength)},unsharp=5:5:{F(0.18 + 0.05 * strength)}:5:5:0,vignette={F(0.18 + 0.04 * strength)}";
        if (style.Contains("comic book"))
            return $"eq=contrast={F(1.07 + 0.018 * strength)}:saturation={F(1.04 + 0.018 * strength)},unsharp=5:5:{F(0.24 + 0.06 * strength)}:5:5:0,noise=alls={Math.Max(1, noise - 1)}:allf=t+u";
        if (style.Contains("mystery") || style.Contains("conspiracy"))
            return $"eq=contrast={F(1.035 + 0.015 * strength)}:brightness={F(-0.006 * strength)}:saturation={F(0.95 - 0.025 * strength)},vignette={F(0.32 + 0.08 * strength)},noise=alls={noise}:allf=t+u";
        if (style.Contains("history") || style.Contains("archival"))
            return $"eq=contrast={F(1.025 + 0.012 * strength)}:saturation={F(0.94 - 0.025 * strength)},noise=alls={noise}:allf=t+u,vignette={vignette}";
        if (style.Contains("editorial cartoon"))
            return $"eq=contrast={F(1.025 + 0.010 * strength)}:saturation={F(1.025 + 0.012 * strength)},unsharp=5:5:{F(0.14 + 0.04 * strength)}:5:5:0";
        if (style.Contains("vox") || style.Contains("paper") || style.Contains("editorial"))
            return $"eq=contrast={F(1.035 + 0.012 * strength)}:saturation={F(0.97 - 0.018 * strength)},unsharp=5:5:{F(0.18 + 0.08 * strength)}:5:5:0,noise=alls={Math.Max(1, noise - 1)}:allf=t+u";
        if (style.Contains("whiteboard") || style.Contains("stickman"))
            return $"eq=contrast={F(1.02 + 0.012 * strength)}:saturation={F(1.0 + 0.008 * strength)}";
        if (style.Contains("hand-drawn") || style.Contains("hand drawn"))
            return $"eq=contrast={F(1.025 + 0.012 * strength)}:saturation={F(0.98 - 0.012 * strength)},noise=alls={Math.Max(1, noise - 1)}:allf=t+u";
        if (style.Contains("cartoon"))
            return $"eq=contrast={F(1.02 + 0.012 * strength)}:saturation={F(1.02 + 0.015 * strength)},unsharp=5:5:{F(0.12 + 0.05 * strength)}:5:5:0";

        return $"eq=contrast={F(1.02 + 0.012 * strength)}:saturation={F(0.99 - 0.01 * strength)},vignette={vignette},noise=alls={Math.Max(1, noise - 1)}:allf=t+u";
    }

    private static string ChooseMotionPreset''', 'style effect method')

s = sub(s,
r'''    private static string ChooseMotionPreset\(VideoProject project, ProjectScene scene, RenderEffectsState state\)\n    \{.*?\n    \}\n\n    private static string ChooseTransitionPreset''',
'''    private static string ChooseMotionPreset(VideoProject project, ProjectScene scene, RenderEffectsState state, bool allowDirector = true)
    {
        if (!project.Options.AddMotion || project.Options.MotionStyle.Equals("None", StringComparison.OrdinalIgnoreCase))
            return "static";

        if (allowDirector && project.Options.AiEditingDirector)
        {
            if (scene.UseParallax == true && project.Options.SmartParallax) return RememberMotion("parallax", state, project.Options.AvoidRepeatedEffects);
            var directedMotion = NormalizeMotion(scene.EditMotion);
            if (directedMotion is not null && (directedMotion != "parallax" || project.Options.SmartParallax))
                return RememberMotion(directedMotion, state, project.Options.AvoidRepeatedEffects);
        }

        var style = (project.Style ?? "").ToLowerInvariant();
        var requested = project.Options.MotionStyle ?? "Automatic";
        List<string> candidates;
        if (requested.Equals("Cinematic", StringComparison.OrdinalIgnoreCase))
            candidates = ["push-in", "drift", "pull-back", "parallax", "static", "pan-horizontal"];
        else if (requested.Equals("Documentary", StringComparison.OrdinalIgnoreCase))
            candidates = ["push-in", "pan-horizontal", "drift", "static", "pull-back", "parallax"];
        else if (requested.Equals("Dynamic", StringComparison.OrdinalIgnoreCase))
            candidates = ["push-in", "pan-horizontal", "pan-vertical", "drift", "handheld", "parallax"];
        else if (requested.Equals("Minimal", StringComparison.OrdinalIgnoreCase))
            candidates = ["push-in", "static", "pull-back", "pan-horizontal"];
        else if (project.Options.RoleAwareEditing && scene.Role.Equals("hook", StringComparison.OrdinalIgnoreCase))
            candidates = ["push-in", "drift", "parallax", "pan-horizontal"];
        else if (project.Options.RoleAwareEditing && scene.Role.Equals("reveal", StringComparison.OrdinalIgnoreCase))
            candidates = ["parallax", "push-in", "static", "drift"];
        else if (project.Options.RoleAwareEditing && (scene.Role.Equals("ending", StringComparison.OrdinalIgnoreCase) || scene.Role.Equals("transition", StringComparison.OrdinalIgnoreCase)))
            candidates = ["pull-back", "pan-horizontal", "static", "drift"];
        else if (style.Contains("cinematic comic"))
            candidates = ["parallax", "push-in", "drift", "pan-horizontal", "pull-back", "static"];
        else if (style.Contains("comic book"))
            candidates = ["push-in", "pan-horizontal", "static", "parallax", "pull-back"];
        else if (style.Contains("whiteboard") || style.Contains("stickman") || style.Contains("hand-drawn") || style.Contains("hand drawn"))
            candidates = ["push-in", "pan-horizontal", "static", "pull-back", "pan-vertical"];
        else if (style.Contains("mystery") || style.Contains("conspiracy"))
            candidates = ["push-in", "drift", "static", "handheld", "pull-back", "parallax"];
        else
            candidates = ["push-in", "pan-horizontal", "drift", "static", "pull-back", "parallax", "pan-vertical"];

        if (!project.Options.SmartParallax)
            candidates.RemoveAll(x => x == "parallax");

        var shotDirected = MapShotDirection(scene.ShotDirection);
        if (shotDirected is not null && (shotDirected != "parallax" || project.Options.SmartParallax))
        {
            candidates.Remove(shotDirected);
            candidates.Insert(0, shotDirected);
        }

        return PickWithHistory(candidates, state.RecentMotions, ref state.MotionCursor, project.Options.AvoidRepeatedEffects, 3);
    }

    private static string ChooseTransitionPreset''', 'motion chooser')

s = sub(s,
r'''    private static string ChooseTransitionPreset\(VideoProject project, RenderEffectsState state\)\n    \{.*?\n    \}\n\n    private static List<string> AutomaticTransitions''',
'''    private static string ChooseTransitionPreset(VideoProject project, ProjectScene scene, RenderEffectsState state, bool allowDirector = true)
    {
        if (!project.Options.SceneTransitions || project.Options.TransitionStyle.Equals("None", StringComparison.OrdinalIgnoreCase))
            return "fade";

        if (allowDirector && project.Options.AiEditingDirector)
        {
            var directed = NormalizeTransition(scene.EditTransition);
            if (directed is not null) return RememberTransition(directed, state, project.Options.AvoidRepeatedEffects);
        }

        var requested = project.Options.TransitionStyle ?? "Automatic";
        List<string> candidates = requested.ToLowerInvariant() switch
        {
            "crossfade" => ["fade"],
            "fade black" => ["fadeblack"],
            "fade white" => ["fadewhite"],
            "slide" => ["slideleft", "slideright"],
            "push" => ["smoothleft", "smoothright"],
            "zoom" => ["zoomin", "fade"],
            "blur" => ["hblur", "fade"],
            "paper swipe" => ["wipeleft", "diagtl", "slideright"],
            "marker wipe" => ["wipeleft", "wipeup", "smoothleft"],
            _ => AutomaticTransitions(project.Style, scene.Role, project.Options.RoleAwareEditing)
        };
        return PickWithHistory(candidates, state.RecentTransitions, ref state.TransitionCursor, project.Options.AvoidRepeatedEffects, 2);
    }

    private static List<string> AutomaticTransitions''', 'transition chooser')

s = sub(s,
r'''    private static List<string> AutomaticTransitions\(string\? style\)\n    \{.*?\n    \}\n\n    private static string\? MapShotDirection''',
'''    private static List<string> AutomaticTransitions(string? style, string? role = null, bool roleAware = true)
    {
        var s = (style ?? "").ToLowerInvariant();
        var r = (role ?? "").ToLowerInvariant();
        if (roleAware && r == "reveal") return ["dissolve", "smoothleft", "fade", "zoomin"];
        if (roleAware && r == "ending") return ["fade", "fadeblack", "dissolve"];
        if (s.Contains("cinematic comic")) return ["dissolve", "smoothleft", "fade", "smoothright"];
        if (s.Contains("comic book")) return ["smoothleft", "slideleft", "dissolve", "fade"];
        if (s.Contains("editorial cartoon")) return ["fade", "dissolve", "smoothleft", "smoothright"];
        if (s.Contains("vox") || s.Contains("paper") || s.Contains("editorial")) return ["fade", "dissolve", "smoothleft", "wipeleft"];
        if (s.Contains("whiteboard") || s.Contains("hand-drawn") || s.Contains("hand drawn")) return ["fade", "smoothleft", "wipeleft"];
        if (s.Contains("cartoon") || s.Contains("stickman")) return ["fade", "dissolve", "smoothleft", "smoothright"];
        if (s.Contains("mystery") || s.Contains("conspiracy")) return ["fadeblack", "fade", "dissolve", "hblur"];
        return ["fade", "dissolve", "smoothleft", "fadeblack"];
    }

    private static string? MapShotDirection''', 'automatic transitions')

# Replace EffectStrength and NormalizeSceneDurations section with expanded helper block.
s = sub(s,
r'''    private static double EffectStrength\(ProjectOptions options\)\n        => .*?;\n\n    private static string F\(double value\) => value.ToString\("0.#####", CultureInfo.InvariantCulture\);''',
'''    private static double EffectStrength(ProjectOptions options, ProjectScene? scene = null)
    {
        var selected = scene is not null && options.AiEditingDirector && !string.IsNullOrWhiteSpace(scene.EditIntensity) && !scene.EditIntensity.Equals("Automatic", StringComparison.OrdinalIgnoreCase)
            ? scene.EditIntensity
            : options.EffectIntensity;
        return (selected ?? "Subtle").ToLowerInvariant() switch
        {
            "strong" => 2.0,
            "medium" => 1.45,
            _ => 1.0
        };
    }

    private static string? NormalizeMotion(string? motion)
    {
        var m = (motion ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(m) || m == "automatic") return null;
        if (new[] { "push-in", "pull-back", "pan-horizontal", "pan-vertical", "drift", "handheld", "parallax", "static" }.Contains(m)) return m;
        return MapShotDirection(motion);
    }

    private static string? NormalizeTransition(string? transition)
    {
        var t = (transition ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(t) || t == "automatic") return null;
        return new[] { "fade", "fadeblack", "fadewhite", "dissolve", "smoothleft", "smoothright", "slideleft", "slideright", "wipeleft", "wipeup", "zoomin", "hblur" }.Contains(t) ? t : null;
    }

    private static string RememberMotion(string motion, RenderEffectsState state, bool avoid)
    {
        if (avoid && state.RecentMotions.Contains(motion))
            return motion; // explicit director choices win, but still enter history.
        state.RecentMotions.Enqueue(motion);
        while (state.RecentMotions.Count > 3) state.RecentMotions.Dequeue();
        return motion;
    }

    private static string RememberTransition(string transition, RenderEffectsState state, bool avoid)
    {
        state.RecentTransitions.Enqueue(transition);
        while (state.RecentTransitions.Count > 2) state.RecentTransitions.Dequeue();
        return transition;
    }

    private static string F(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);''', 'effect strength/helpers')

s = sub(s,
r'''    private static void NormalizeSceneDurations\(ProjectChapter chapter, double actualAudioSeconds\)\n    \{.*?\n    \}\n''',
'''    private static void NormalizeSceneDurations(ProjectChapter chapter, double actualAudioSeconds, ProjectOptions options)
    {
        if (chapter.Scenes.Count == 0) return;

        if (!options.SmartSceneTiming)
        {
            var total = chapter.Scenes.Sum(s => Math.Max(1, s.TargetSeconds));
            var factor = actualAudioSeconds / Math.Max(1, total);
            foreach (var scene in chapter.Scenes)
                scene.TargetSeconds = Math.Max(1, scene.TargetSeconds * factor);
            chapter.TargetSeconds = (int)Math.Round(actualAudioSeconds);
            return;
        }

        var weights = chapter.Scenes.Select(scene =>
        {
            var words = Math.Max(1, Regex.Matches(scene.Narration ?? "", @"\\b[\\p{L}\\p{N}][\\p{L}\\p{N}'’-]*\\b").Count);
            var role = (scene.Role ?? "").ToLowerInvariant();
            var roleFactor = role switch
            {
                "hook" => 1.08,
                "reveal" => 1.10,
                "ending" => 1.06,
                "transition" => 0.90,
                _ => 1.0
            };
            return words * roleFactor;
        }).ToArray();

        var totalWeight = Math.Max(1.0, weights.Sum());
        var raw = weights.Select(w => Math.Max(1.25, actualAudioSeconds * w / totalWeight)).ToArray();
        var correction = actualAudioSeconds / Math.Max(1.0, raw.Sum());
        for (var i = 0; i < chapter.Scenes.Count; i++)
            chapter.Scenes[i].TargetSeconds = Math.Max(1.0, raw[i] * correction);
        chapter.TargetSeconds = (int)Math.Round(actualAudioSeconds);
    }
''', 'smart timing')

# Add Phase 2 helper methods before FindNearestTimelineMedia.
s = rep(s,
'''    private static TimelineMedia? FindNearestTimelineMedia(VideoProject project, ProjectChapter currentChapter, ProjectScene currentScene)
    {''',
'''    private async Task<string> MasterNarrationAsync(string source, int chapterNumber, ZeroVideoSettings settings, string renderDir, Action<string>? log, CancellationToken ct)
    {
        var output = Path.Combine(renderDir, $"chapter-{chapterNumber:00}-mastered.m4a");
        var filter = "highpass=f=70,acompressor=threshold=0.125:ratio=2.5:attack=20:release=250,loudnorm=I=-16:TP=-1.5:LRA=11,alimiter=limit=0.95";
        var result = await runner.RunAsync(settings.FfmpegPath, $"-y -i \\\"{source}\\\" -vn -af \\\"{filter}\\\" -c:a aac -b:a 192k \\\"{output}\\\"", renderDir, ct);
        if (result.ExitCode != 0)
        {
            log?.Invoke($"Audio mastering failed for chapter {chapterNumber}; using validated raw narration instead.");
            return source;
        }
        log?.Invoke($"Chapter {chapterNumber}: narration mastered to a consistent -16 LUFS target with compression/limiting.");
        return output;
    }

    private async Task ValidateFinalVideoAsync(string path, VideoProject project, ZeroVideoSettings settings, RenderProfile profile, Action<string>? log, CancellationToken ct)
    {
        var duration = await ProbeDurationAsync(path, settings, ct);
        var expected = 0.0;
        foreach (var chapter in project.Chapters)
            if (!string.IsNullOrWhiteSpace(chapter.AudioPath) && File.Exists(chapter.AudioPath))
                expected += await ProbeDurationAsync(chapter.AudioPath, settings, ct);

        var videoInfo = await runner.RunAsync(settings.FfprobePath, $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=s=x:p=0 \\\"{path}\\\"", ct: ct);
        var resolution = videoInfo.StdOut.Trim();
        if (!resolution.Equals($"{profile.Width}x{profile.Height}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Final QC failed: expected {profile.Width}x{profile.Height}, got {resolution}.");
        if (duration <= 1)
            throw new InvalidOperationException("Final QC failed: final video duration is invalid.");
        if (expected > 0 && Math.Abs(duration - expected) > Math.Max(8.0, expected * 0.04))
            throw new InvalidOperationException($"Final QC failed: video duration {duration:0.0}s differs too much from narration total {expected:0.0}s.");

        log?.Invoke($"FINAL QC PASS — video {duration:0.0}s, audio present/audible, resolution {resolution}, chapters {project.Chapters.Count}/{project.Chapters.Count}.");
    }

    private static RenderProfile GetRenderProfile(VideoProject project)
    {
        var vertical = project.AspectRatio == "9:16";
        return (project.Options.RenderQuality ?? "Final").ToLowerInvariant() switch
        {
            "draft" => vertical ? new RenderProfile(720, 1280, "ultrafast", 28) : new RenderProfile(1280, 720, "ultrafast", 28),
            "normal" => vertical ? new RenderProfile(1080, 1920, "fast", 21) : new RenderProfile(1920, 1080, "fast", 21),
            _ => vertical ? new RenderProfile(1080, 1920, "medium", 18) : new RenderProfile(1920, 1080, "medium", 18)
        };
    }

    private static string CaptionForceStyle(string? style, bool vertical)
    {
        var size = vertical ? 20 : 18;
        return (style ?? "Documentary").ToLowerInvariant() switch
        {
            "minimal" => $"FontName=Arial,FontSize={size},PrimaryColour=&H00FFFFFF,Outline=0,Shadow=1,Alignment=2,MarginV=42",
            "bold" => $"FontName=Arial,FontSize={size + 3},Bold=1,PrimaryColour=&H00FFFFFF,Outline=3,Shadow=1,Alignment=2,MarginV=45",
            _ => $"FontName=Arial,FontSize={size},PrimaryColour=&H00FFFFFF,OutlineColour=&H00000000,Outline=2,Shadow=1,Alignment=2,MarginV=44"
        };
    }

    private static string BuildRenderCacheKey(string media, double seconds, int w, int h, string motion, string transition, VideoProject project, ProjectScene scene)
    {
        var info = new FileInfo(media);
        var raw = string.Join("|", media, info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
            F(seconds), w, h, motion, transition, project.Style, project.Options.RenderQuality, project.Options.EffectIntensity,
            project.Options.CinematicEffects, project.Options.ContinuousMotion, project.Options.MotionCycleSeconds,
            project.Options.SmartParallax, scene.EditIntensity, scene.Role);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static bool IsRenderCacheHit(string output, string cacheKey)
    {
        var sidecar = output + ".cache";
        return File.Exists(output) && File.Exists(sidecar) && string.Equals(File.ReadAllText(sidecar).Trim(), cacheKey, StringComparison.Ordinal);
    }

    private static void WriteRenderCache(string output, string cacheKey)
    {
        try { File.WriteAllText(output + ".cache", cacheKey); } catch { }
    }

    private static TimelineMedia? FindNearestTimelineMedia(VideoProject project, ProjectChapter currentChapter, ProjectScene currentScene)
    {''', 'phase2 helpers')

# Add record near existing records/classes at end by inserting before TimelineMedia record if present.
if 'private readonly record struct RenderProfile' not in s:
    marker = '    private sealed record TimelineMedia'
    if marker in s:
        s = s.replace(marker, '    private readonly record struct RenderProfile(int Width, int Height, string Preset, int Crf);\n\n' + marker, 1)
    else:
        # fallback before class closing - use known RenderedClip record anchor
        marker = '    private sealed record RenderedClip'
        if marker in s:
            s = s.replace(marker, '    private readonly record struct RenderProfile(int Width, int Height, string Preset, int Crf);\n\n' + marker, 1)
        else:
            raise SystemExit('Missing record anchor for RenderProfile')

save(p, s)

# -----------------------------------------------------------------------------
# MainWindow: Smart Edit tab
# -----------------------------------------------------------------------------
p = 'src/ZeroVideo.App/MainWindow.xaml'
s = load(p)
smart_tab = r'''
      <TabItem Header="Smart Edit">
        <Grid Margin="0,12,0,0"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="16"/><RowDefinition Height="*"/></Grid.RowDefinitions>
          <Grid><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="16"/><ColumnDefinition/></Grid.ColumnDefinitions>
            <Border Style="{StaticResource CardBorder}">
              <StackPanel>
                <TextBlock Text="AI Editing Director" FontSize="21" FontWeight="SemiBold"/>
                <TextBlock Text="Use narration role and AI scene direction to choose motion, transitions, parallax and emphasis without repeating the same edit." Foreground="{StaticResource Muted}" TextWrapping="Wrap" Margin="0,5,0,10"/>
                <CheckBox Content="AI Editing Director" IsChecked="{Binding Project.Options.AiEditingDirector}"/>
                <CheckBox Content="Smart Scene Timing" IsChecked="{Binding Project.Options.SmartSceneTiming}" ToolTip="Allocate the real chapter audio duration by narration word count and scene role."/>
                <CheckBox Content="Role-aware Editing" IsChecked="{Binding Project.Options.RoleAwareEditing}"/>
                <CheckBox Content="Image Consistency Mode" IsChecked="{Binding Project.Options.ImageConsistencyMode}"/>
                <Grid Margin="18,8,0,0"><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="100"/></Grid.ColumnDefinitions>
                  <TextBlock Text="Visual refresh every" VerticalAlignment="Center"/>
                  <StackPanel Grid.Column="1" Orientation="Horizontal"><TextBox Width="58" Text="{Binding Project.Options.VisualRefreshSeconds, UpdateSourceTrigger=LostFocus}" HorizontalContentAlignment="Center"/><TextBlock Text=" sec" Foreground="{StaticResource Muted}" VerticalAlignment="Center"/></StackPanel>
                </Grid>
              </StackPanel>
            </Border>
            <Border Grid.Column="2" Style="{StaticResource CardBorder}">
              <StackPanel>
                <TextBlock Text="Render &amp; Audio" FontSize="21" FontWeight="SemiBold"/>
                <CheckBox Content="Audio Mastering (-16 LUFS target)" IsChecked="{Binding Project.Options.AudioMastering}" Margin="0,10,0,0"/>
                <CheckBox Content="Automatic Music Ducking" IsChecked="{Binding Project.Options.AutoMusicDucking}"/>
                <CheckBox Content="Render Cache" IsChecked="{Binding Project.Options.RenderCache}"/>
                <CheckBox Content="Visual QA before render" IsChecked="{Binding Project.Options.VisualQa}"/>
                <CheckBox Content="Final QC verification" IsChecked="{Binding Project.Options.FinalQc}"/>
                <CheckBox Content="Burn captions into video" IsChecked="{Binding Project.Options.BurnInCaptions}"/>
                <Grid Margin="0,7,0,0"><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
                  <StackPanel><Label Content="RENDER QUALITY"/><ComboBox SelectedValue="{Binding Project.Options.RenderQuality, Mode=TwoWay}" SelectedValuePath="Content"><ComboBoxItem Content="Draft"/><ComboBoxItem Content="Normal"/><ComboBoxItem Content="Final"/></ComboBox></StackPanel>
                  <StackPanel Grid.Column="1" Margin="10,0,0,0"><Label Content="CAPTION STYLE"/><ComboBox SelectedValue="{Binding Project.Options.CaptionStyle, Mode=TwoWay}" SelectedValuePath="Content"><ComboBoxItem Content="Documentary"/><ComboBoxItem Content="Minimal"/><ComboBoxItem Content="Bold"/></ComboBox></StackPanel>
                </Grid>
              </StackPanel>
            </Border>
          </Grid>

          <Border Grid.Row="2" Style="{StaticResource CardBorder}">
            <Grid><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
              <Grid Margin="0,0,0,10"><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                <StackPanel><TextBlock Text="Scene Editor" FontSize="21" FontWeight="SemiBold"/><TextBlock Text="Override AI edit choices before Build video. Automatic leaves the decision to ZeroVideo." Foreground="{StaticResource Muted}"/></StackPanel>
                <Button Grid.Column="1" Style="{StaticResource SecondaryButton}" Command="{Binding SaveProjectEditsCommand}" Content="Save scene edits"/>
              </Grid>
              <DataGrid Grid.Row="1" ItemsSource="{Binding AllScenes}" AutoGenerateColumns="False" CanUserAddRows="False" AlternationCount="2">
                <DataGrid.Columns>
                  <DataGridTextColumn Header="Ch" Binding="{Binding ChapterNumber}" Width="45" IsReadOnly="True"/>
                  <DataGridTextColumn Header="#" Binding="{Binding Number}" Width="42" IsReadOnly="True"/>
                  <DataGridTextColumn Header="Role" Binding="{Binding Role, UpdateSourceTrigger=PropertyChanged}" Width="85"/>
                  <DataGridTextColumn Header="Seconds" Binding="{Binding TargetSeconds, UpdateSourceTrigger=PropertyChanged}" Width="75"/>
                  <DataGridTextColumn Header="Motion" Binding="{Binding EditMotion, UpdateSourceTrigger=PropertyChanged}" Width="120"/>
                  <DataGridTextColumn Header="Transition" Binding="{Binding EditTransition, UpdateSourceTrigger=PropertyChanged}" Width="120"/>
                  <DataGridCheckBoxColumn Header="Parallax" Binding="{Binding UseParallax, Mode=TwoWay}" Width="70"/>
                  <DataGridTextColumn Header="Intensity" Binding="{Binding EditIntensity, UpdateSourceTrigger=PropertyChanged}" Width="85"/>
                  <DataGridTextColumn Header="Editor note" Binding="{Binding EditorNote, UpdateSourceTrigger=PropertyChanged}" Width="*"/>
                </DataGrid.Columns>
              </DataGrid>
            </Grid>
          </Border>
        </Grid>
      </TabItem>

'''
s = rep(s, '      <TabItem Header="Automation">', smart_tab + '      <TabItem Header="Automation">', 'smart edit tab')
save(p, s)

print('Phase 2 patch applied successfully.')
