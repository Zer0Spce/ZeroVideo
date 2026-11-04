using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class FfmpegService(ProcessRunner runner, DependencyService dependencies, LocalImageService? localImages = null)
{
    private readonly LivePortraitService _livePortrait = new(localImages ?? new LocalImageService());
    public async Task ValidateAsync(ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)
    {
        await EnsureToolsAsync(settings, log, ct);
        var result = await runner.RunAsync(settings.FfmpegPath, "-version", ct: ct);
        if (result.ExitCode != 0) throw new InvalidOperationException("FFmpeg could not be started after installation/configuration.");
    }

    private async Task EnsureToolsAsync(ZeroVideoSettings settings, Action<string>? log, CancellationToken ct)
    {
        settings.FfmpegPath = SettingsService.ResolveTool(settings.FfmpegPath, "ffmpeg.exe");
        settings.FfprobePath = SettingsService.ResolveTool(settings.FfprobePath, "ffprobe.exe");

        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath) && File.Exists(settings.FfmpegPath) &&
            !string.IsNullOrWhiteSpace(settings.FfprobePath) && File.Exists(settings.FfprobePath))
        {
            log?.Invoke($"FFmpeg ready: {settings.FfmpegPath}");
            return;
        }

        log?.Invoke("FFmpeg was not found. Installing it automatically to ZeroVideo's persistent tools folder...");
        var ffmpeg = await dependencies.InstallFfmpegAsync(log, ct);
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
            throw new FileNotFoundException("ZeroVideo finished the FFmpeg install step, but ffmpeg.exe/ffprobe.exe could not be found.");

        settings.FfmpegPath = ffmpeg;
        settings.FfprobePath = ffprobe;
        log?.Invoke("FFmpeg is ready. Continuing the render automatically.");
    }

    public async Task<double> ProbeDurationAsync(string path, ZeroVideoSettings settings, CancellationToken ct = default)
    {
        var args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"";
        var result = await runner.RunAsync(settings.FfprobePath, args, ct: ct);
        return double.TryParse(result.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
    }

    public async Task ValidateAudibleAudioAsync(string path, ZeroVideoSettings settings, string label, Action<string>? log = null, CancellationToken ct = default)
    {
        await EnsureToolsAsync(settings, log, ct);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"{label} audio file is missing.", path);

        var streamArgs = $"-v error -select_streams a:0 -show_entries stream=codec_type -of csv=p=0 \"{path}\"";
        var stream = await runner.RunAsync(settings.FfprobePath, streamArgs, ct: ct);
        if (stream.ExitCode != 0 || !stream.StdOut.Contains("audio", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{label} has no audio stream.");

        var duration = await ProbeDurationAsync(path, settings, ct);
        if (duration < 0.5)
            throw new InvalidOperationException($"{label} audio is only {duration:0.00}s long.");

        var volume = await runner.RunAsync(settings.FfmpegPath, $"-hide_banner -nostats -i \"{path}\" -af volumedetect -f null NUL", ct: ct);
        var report = (volume.StdOut ?? "") + "\n" + (volume.StdErr ?? "");
        var match = Regex.Match(report, @"max_volume:\s*(?<v>-?inf|-?\d+(?:\.\d+)?)\s*dB", RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new InvalidOperationException($"{label} audio could not be verified for audible sound.");

        var raw = match.Groups["v"].Value;
        if (raw.Equals("-inf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{label} is completely silent.");

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxDb))
            throw new InvalidOperationException($"{label} audio level could not be parsed.");
        if (maxDb <= -60)
            throw new InvalidOperationException($"{label} is effectively silent (max volume {maxDb:0.0} dB).");

        log?.Invoke($"Audio validation passed: {label} ({duration:0.0}s, max {maxDb:0.0} dB).");
    }

    public async Task ValidateNarrationAudioAsync(string path, ZeroVideoSettings settings, string label, string narration, Action<string>? log = null, CancellationToken ct = default)
    {
        await ValidateAudibleAudioAsync(path, settings, label, log, ct);
        var duration = await ProbeDurationAsync(path, settings, ct);
        var words = Regex.Matches(narration ?? "", @"\b[\p{L}\p{N}][\p{L}\p{N}'’-]*\b").Count;
        var minimumExpectedSeconds = Math.Max(8.0, words / 5.0); // reject Clipchamp's audible-but-wrong 5-second placeholder
        if (duration < minimumExpectedSeconds)
            throw new InvalidOperationException($"{label} is only {duration:0.0}s for {words} narration word(s). Expected at least about {minimumExpectedSeconds:0.0}s; this is probably Clipchamp's 5-second placeholder export.");
        log?.Invoke($"Narration-duration validation passed: {label} ({duration:0.0}s for {words} word(s)).");
    }

    public async Task<string> RenderAsync(VideoProject project, ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)
    {
        await ValidateAsync(settings, log, ct);
        var renderDir = Path.Combine(project.ProjectDirectory, "render");
        var exportsDir = Path.Combine(project.ProjectDirectory, "exports");
        Directory.CreateDirectory(renderDir);
        Directory.CreateDirectory(exportsDir);

        var chapterOutputs = new List<string>();
        var chapterTimings = new List<NarrationTimelineService.ChapterTiming>();
        var effects = new RenderEffectsState();
        foreach (var chapter in project.Chapters)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(chapter.AudioPath) || !File.Exists(chapter.AudioPath))
                throw new FileNotFoundException($"Missing audio for chapter {chapter.Number}.", chapter.AudioPath);

            var audioSeconds = await ProbeDurationAsync(chapter.AudioPath, settings, ct);
            if (audioSeconds <= 0) audioSeconds = Math.Max(1, chapter.TargetSeconds);
            // One authoritative timing plan for the scene visuals, ASS
            // captions and exported SRT. Distribute scene time based on
            // actual spoken script and the chapter's detected voice pauses.
            var voiceActivity = await DetectNarrationSpeechAsync(
                chapter, settings, audioSeconds, log, ct);
            var timing = NarrationTimelineService.Plan(
                chapter, audioSeconds, voiceActivity);
            chapterTimings.Add(timing);
            for (var i = 0; i < chapter.Scenes.Count; i++)
                chapter.Scenes[i].TargetSeconds = timing.Scenes[i].Duration;
            chapter.TargetSeconds = (int)Math.Round(audioSeconds);
            if (!NarrationTimelineService.ScriptsMatch(chapter))
                log?.Invoke($"Chapter {chapter.Number}: saved scene text differs " +
                    "from the original chapter TTS. Caption text will follow " +
                    "the actual spoken chapter script rather than mixing scene drafts.");
            log?.Invoke($"Chapter {chapter.Number}: aligned {timing.Scenes.Count} " +
                $"scene(s) to {audioSeconds:0.00}s narration with " +
                $"{timing.Speech.Count} detected speech interval(s).");

            var visual = await RenderChapterVisualAsync(project, chapter, settings, renderDir, effects, log, ct);
            var output = Path.Combine(renderDir, $"chapter-{chapter.Number:00}-final.mp4");
            var muxArgs = $"-y -i \"{visual}\" -i \"{chapter.AudioPath}\" -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 192k -shortest \"{output}\"";
            var mux = await runner.RunAsync(settings.FfmpegPath, muxArgs, renderDir, ct);
            if (mux.ExitCode != 0) throw new InvalidOperationException($"FFmpeg chapter mux failed: {mux.StdErr}");
            chapterOutputs.Add(output);
        }

        var concatList = Path.Combine(renderDir, "chapters.txt");
        await File.WriteAllLinesAsync(concatList, chapterOutputs.Select(p => $"file '{p.Replace("'", "'\\''")}'"), ct);
        var voiceFinal = Path.Combine(renderDir, "video-with-voice.mp4");
        var concat = await runner.RunAsync(settings.FfmpegPath, $"-y -f concat -safe 0 -i \"{concatList}\" -map 0:v:0 -map 0:a:0 -c:v copy -c:a aac -b:a 192k \"{voiceFinal}\"", renderDir, ct);
        if (concat.ExitCode != 0) throw new InvalidOperationException($"FFmpeg concat failed: {concat.StdErr}");

        var finalPath = Path.Combine(exportsDir, Sanitize(project.Title) + ".mp4");
        if (project.Options.AddBackgroundMusic)
        {
            var musicPath = project.Options.BackgroundMusicPath;
            if (string.IsNullOrWhiteSpace(musicPath) || !File.Exists(musicPath))
                throw new FileNotFoundException("Background music was enabled but its file is missing.", musicPath);
            var volume = project.Options.BackgroundMusicVolume.ToString("0.###", CultureInfo.InvariantCulture);
            log?.Invoke("Mixing custom background music: " + Path.GetFileName(musicPath) +
                " (linear gain " + volume + ", looped to video duration).");
            var args = $"-y -i \"{voiceFinal}\" -stream_loop -1 -i \"{musicPath}\" -filter_complex \"[1:a]volume={volume}[bg];[0:a][bg]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[a]\" -map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k -shortest \"{finalPath}\"";
            var mixed = await runner.RunAsync(settings.FfmpegPath, args, renderDir, ct);
            if (mixed.ExitCode != 0) throw new InvalidOperationException("Music mix failed: " + mixed.StdErr);
            log?.Invoke("Custom music mix completed in final MP4.");
        }
        else
        {
            log?.Invoke("Background music disabled; exporting narration-only video.");
            File.Copy(voiceFinal, finalPath, true);
        }

        if (project.Options.GenerateCaptions)
            await WriteSrtAsync(project, Path.ChangeExtension(finalPath, ".srt"),
                chapterTimings, ct);

        // Longform is 16:9; the previous 9:16-only condition wrote .srt
        // but NEVER burned captions into its MP4. Use aspect-appropriate
        // ASS for both automation modes, retaining the existing opt-out.
        if (CaptionRenderPolicy.ShouldBurn(project.Options,project.AspectRatio))
        {
            var captionAss = Path.Combine(renderDir,"styled-video-captions.ass");
            if (await WriteBoldCaptionAssAsync(
                project,chapterTimings,captionAss,log,ct))
            {
                var candidate = Path.Combine(renderDir,
                    "video-with-styled-captions.mp4");
                await BurnCaptionAssAsync(
                    finalPath,captionAss,candidate,settings,renderDir,log,ct);
                File.Copy(candidate,finalPath,true);
                File.Delete(candidate);
                log?.Invoke("Visible narration-synced captions embedded " +
                    "in final "+project.AspectRatio+" MP4.");
            }
            else
                log?.Invoke("Caption burn was selected but no script words " +
                    "were available. Check the saved chapter narration.");
        }
        else
            log?.Invoke("Burned captions disabled for this project; "+
                (project.Options.GenerateCaptions?"SRT sidecar only.":"no captions selected."));

        await ValidateAudibleAudioAsync(finalPath, settings, "Final video", log, ct);
        return finalPath;
    }

    private async Task<string> RenderChapterVisualAsync(VideoProject project, ProjectChapter chapter, ZeroVideoSettings settings, string renderDir, RenderEffectsState effects, Action<string>? log, CancellationToken ct)
    {
        var (w, h) = project.AspectRatio == "9:16" ? (1080, 1920) : (1920, 1080);
        var clips = new List<RenderedClip>();
        var configuredTransition = project.Options.SceneTransitions
            ? Math.Clamp(project.Options.TransitionDurationSeconds, 0.15, 0.8)
            : 0.0;

        foreach (var scene in chapter.Scenes)
        {
            var mediaItems = ProjectMedia.FindAll(project, chapter, scene).ToList();
            if (mediaItems.Count == 0)
            {
                var fallback = FindScriptAlignedTimelineMedia(project, chapter, scene);
                if (fallback is not null)
                {
                    mediaItems.Add(fallback.Path);
                    log?.Invoke($"Scene {chapter.Number}.{scene.Number}: missing direct media. " +
                        $"Reusing script-relevant visual {fallback.ChapterNumber}.{fallback.SceneNumber} " +
                        "based on matching subject, location and evidence.");
                }
            }

            if (mediaItems.Count == 0)
            {
                var seconds = Math.Max(0.25, scene.TargetSeconds);
                var hasNextScene = !ReferenceEquals(scene, chapter.Scenes[^1]);
                var renderSeconds = seconds + (hasNextScene ? configuredTransition : 0);
                var duration = F(renderSeconds);
                var output = Path.Combine(renderDir, $"c{chapter.Number:00}-s{scene.Number:000}.mp4");
                log?.Invoke($"Scene {chapter.Number}.{scene.Number}: no media exists anywhere in the project; using the branded project backdrop.");
                var args = $"-y -f lavfi -i \"color=c=0x241C30:s={w}x{h}:r=30:d={duration}\" -vf \"drawbox=x=0:y=0:w=iw:h=ih/3:color=0x34254A@0.75:t=fill,drawbox=x=0:y=ih*0.72:w=iw:h=ih*0.28:color=0x17151F@0.82:t=fill\" -an -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \"{output}\"";
                var result = await runner.RunAsync(settings.FfmpegPath, args, renderDir, ct);
                if (result.ExitCode != 0) throw new InvalidOperationException($"Scene render failed ({chapter.Number}.{scene.Number}): {result.StdErr}");
                clips.Add(new RenderedClip(output, renderSeconds, "fade"));
                continue;
            }

            var perVisualSeconds = Math.Max(0.25, scene.TargetSeconds / mediaItems.Count);
            for (var mediaIndex = 0; mediaIndex < mediaItems.Count; mediaIndex++)
            {
                var media = mediaItems[mediaIndex];
                var transition = ChooseTransitionPreset(project, effects);
                // Every scene/visual receives only the transition overlap
                // that will be consumed by the next clip. The final clip
                // has NO extra frames; this keeps the chapter aligned with
                // exactly its TTS audio duration.
                var isLast = ReferenceEquals(scene, chapter.Scenes[^1]) &&
                    mediaIndex == mediaItems.Count - 1;
                var renderSeconds = perVisualSeconds +
                    (isLast ? 0 : configuredTransition);
                var duration = F(renderSeconds);
                var suffix = mediaItems.Count > 1 ? $"-v{mediaIndex + 1:00}" : "";
                var output = Path.Combine(renderDir, $"c{chapter.Number:00}-s{scene.Number:000}{suffix}.mp4");
                string args;
                string motion;

                // LivePortrait animates only detected faces. No generative
                // full-scene video diffusion, and no Wan/FramePack service.
                // Scenes with no face gracefully use existing FFmpeg motion.
                string? aiClip = null;
                var wantsPortrait = settings.AiImageAnimationEngine == "LivePortrait (Local)";
                var maxClips = Math.Clamp(settings.LivePortraitMaxClipsPerVideo, 0, 30);
                if (!IsVideo(media) && wantsPortrait &&
                    effects.AiAttemptCount < maxClips &&
                    await _livePortrait.CanAnimateAsync(media, log, ct))
                {
                    // Only count scenes with a detected, sufficiently large
                    // face. Environment-only scenes must not exhaust the
                    // per-video LivePortrait limit.
                    effects.AiAttemptCount++;
                    aiClip = await _livePortrait.TryAnimateAsync(
                        media, project, scene, settings, log, ct);
                    if (aiClip is not null)
                    {
                        try
                        {
                            if (await ProbeDurationAsync(aiClip, settings, ct) < 1.0)
                                throw new InvalidDataException("Animated face clip too short.");
                            effects.AiGeneratedClipCount++;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            log?.Invoke("LivePortrait clip invalid; using source still: " + ex.Message);
                            aiClip = null;
                        }
                    }
                }
                var renderMedia = aiClip ?? media;
                if (IsVideo(renderMedia))
                {
                    motion = aiClip is null ? "source video"
                        : "AI subtle facial motion (LivePortrait)";
                    var vf = $"scale={w}:{h}:force_original_aspect_ratio=increase:flags=lanczos,crop={w}:{h},fps=30";
                    var styleFx = BuildStyleEffectFilter(project);
                    if (!string.IsNullOrWhiteSpace(styleFx)) vf += "," + styleFx;
                    args = $"-y -stream_loop -1 -i \"{renderMedia}\" -t {duration} -vf \"{vf}\" -an -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \"{output}\"";
                }
                else
                {
                    motion = settings.AiImageAnimationEngine == "Off" ||
                        (settings.AiImageAnimationEngine == "LivePortrait (Local)" &&
                         !settings.LivePortraitFallbackToCamera)
                        ? "static" : ChooseMotionPreset(project, scene, effects);
                    args = BuildStillRenderArgs(media, output, renderSeconds, w, h, motion, project);
                }

                log?.Invoke($"Scene {chapter.Number}.{scene.Number}: effect {motion}; transition {transition}; {renderSeconds:0.0}s render segment.");
                var result = await runner.RunAsync(settings.FfmpegPath, args, renderDir, ct);
                if (result.ExitCode != 0 && aiClip is not null)
                {
                    // An AI clip may decode but still be incompatible with
                    // the main renderer. Retry against the source still.
                    log?.Invoke("AI video render failed; falling back to original illustrated image.");
                    motion = settings.AiImageAnimationEngine == "Off" ||
                        (settings.AiImageAnimationEngine == "LivePortrait (Local)" &&
                         !settings.LivePortraitFallbackToCamera)
                        ? "static" : ChooseMotionPreset(project, scene, effects);
                    args = BuildStillRenderArgs(media, output, renderSeconds, w, h, motion, project);
                    result = await runner.RunAsync(settings.FfmpegPath, args, renderDir, ct);
                }
                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Scene render failed ({chapter.Number}.{scene.Number}, visual {mediaIndex + 1}): {result.StdErr}");

                clips.Add(new RenderedClip(output, renderSeconds, transition));
            }
        }

        return await RenderChapterTimelineAsync(project, chapter, settings, renderDir, clips, configuredTransition, log, ct);
    }

    private async Task<string> RenderChapterTimelineAsync(VideoProject project, ProjectChapter chapter, ZeroVideoSettings settings, string renderDir, List<RenderedClip> clips, double configuredTransition, Action<string>? log, CancellationToken ct)
    {
        if (clips.Count == 0)
            throw new InvalidOperationException($"Chapter {chapter.Number} has no rendered visual clips.");
        if (clips.Count == 1)
            return clips[0].Path;

        var chapterVisual = Path.Combine(renderDir, $"chapter-{chapter.Number:00}-visuals.mp4");
        if (!project.Options.SceneTransitions || project.Options.TransitionStyle.Equals("None", StringComparison.OrdinalIgnoreCase) || configuredTransition <= 0)
            return await ConcatRenderedClipsAsync(chapter, settings, renderDir, clips, chapterVisual, ct);

        var transitionSeconds = Math.Min(configuredTransition, Math.Max(0.12, clips.Min(x => x.DurationSeconds) / 3.0));
        var inputArgs = new StringBuilder();
        for (var i = 0; i < clips.Count; i++) inputArgs.Append($" -i \"{clips[i].Path}\"");

        var filter = new StringBuilder();
        for (var i = 0; i < clips.Count; i++)
            filter.Append($"[{i}:v]settb=AVTB,setpts=PTS-STARTPTS[v{i}];");

        var current = "v0";
        var offset = clips[0].DurationSeconds - transitionSeconds;
        for (var i = 1; i < clips.Count; i++)
        {
            var next = $"x{i}";
            filter.Append($"[{current}][v{i}]xfade=transition={clips[i].Transition}:duration={F(transitionSeconds)}:offset={F(Math.Max(0.05, offset))}[{next}];");
            current = next;
            offset += clips[i].DurationSeconds - transitionSeconds;
        }
        filter.Append($"[{current}]format=yuv420p[outv]");

        var args = $"-y{inputArgs} -filter_complex \"{filter}\" -map \"[outv]\" -an -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \"{chapterVisual}\"";
        var result = await runner.RunAsync(settings.FfmpegPath, args, renderDir, ct);
        if (result.ExitCode == 0)
        {
            log?.Invoke($"Chapter {chapter.Number}: applied {clips.Count - 1} scene transition(s) with anti-repetition.");
            return chapterVisual;
        }

        log?.Invoke($"Chapter {chapter.Number}: advanced transition render failed; " +
            "rebuilding a cut-only scene timeline without the extra " +
            "crossfade frames, preserving audio/visual boundaries. " + result.StdErr);
        return await ConcatRenderedClipsAsync(chapter, settings, renderDir,
            clips, chapterVisual, ct, configuredTransition);
    }

    private async Task<string> ConcatRenderedClipsAsync(
        ProjectChapter chapter, ZeroVideoSettings settings, string renderDir,
        List<RenderedClip> clips, string chapterVisual, CancellationToken ct,
        double removeTransitionOverlap = 0)
    {
        if (removeTransitionOverlap <= 0)
        {
            var list = Path.Combine(renderDir,
                $"chapter-{chapter.Number:00}-visuals.txt");
            await File.WriteAllLinesAsync(list, clips.Select(p =>
                $"file '{p.Path.Replace("'", "'\\''")}'"), ct);
            var concat = await runner.RunAsync(settings.FfmpegPath,
                $"-y -f concat -safe 0 -i \"{list}\" -c copy \"{chapterVisual}\"",
                renderDir, ct);
            if (concat.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Chapter visual concat failed: {concat.StdErr}");
            return chapterVisual;
        }

        // Advanced xfade failed. If we simply concatenate original clips,
        // every clip still contains its unused 0.35s transition tail:
        // scene N's audio then plays over scene N-1's image. Trim each
        // crossfade pad before rebuilding a cut-only 30fps timeline.
        var args = new StringBuilder("-y");
        var filter = new StringBuilder();
        for (var i = 0; i < clips.Count; i++)
        {
            args.Append($" -i \"{clips[i].Path}\"");
            var duration = Math.Max(0.10, clips[i].DurationSeconds -
                (i < clips.Count - 1 ? removeTransitionOverlap : 0));
            filter.Append($"[{i}:v]trim=duration={F(duration)},")
                  .Append($"setpts=PTS-STARTPTS,fps=30[v{i}];");
        }
        for (var i = 0; i < clips.Count; i++)
            filter.Append($"[v{i}]");
        filter.Append($"concat=n={clips.Count}:v=1:a=0[outv]");
        args.Append($" -filter_complex \"{filter}\" -map \"[outv]\"")
            .Append($" -an -c:v libx264 -preset medium -crf 18")
            .Append($" -pix_fmt yuv420p \"{chapterVisual}\"");
        var built = await runner.RunAsync(settings.FfmpegPath,
            args.ToString(), renderDir, ct);
        if (built.ExitCode != 0)
            throw new InvalidOperationException(
                $"Scene-aligned fallback concat failed: {built.StdErr}");
        return chapterVisual;
    }

    private static string BuildStillRenderArgs(string media, string output, double seconds, int w, int h, string motion, VideoProject project)
    {
        var duration = F(seconds);
        var frames = Math.Max(30, (int)Math.Ceiling(seconds * 30));
        if (motion == "parallax" && project.Options.SmartParallax)
            return BuildParallaxArgs(media, output, duration, frames, w, h, project);

        var vf = $"scale={w}:{h}:force_original_aspect_ratio=increase:flags=lanczos,crop={w}:{h}";
        if (project.Options.AddMotion && !motion.Equals("static", StringComparison.OrdinalIgnoreCase))
            vf += "," + BuildZoomPanFilter(motion, frames, w, h, project.Options);
        else
            vf += ",fps=30";

        var styleFx = BuildStyleEffectFilter(project);
        if (!string.IsNullOrWhiteSpace(styleFx)) vf += "," + styleFx;
        return $"-y -loop 1 -i \"{media}\" -t {duration} -vf \"{vf}\" -an -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \"{output}\"";
    }

    private static string BuildParallaxArgs(string media, string output, string duration, int frames, int w, int h, VideoProject project)
    {
        var cycle = Math.Clamp(project.Options.MotionCycleSeconds, 2.0, 12.0);
        var step = 2.0 * Math.PI / (cycle * 30.0);
        var s = F(step);
        var strength = EffectStrength(project.Options);
        var bgAmp = F(0.10 + 0.03 * strength);
        var fgAmp = F(0.26 + 0.05 * strength);
        var bgZoom = F(1.018 + 0.005 * strength);
        var fgZoom = F(1.045 + 0.010 * strength);
        var blur = F(5.0 + 2.0 * strength);
        var feather = F(Math.Max(26, Math.Min(w, h) * 0.035));
        var styleFx = BuildStyleEffectFilter(project);
        var tailFx = string.IsNullOrWhiteSpace(styleFx) ? "" : "," + styleFx;

        var filter = $"[0:v]scale={w}:{h}:force_original_aspect_ratio=increase:flags=lanczos,crop={w}:{h},split=2[bg0][fg0];" +
                     $"[bg0]zoompan=z='{bgZoom}+0.009*sin(on*{s})':x='(iw-iw/zoom)*(0.5+{bgAmp}*sin(on*{s}))':y='(ih-ih/zoom)*(0.5+{bgAmp}*cos(on*{s}))':d={frames}:s={w}x{h}:fps=30,gblur=sigma={blur}[bg];" +
                     $"[fg0]zoompan=z='{fgZoom}+0.022*sin(on*{s})':x='(iw-iw/zoom)*(0.5+{fgAmp}*sin(on*{s}))':y='(ih-ih/zoom)*(0.5+{fgAmp}*cos(on*{s}))':d={frames}:s={w}x{h}:fps=30,format=rgba[fg];" +
                     $"color=c=black:s={w}x{h}:r=30:d={duration},format=gray,drawbox=x=iw*0.09:y=ih*0.04:w=iw*0.82:h=ih*0.92:color=white:t=fill,gblur=sigma={feather}[mask];" +
                     $"[fg][mask]alphamerge[fgm];[bg][fgm]overlay=0:0:format=auto{tailFx},fps=30[outv]";

        return $"-y -loop 1 -i \"{media}\" -t {duration} -filter_complex \"{filter}\" -map \"[outv]\" -an -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \"{output}\"";
    }

    private static string BuildZoomPanFilter(string motion, int frames, int w, int h, ProjectOptions options)
    {
        var cycle = Math.Clamp(options.MotionCycleSeconds, 2.0, 12.0);
        var step = 2.0 * Math.PI / (cycle * 30.0);
        var s = F(step);
        var strength = EffectStrength(options);
        // Render camera motion at 2x resolution, then Lanczos-downsample. This gives
        // slow pans/zooms sub-pixel precision instead of visible one-pixel stepping.
        var sw = w * 2;
        var sh = h * 2;
        var pan = F(0.30 + 0.035 * strength);
        var zoomAmp = F(0.016 + 0.006 * strength);
        var zoomBase = F(1.035 + 0.004 * strength);
        var dynamicStep = F(step * 2.5);
        string Smooth(string expr) => expr + $",scale={w}:{h}:flags=lanczos,fps=30";

        if (motion == "subtle-life")
        {
            // Periodic 3–5-second micro-pan, micro-zoom, and ambient light
            // breathing. Every cycle returns to the initial pose without
            // changing the illustrated subject or adding fabricated details.
            var loop = Math.Clamp(options.SubtleImageAnimationSeconds, 3.0, 5.0);
            var phase = F(2.0 * Math.PI / (loop * 30.0));
            return Smooth($"zoompan=z='1.044+0.004*sin(on*{phase})':" +
                $"x='(iw-iw/zoom)*(0.5+0.08*sin(on*{phase}))':" +
                $"y='(ih-ih/zoom)*(0.5+0.06*(1-cos(on*{phase})))':" +
                $"d={frames}:s={sw}x{sh}:fps=30") +
                $",eq=brightness='0.002*sin(n*{phase})':eval=frame";
        }

        if (!options.ContinuousMotion)
        {
            return motion switch
            {
                "pull-back" => Smooth($"zoompan=z='max(1.0,1.085-on*0.00055)':x='(iw-iw/zoom)/2':y='(ih-ih/zoom)/2':d={frames}:s={sw}x{sh}:fps=30"),
                "pan-horizontal" => Smooth($"zoompan=z='1.065':x='(iw-iw/zoom)*(0.5-0.5*cos(PI*on/{Math.Max(1, frames - 1)}))':y='(ih-ih/zoom)/2':d={frames}:s={sw}x{sh}:fps=30"),
                "pan-vertical" => Smooth($"zoompan=z='1.065':x='(iw-iw/zoom)/2':y='(ih-ih/zoom)*(0.5-0.5*cos(PI*on/{Math.Max(1, frames - 1)}))':d={frames}:s={sw}x{sh}:fps=30"),
                _ => Smooth($"zoompan=z='min(1.08,1+on*0.00055)':x='(iw-iw/zoom)/2':y='(ih-ih/zoom)/2':d={frames}:s={sw}x{sh}:fps=30")
            };
        }

        return motion switch
        {
            "pan-horizontal" => Smooth($"zoompan=z='1.065':x='(iw-iw/zoom)*(0.5+{pan}*sin(on*{s}))':y='(ih-ih/zoom)/2':d={frames}:s={sw}x{sh}:fps=30"),
            "pan-vertical" => Smooth($"zoompan=z='1.065':x='(iw-iw/zoom)/2':y='(ih-ih/zoom)*(0.5+{pan}*sin(on*{s}))':d={frames}:s={sw}x{sh}:fps=30"),
            "drift" => Smooth($"zoompan=z='{zoomBase}+{zoomAmp}*sin(on*{s})':x='(iw-iw/zoom)*(0.5+0.25*sin(on*{s}))':y='(ih-ih/zoom)*(0.5+0.20*cos(on*{s}))':d={frames}:s={sw}x{sh}:fps=30"),
            "pull-back" => Smooth($"zoompan=z='{zoomBase}+{zoomAmp}*cos(on*{s})':x='(iw-iw/zoom)/2':y='(ih-ih/zoom)/2':d={frames}:s={sw}x{sh}:fps=30"),
            "handheld" => Smooth($"zoompan=z='1.05':x='(iw-iw/zoom)*(0.5+0.10*sin(on*{dynamicStep}))':y='(ih-ih/zoom)*(0.5+0.08*cos(on*{dynamicStep}))':d={frames}:s={sw}x{sh}:fps=30"),
            _ => Smooth($"zoompan=z='{zoomBase}+{zoomAmp}*sin(on*{s})':x='(iw-iw/zoom)/2':y='(ih-ih/zoom)/2':d={frames}:s={sw}x{sh}:fps=30")
        };
    }

    private static string BuildStyleEffectFilter(VideoProject project)
    {
        if (!project.Options.CinematicEffects) return "";
        var style = (project.Style ?? "").ToLowerInvariant();
        var strength = EffectStrength(project.Options);
        var noise = Math.Clamp((int)Math.Round(1 + strength * 2), 1, 6);
        var vignette = F(0.24 + 0.07 * strength);

        if (!project.Options.StyleAwareEffects)
            return $"eq=contrast={F(1.015 + 0.012 * strength)}:saturation={F(1.0 + 0.01 * strength)},vignette={vignette}";

        if (style.Contains("neo-noir"))
            return $"eq=contrast={F(1.055 + 0.012 * strength)}:saturation={F(0.96 + 0.015 * strength)},vignette={F(0.28 + 0.04 * strength)}";
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

    private static string ChooseMotionPreset(VideoProject project, ProjectScene scene, RenderEffectsState state)
    {
        if (!project.Options.AddMotion || project.Options.MotionStyle.Equals("None", StringComparison.OrdinalIgnoreCase))
            return "static";
        if (project.Options.SubtleImageAnimation)
            return "subtle-life";

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
        else if (style.Contains("whiteboard") || style.Contains("stickman") || style.Contains("hand-drawn") || style.Contains("hand drawn"))
            candidates = ["push-in", "pan-horizontal", "static", "pull-back", "pan-vertical"];
        else if (style.Contains("mystery") || style.Contains("conspiracy"))
            candidates = ["push-in", "drift", "static", "handheld", "pull-back", "parallax"];
        else
            candidates = ["push-in", "pan-horizontal", "drift", "static", "pull-back", "parallax", "pan-vertical"];

        if (!project.Options.SmartParallax)
            candidates.RemoveAll(x => x == "parallax");

        var directed = MapShotDirection(scene.ShotDirection);
        if (directed is not null && (directed != "parallax" || project.Options.SmartParallax))
        {
            candidates.Remove(directed);
            candidates.Insert(0, directed);
        }

        return PickWithHistory(candidates, state.RecentMotions, ref state.MotionCursor, project.Options.AvoidRepeatedEffects, 3);
    }

    private static string ChooseTransitionPreset(VideoProject project, RenderEffectsState state)
    {
        if (!project.Options.SceneTransitions || project.Options.TransitionStyle.Equals("None", StringComparison.OrdinalIgnoreCase))
            return "fade";

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
            _ => AutomaticTransitions(project.Style)
        };
        return PickWithHistory(candidates, state.RecentTransitions, ref state.TransitionCursor, project.Options.AvoidRepeatedEffects, 2);
    }

    private static List<string> AutomaticTransitions(string? style)
    {
        var s = (style ?? "").ToLowerInvariant();
        if (s.Contains("editorial cartoon")) return ["fade", "dissolve", "smoothleft", "smoothright"];
        if (s.Contains("vox") || s.Contains("paper") || s.Contains("editorial")) return ["fade", "dissolve", "smoothleft", "wipeleft"];
        if (s.Contains("whiteboard") || s.Contains("hand-drawn") || s.Contains("hand drawn")) return ["fade", "smoothleft", "wipeleft"];
        if (s.Contains("cartoon") || s.Contains("stickman")) return ["fade", "dissolve", "smoothleft", "smoothright"];
        if (s.Contains("mystery") || s.Contains("conspiracy")) return ["fadeblack", "fade", "dissolve", "hblur"];
        return ["fade", "dissolve", "smoothleft", "fadeblack"];
    }

    private static string? MapShotDirection(string? direction)
    {
        var s = (direction ?? "").ToLowerInvariant();
        if (s.Contains("parallax")) return "parallax";
        if (s.Contains("pull") || s.Contains("zoom out")) return "pull-back";
        if (s.Contains("pan up") || s.Contains("pan down") || s.Contains("vertical")) return "pan-vertical";
        if (s.Contains("pan") || s.Contains("left") || s.Contains("right")) return "pan-horizontal";
        if (s.Contains("static") || s.Contains("hold")) return "static";
        if (s.Contains("handheld") || s.Contains("shake")) return "handheld";
        if (s.Contains("push") || s.Contains("zoom")) return "push-in";
        return null;
    }

    private static string PickWithHistory(List<string> candidates, Queue<string> recent, ref int cursor, bool avoidRepeats, int remember)
    {
        if (candidates.Count == 0) return "static";
        string? selected = null;
        for (var attempt = 0; attempt < candidates.Count; attempt++)
        {
            var candidate = candidates[cursor++ % candidates.Count];
            if (!avoidRepeats || !recent.Contains(candidate))
            {
                selected = candidate;
                break;
            }
        }
        selected ??= candidates[cursor++ % candidates.Count];
        recent.Enqueue(selected);
        while (recent.Count > remember) recent.Dequeue();
        return selected;
    }

    private static double EffectStrength(ProjectOptions options)
        => (options.EffectIntensity ?? "Subtle").ToLowerInvariant() switch
        {
            "strong" => 2.0,
            "medium" => 1.45,
            _ => 1.0
        };

    private static string F(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

    private static TimelineMedia? FindScriptAlignedTimelineMedia(
        VideoProject project, ProjectChapter currentChapter,
        ProjectScene currentScene)
    {
        // A missing scene visual must not silently become an unrelated
        // image merely because it came immediately before in the queue.
        // Prefer an existing visual illustrating the same specific subject,
        // evidence or location. If none matches, use the neutral backdrop.
        var target = SceneVisualPlanService.Keywords(currentScene);
        if(target.Count==0) return null;
        var bestScore = 0.0;
        TimelineMedia? best = null;
        var timeline = project.Chapters.SelectMany(ch =>
            ch.Scenes.Select(sc => (Chapter:ch,Scene:sc))).ToList();
        var current = timeline.FindIndex(x =>
            ReferenceEquals(x.Chapter,currentChapter) &&
            ReferenceEquals(x.Scene,currentScene));
        for (var i = 0; i < timeline.Count; i++)
        {
            var item = timeline[i];
            if (ReferenceEquals(item.Scene,currentScene) &&
                ReferenceEquals(item.Chapter,currentChapter)) continue;
            var media = ProjectMedia.FindAll(project,item.Chapter,item.Scene);
            if(media.Count==0) continue;
            var found=SceneVisualPlanService.Keywords(item.Scene);
            var shared=target.Count(w=>found.Contains(w));
            // A single generic shared word like "investigation" is
            // insufficient evidence that the visual matches narration.
            if(shared<2 && target.Count>2)continue;
            var relevance=SceneVisualPlanService.Relevance(
                currentScene,item.Scene);
            if(relevance<0.22)continue;
            var distance=current<0?20:Math.Abs(current-i);
            var score=relevance+
                (item.Chapter.Number==currentChapter.Number?0.12:0)-
                Math.Min(distance*0.012,0.30);
            if(score>bestScore)
            {
                bestScore=score;
                best=new TimelineMedia(media[0],item.Chapter.Number,
                    item.Scene.Number);
            }
        }
        return best;
    }

    private static bool IsVideo(string path)
        => new[] { ".mp4", ".mov", ".mkv", ".webm", ".avi" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static void NormalizeSceneDurations(ProjectChapter chapter, double actualAudioSeconds)
    {
        if (chapter.Scenes.Count == 0) return;
        var total = chapter.Scenes.Sum(s => Math.Max(1, s.TargetSeconds));
        var factor = actualAudioSeconds / total;
        foreach (var scene in chapter.Scenes)
            scene.TargetSeconds = Math.Max(1, scene.TargetSeconds * factor);
        chapter.TargetSeconds = (int)Math.Round(actualAudioSeconds);
    }

    // FFmpeg's silencedetect runs on the ORIGINAL chapter TTS track (before
    // background music is mixed), so quiet music cannot skew subtitle timing.
    // This is pause-aware speech timing, not phoneme/word forced alignment.
    private async Task<List<(double Start, double End)>> DetectNarrationSpeechAsync(
        ProjectChapter chapter, ZeroVideoSettings settings, double seconds,
        Action<string>? log, CancellationToken ct)
    {
        var fallback = new List<(double Start, double End)> { (0, seconds) };
        if (string.IsNullOrWhiteSpace(chapter.AudioPath) ||
            !File.Exists(chapter.AudioPath) || seconds <= 0) return fallback;
        try
        {
            // Silence events are emitted to stderr. The null output is local
            // Windows FFmpeg, so it does not affect existing uploaded media.
            var args = $"-hide_banner -nostats -i \"{chapter.AudioPath}\" " +
                "-af silencedetect=noise=-37dB:d=0.14 -f null NUL";
            var result = await runner.RunAsync(settings.FfmpegPath, args, ct: ct);
            if (result.ExitCode != 0) return fallback;
            var events = Regex.Matches(result.StdErr ?? "",
                @"silence_(start|end):\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            if (events.Count == 0) return fallback;

            var active = new List<(double Start, double End)>();
            double? talkingFrom = 0;
            foreach (Match match in events)
            {
                ct.ThrowIfCancellationRequested();
                var type = match.Groups[1].Value;
                if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var time)) continue;
                time = Math.Clamp(time, 0, seconds);
                if (type.Equals("start", StringComparison.OrdinalIgnoreCase))
                {
                    if (talkingFrom.HasValue && time > talkingFrom.Value + 0.055)
                        active.Add((talkingFrom.Value, time));
                    talkingFrom = null;
                }
                else
                {
                    talkingFrom = time;
                }
            }
            if (talkingFrom.HasValue && seconds > talkingFrom.Value + 0.055)
                active.Add((talkingFrom.Value, seconds));
            // Keep only substantial speech activity. A short quiet first/last
            // part is fine, but detection errors must not zero the captions.
            var total = active.Sum(x => x.End - x.Start);
            if (active.Count == 0 || total < seconds * 0.20) return fallback;
            log?.Invoke($"Captions chapter {chapter.Number}: analyzed voice track, " +
                $"{active.Count} speech section(s), {total:0.0}/{seconds:0.0}s audible.");
            return active;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Invoke($"Captions chapter {chapter.Number}: speech analysis unavailable " +
                $"({ex.Message}); using chapter timing.");
            return fallback;
        }
    }

    private static double SpeechPosition(
        IReadOnlyList<(double Start, double End)> intervals, double progress)
    {
        var total = intervals.Sum(x => x.End - x.Start);
        if (total <= 0) return 0;
        var remaining = Math.Clamp(progress, 0, 1) * total;
        foreach (var span in intervals)
        {
            var duration = span.End - span.Start;
            if (remaining <= duration) return span.Start + remaining;
            remaining -= duration;
        }
        return intervals[^1].End;
    }

    // Pronunciation pace differs between short and long words; a light
    // syllable-length estimate distributes narration between audio pauses.
    private static double CaptionWordWeight(string word)
    {
        var clean = Regex.Replace(word, @"[^\p{L}\p{N}]", "");
        return Math.Clamp(clean.Length / 4.2, 0.85, 3.4);
    }

    /// <summary>
    /// Encodes only the finished video frames with ASS. Existing narration,
    /// chapter visuals, audio codecs and project images remain untouched.
    /// Source and output paths are distinct; a failed FFmpeg run cannot
    /// overwrite the user's original completed video.
    /// </summary>
    private async Task BurnCaptionAssAsync(
        string sourceMp4,string captionAss,string outputMp4,
        ZeroVideoSettings settings,string renderDir,
        Action<string>? log,CancellationToken ct)
    {
        if(!File.Exists(sourceMp4))
            throw new FileNotFoundException(
                "Caption source video does not exist.",sourceMp4);
        if(!File.Exists(captionAss))
            throw new FileNotFoundException(
                "Caption ASS file was not created.",captionAss);
        var assName=Path.GetFileName(captionAss);
        // The filter uses only a relative script name to avoid Windows
        // FFmpeg parsing the drive colon as an ASS filter argument.
        // The generated ASS is always placed in renderDir.
        if(!Path.GetFullPath(Path.GetDirectoryName(captionAss)!)
            .Equals(Path.GetFullPath(renderDir),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Caption ASS file must be inside the project's render folder.");
        log?.Invoke("Burning synchronized captions with FFmpeg/libass: "+
            assName+" (video encode only; audio copied unchanged)...");
        var args=$"-y -nostdin -i \\"{sourceMp4}\\" "+
            $"-vf \\"ass={assName}\\" "+
            "-map 0:v:0 -map 0:a:0 -c:v libx264 -preset medium -crf 20 "+
            $"-pix_fmt yuv420p -c:a copy -movflags +faststart \\"{outputMp4}\\"";
        var burn=await runner.RunAsync(settings.FfmpegPath,args,renderDir,ct);
        if(burn.ExitCode!=0 ||
            !File.Exists(outputMp4) ||
            new FileInfo(outputMp4).Length<64*1024)
            throw new InvalidOperationException(
                "Caption burn-in failed (FFmpeg requires the libass filter). "+
                (burn.StdErr.Length>1200?
                    burn.StdErr[^1200..]:burn.StdErr));
    }

    /// <summary>
    /// Repair a finished widescreen Longform MP4 without remaking images,
    /// narration, transitions, thumbnails or metadata. Creates a one-time
    /// original-video backup under this project's render folder, then
    /// re-burns synchronized captions from saved chapter audio and script.
    /// A failed attempt always leaves the export MP4 intact.
    /// </summary>
    public async Task<string> RepairExistingLongFormCaptionsAsync(
        VideoProject project,string exportedMp4,
        ZeroVideoSettings settings,Action<string>? log=null,
        CancellationToken ct=default)
    {
        if(project.AspectRatio!="16:9")
            throw new InvalidOperationException(
                "This caption repair is for 16:9 Longform projects only.");
        if(project.Chapters.Count==0)
            throw new InvalidOperationException(
                "No scripted chapters found for caption repair.");
        var exports=Path.GetFullPath(Path.Combine(
            project.ProjectDirectory,"exports"))+
            Path.DirectorySeparatorChar;
        var fullMp4=Path.GetFullPath(exportedMp4);
        if(!fullMp4.StartsWith(exports,
            StringComparison.OrdinalIgnoreCase) ||
            !fullMp4.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullMp4))
            throw new FileNotFoundException(
                "Select a completed Longform MP4 within this project's exports folder.",
                fullMp4);
        await ValidateAsync(settings,log,ct);
        var renderDir=Path.Combine(project.ProjectDirectory,"render");
        Directory.CreateDirectory(renderDir);
        var safeName=Path.GetFileNameWithoutExtension(fullMp4);
        var original=Path.Combine(renderDir,
            safeName+".before-longform-captions.mp4");
        var temporary=Path.Combine(renderDir,
            "longform-caption-repair-"+Guid.NewGuid().ToString("N")+".mp4");
        var captionAss=Path.Combine(renderDir,
            "longform-captions-repair.ass");
        if(!File.Exists(original))
        {
            log?.Invoke("Preserving the original finished MP4 for repeatable "+
                "caption repair: "+original);
            var partial=original+".partial";
            try
            {
                File.Copy(fullMp4,partial,true);
                File.Move(partial,original,true);
            }
            finally
            {
                if(File.Exists(partial))File.Delete(partial);
            }
        }
        var timelines=new List<NarrationTimelineService.ChapterTiming>();
        foreach(var chapter in project.Chapters)
        {
            ct.ThrowIfCancellationRequested();
            if(string.IsNullOrWhiteSpace(chapter.AudioPath) ||
                !File.Exists(chapter.AudioPath))
                throw new FileNotFoundException(
                    "Caption repair needs the saved narration audio for "+
                    "chapter "+chapter.Number+".",chapter.AudioPath);
            var seconds=await ProbeDurationAsync(
                chapter.AudioPath,settings,ct);
            if(seconds<=0)
                throw new InvalidDataException(
                    "Cannot determine narration duration for chapter "+
                    chapter.Number+".");
            var activity=await DetectNarrationSpeechAsync(
                chapter,settings,seconds,log,ct);
            timelines.Add(NarrationTimelineService.Plan(
                chapter,seconds,activity));
        }
        if(!await WriteBoldCaptionAssAsync(
            project,timelines,captionAss,log,ct))
            throw new InvalidOperationException(
                "No script words available for Longform captions.");
        if(project.Options.GenerateCaptions)
            await WriteSrtAsync(project,
                Path.ChangeExtension(fullMp4,".srt"),timelines,ct);
        try
        {
            await BurnCaptionAssAsync(
                original,captionAss,temporary,settings,renderDir,log,ct);
            await ValidateAudibleAudioAsync(
                temporary,settings,"Repaired captioned Longform",log,ct);
            File.Copy(temporary,fullMp4,true);
            log?.Invoke("16:9 Longform captions repaired in the EXISTING "+
                "exported MP4; no AI images, narration, scenes or thumbnails "+
                "were regenerated. Original uncaptioned backup retained at: "+
                original);
            return fullMp4;
        }
        finally
        {
            try {if(File.Exists(temporary))File.Delete(temporary);}
            catch(IOException){}
        }
    }

    private async Task<bool> WriteBoldCaptionAssAsync(
        VideoProject project,
        IReadOnlyList<NarrationTimelineService.ChapterTiming> timelines,
        string path, Action<string>? log, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]")
          .AppendLine("ScriptType: v4.00+")
          .AppendLine(CaptionRenderPolicy.AssCanvas(project.AspectRatio))
          .AppendLine("ScaledBorderAndShadow: yes")
          .AppendLine("WrapStyle: 2")
          .AppendLine()
          .AppendLine("[V4+ Styles]")
          .AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding")
          .AppendLine(CaptionRenderPolicy.AssStyle(project.AspectRatio))
          .AppendLine()
          .AppendLine("[Events]")
          .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        var elapsed = 0.0;
        var captions = 0;
        for(var chapterIndex=0; chapterIndex<project.Chapters.Count; chapterIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var chapter = project.Chapters[chapterIndex];
            var timing = timelines[chapterIndex];
            var cues = NarrationTimelineService.CaptionCues(chapter,timing);
            foreach (var cue in cues)
            {
                var caption = CaptionLayoutService.Format(cue.Words,
                    CaptionRenderPolicy.IsLandscape(project.AspectRatio));
                if(string.IsNullOrWhiteSpace(caption)) continue;
                sb.Append("Dialogue: 0,")
                  .Append(AssCaptionTime(elapsed+cue.Time.Start)).Append(',')
                  .Append(AssCaptionTime(elapsed+cue.Time.End))
                  .Append(",Punchy,,0,0,0,,")
                  .AppendLine(caption);
                captions++;
            }
            log?.Invoke($"Captions chapter {chapter.Number}: {cues.Count} " +
                "script phrases locked to the same scene timing as visuals.");
            elapsed += timing.Duration;
        }

        if (captions == 0) return false;
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), ct);
        log?.Invoke($"Narration-synced {project.AspectRatio} captions written: "+
            $"{captions} non-overlapping scene-bound phrases; all white, "+
            "at most 2 lines, protected side margins and no automatic wrapping.");
        return true;
    }

    private static string AssCaptionTime(double seconds)
    {
        var ticks = Math.Max(0, (long)Math.Round(seconds * 100.0));
        var hh = ticks / 360000;
        var mm = ticks / 6000 % 60;
        var ss = ticks / 100 % 60;
        var cc = ticks % 100;
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:00}", hh, mm, ss, cc);
    }

    private static async Task WriteSrtAsync(
        VideoProject project,string path,
        IReadOnlyList<NarrationTimelineService.ChapterTiming> timelines,
        CancellationToken ct)
    {
        var sb=new StringBuilder();
        var index=1;
        var chapterOffset=0.0;
        for(var chapterIndex=0;chapterIndex<project.Chapters.Count;chapterIndex++)
        {
            var chapter=project.Chapters[chapterIndex];
            var timing=timelines[chapterIndex];
            var spoken=NarrationTimelineService.SpokenSceneWords(chapter);
            for(var i=0;i<chapter.Scenes.Count;i++)
            {
                var beat=timing.Scenes[i];
                sb.AppendLine(index++.ToString())
                  .AppendLine($"{Timestamp(chapterOffset+beat.Start)} --> " +
                    $"{Timestamp(chapterOffset+beat.End)}")
                  .AppendLine(string.Join(" ",spoken[i]).Trim())
                  .AppendLine();
            }
            chapterOffset+=timing.Duration;
        }
        await File.WriteAllTextAsync(path,sb.ToString(),
            new UTF8Encoding(false),ct);
    }

    private static string Timestamp(double sec)
    {
        var ts = TimeSpan.FromSeconds(sec);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value.Length == 0 ? "ZeroVideo" : value;
    }

    private sealed record TimelineMedia(string Path, int ChapterNumber, int SceneNumber);
    private sealed record RenderedClip(string Path, double DurationSeconds, string Transition);

    private sealed class RenderEffectsState
    {
        public Queue<string> RecentMotions { get; } = new();
        public Queue<string> RecentTransitions { get; } = new();
        public int MotionCursor;
        public int TransitionCursor;
        public int AiGeneratedClipCount;
        public int AiAttemptCount;
    }
}
