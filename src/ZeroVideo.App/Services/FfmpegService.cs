using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class FfmpegService(ProcessRunner runner, DependencyService dependencies)
{
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
        var effects = new RenderEffectsState();
        foreach (var chapter in project.Chapters)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(chapter.AudioPath) || !File.Exists(chapter.AudioPath))
                throw new FileNotFoundException($"Missing audio for chapter {chapter.Number}.", chapter.AudioPath);

            var audioSeconds = await ProbeDurationAsync(chapter.AudioPath, settings, ct);
            if (audioSeconds <= 0) audioSeconds = Math.Max(1, chapter.TargetSeconds);
            NormalizeSceneDurations(chapter, audioSeconds);

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
            await WriteSrtAsync(project, Path.ChangeExtension(finalPath, ".srt"), ct);

        if (project.Options.BurnInBoldCaptions && project.AspectRatio == "9:16")
        {
            var captionAss = Path.Combine(renderDir, "styled-video-captions.ass");
            if (await WriteBoldCaptionAssAsync(project, captionAss, ct))
            {
                var captionedMp4 = Path.Combine(renderDir, "video-with-styled-captions.mp4");
                log?.Invoke("Rendering bold vertical captions: large white uppercase, " +
                    "black outline, cyan emphasis; narration scene timing is estimated.");
                // 'ass' supports color override spans; use a short relative
                // filename to avoid Windows drive-letter filter escaping.
                var renderArgs = $"-y -i \"{finalPath}\" -vf \"ass=styled-video-captions.ass\" " +
                    $"-map 0:v:0 -map 0:a:0 -c:v libx264 -preset medium -crf 20 " +
                    $"-pix_fmt yuv420p -c:a copy -movflags +faststart \"{captionedMp4}\"";
                var burn = await runner.RunAsync(settings.FfmpegPath, renderArgs, renderDir, ct);
                if (burn.ExitCode != 0 || !File.Exists(captionedMp4))
                    throw new InvalidOperationException("Bold caption burn-in failed. " +
                        "Check that FFmpeg has the libass subtitle filter. " +
                        (burn.StdErr.Length > 1000 ? burn.StdErr[^1000..] : burn.StdErr));
                File.Copy(captionedMp4, finalPath, true);
                File.Delete(captionedMp4);
                log?.Invoke("Bold captions embedded in final vertical MP4.");
            }
        }

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
                var args = $"-y -f lavfi -i \"color=c=0x241C30:s={w}x{h}:r=30:d={duration}\" -vf \"drawbox=x=0:y=0:w=iw:h=ih/3:color=0x34254A@0.75:t=fill,drawbox=x=0:y=ih*0.72:w=iw:h=ih*0.28:color=0x17151F@0.82:t=fill\" -an -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \"{output}\"";
                var result = await runner.RunAsync(settings.FfmpegPath, args, renderDir, ct);
                if (result.ExitCode != 0) throw new InvalidOperationException($"Scene render failed ({chapter.Number}.{scene.Number}): {result.StdErr}");
                clips.Add(new RenderedClip(output, seconds, "fade"));
                continue;
            }

            var perVisualSeconds = Math.Max(1.0, scene.TargetSeconds / mediaItems.Count);
            for (var mediaIndex = 0; mediaIndex < mediaItems.Count; mediaIndex++)
            {
                var media = mediaItems[mediaIndex];
                var transition = ChooseTransitionPreset(project, effects);
                var renderSeconds = perVisualSeconds + configuredTransition;
                var duration = F(renderSeconds);
                var suffix = mediaItems.Count > 1 ? $"-v{mediaIndex + 1:00}" : "";
                var output = Path.Combine(renderDir, $"c{chapter.Number:00}-s{scene.Number:000}{suffix}.mp4");
                string args;
                string motion;

                if (IsVideo(media))
                {
                    motion = "source video";
                    var vf = $"scale={w}:{h}:force_original_aspect_ratio=increase:flags=lanczos,crop={w}:{h},fps=30";
                    var styleFx = BuildStyleEffectFilter(project);
                    if (!string.IsNullOrWhiteSpace(styleFx)) vf += "," + styleFx;
                    args = $"-y -stream_loop -1 -i \"{media}\" -t {duration} -vf \"{vf}\" -an -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \"{output}\"";
                }
                else
                {
                    motion = ChooseMotionPreset(project, scene, effects);
                    args = BuildStillRenderArgs(media, output, renderSeconds, w, h, motion, project);
                }

                log?.Invoke($"Scene {chapter.Number}.{scene.Number}: effect {motion}; transition {transition}; {renderSeconds:0.0}s render segment.");
                var result = await runner.RunAsync(settings.FfmpegPath, args, renderDir, ct);
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

        log?.Invoke($"Chapter {chapter.Number}: advanced transition render failed; falling back to safe concatenation. {result.StdErr}");
        return await ConcatRenderedClipsAsync(chapter, settings, renderDir, clips, chapterVisual, ct);
    }

    private async Task<string> ConcatRenderedClipsAsync(ProjectChapter chapter, ZeroVideoSettings settings, string renderDir, List<RenderedClip> clips, string chapterVisual, CancellationToken ct)
    {
        var list = Path.Combine(renderDir, $"chapter-{chapter.Number:00}-visuals.txt");
        await File.WriteAllLinesAsync(list, clips.Select(p => $"file '{p.Path.Replace("'", "'\\''")}'"), ct);
        var concat = await runner.RunAsync(settings.FfmpegPath, $"-y -f concat -safe 0 -i \"{list}\" -c copy \"{chapterVisual}\"", renderDir, ct);
        if (concat.ExitCode != 0) throw new InvalidOperationException($"Chapter visual concat failed: {concat.StdErr}");
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

    private static TimelineMedia? FindNearestTimelineMedia(VideoProject project, ProjectChapter currentChapter, ProjectScene currentScene)
    {
        var timeline = project.Chapters
            .SelectMany(chapter => chapter.Scenes.Select(scene => (Chapter: chapter, Scene: scene)))
            .ToList();

        var currentIndex = timeline.FindIndex(x =>
            ReferenceEquals(x.Chapter, currentChapter) && ReferenceEquals(x.Scene, currentScene));

        if (currentIndex < 0)
            currentIndex = timeline.FindIndex(x => x.Chapter.Number == currentChapter.Number && x.Scene.Number == currentScene.Number);
        if (currentIndex < 0) return null;

        // Prefer the previous scene so a missing visual naturally holds the last real image.
        for (var i = currentIndex - 1; i >= 0; i--)
        {
            var media = ProjectMedia.FindAll(project, timeline[i].Chapter, timeline[i].Scene);
            if (media.Count > 0)
                return new TimelineMedia(media[^1], timeline[i].Chapter.Number, timeline[i].Scene.Number);
        }

        // At the beginning of a project/chapter, borrow the next available visual.
        for (var i = currentIndex + 1; i < timeline.Count; i++)
        {
            var media = ProjectMedia.FindAll(project, timeline[i].Chapter, timeline[i].Scene);
            if (media.Count > 0)
                return new TimelineMedia(media[0], timeline[i].Chapter.Number, timeline[i].Scene.Number);
        }

        return null;
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

    // Creates ASS events from the project narration for punchy, legible
    // vertical in-video captions. Scene-level audio timings are available;
    // exact spoken-word timestamps are not, so distribute groups in proportion
    // to their word counts. Original .SRT export remains independent.
    private static async Task<bool> WriteBoldCaptionAssAsync(
        VideoProject project, string path, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]")
          .AppendLine("ScriptType: v4.00+")
          .AppendLine("PlayResX: 1080")
          .AppendLine("PlayResY: 1920")
          .AppendLine("ScaledBorderAndShadow: yes")
          .AppendLine("WrapStyle: 2")
          .AppendLine()
          .AppendLine("[V4+ Styles]")
          .AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding")
          .AppendLine("Style: Punchy,Arial Black,104,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,9,0,2,64,64,350,1")
          .AppendLine()
          .AppendLine("[Events]")
          .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        var elapsed = 0.0;
        var count = 0;
        foreach (var chapter in project.Chapters)
        {
            foreach (var scene in chapter.Scenes)
            {
                ct.ThrowIfCancellationRequested();
                var seconds = Math.Max(0.1, scene.TargetSeconds);
                // Preserve apostrophes and punctuation while avoiding
                // multiline/ASS control sequence injection in subtitles.
                var words = Regex.Matches(scene.Narration ?? "",
                    @"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*[.!?,;:]?")
                    .Select(m => m.Value).ToList();
                var groups = new List<List<string>>();
                var current = new List<string>();
                foreach (var word in words)
                {
                    var proposedLength = current.Sum(w => w.Length) + current.Count + word.Length;
                    if (current.Count > 0 && (current.Count >= 4 || proposedLength > 20))
                    {
                        groups.Add(current);
                        current = new List<string>();
                    }
                    current.Add(word);
                }
                if (current.Count > 0) groups.Add(current);
                if (groups.Count > 0)
                {
                    var wordTotal = groups.Sum(g => g.Count);
                    var passed = 0;
                    foreach (var group in groups)
                    {
                        var from = elapsed + seconds * passed / wordTotal;
                        passed += group.Count;
                        var to = elapsed + seconds * passed / wordTotal;
                        if (to <= from) continue;
                        var first = EscapeAssCaptionWord(group[0]);
                        var rest = string.Join(" ", group.Skip(1).Select(EscapeAssCaptionWord));
                        // ASS uses BGR ordering. Cyan #00D9FF => &HFFD900&.
                        var styled = "{\\1c&HFFD900&}" + first +
                            "{\\1c&HFFFFFF&}" +
                            (rest.Length > 0 ? " " + rest : "");
                        sb.Append("Dialogue: 0,")
                          .Append(AssCaptionTime(from)).Append(',')
                          .Append(AssCaptionTime(to))
                          .Append(",Punchy,,0,0,0,,")
                          .AppendLine(styled);
                        count++;
                    }
                }
                elapsed += seconds;
            }
        }
        if (count == 0) return false;
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), ct);
        return true;
    }

    private static string EscapeAssCaptionWord(string value)
    {
        // Remove control tokens. Generated words use Unicode letters and
        // numbers plus selected punctuation; these escapes are defensive.
        var upper = value.ToUpperInvariant();
        return upper.Replace("\\", "").Replace("{", "").Replace("}", "");
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

    private static async Task WriteSrtAsync(VideoProject project, string path, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var index = 1;
        double cursor = 0;

        foreach (var chapter in project.Chapters)
        foreach (var scene in chapter.Scenes)
        {
            var end = cursor + scene.TargetSeconds;
            sb.AppendLine(index++.ToString())
              .AppendLine($"{Timestamp(cursor)} --> {Timestamp(end)}")
              .AppendLine(scene.Narration.Trim())
              .AppendLine();
            cursor = end;
        }

        await File.WriteAllTextAsync(path, sb.ToString(), ct);
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
    }
}
