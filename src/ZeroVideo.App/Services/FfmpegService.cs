using System.Globalization;
using System.Security.Cryptography;
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
            var muxArgs = $"-y -i \"{visual}\" -i \"{audioForMux}\" -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 192k -shortest \"{output}\"";
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
        if (project.Options.AddBackgroundMusic && !string.IsNullOrWhiteSpace(project.Options.BackgroundMusicPath) && File.Exists(project.Options.BackgroundMusicPath))
        {
            var volume = project.Options.BackgroundMusicVolume.ToString("0.###", CultureInfo.InvariantCulture);
            string filter;
            if (project.Options.AutoMusicDucking)
                filter = $"[1:a]volume={volume}[bg];[bg][0:a]sidechaincompress=threshold=0.025:ratio=10:attack=40:release=500[ducked];[0:a][ducked]amix=inputs=2:duration=first:dropout_transition=2[a]";
            else
                filter = $"[1:a]volume={volume}[bg];[0:a][bg]amix=inputs=2:duration=first:dropout_transition=2[a]";
            var args = $"-y -i \"{voiceFinal}\" -stream_loop -1 -i \"{project.Options.BackgroundMusicPath}\" -filter_complex \"{filter}\" -map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k -shortest \"{finalPath}\"";
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
            var burnArgs = $"-y -i \"{finalPath}\" -vf \"subtitles='phase2-captions.srt':force_style='{forceStyle}'\" -map 0:v:0 -map 0:a:0 -c:v libx264 -preset {profile.Preset} -crf {profile.Crf} -c:a copy \"{burned}\"";
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

    private async Task<string> RenderChapterVisualAsync(VideoProject project, ProjectChapter chapter, ZeroVideoSettings settings, string renderDir, RenderEffectsState effects, Action<string>? log, CancellationToken ct)
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
                var args = $"-y -f lavfi -i \"color=c=0x241C30:s={w}x{h}:r=30:d={duration}\" -vf \"drawbox=x=0:y=0:w=iw:h=ih/3:color=0x34254A@0.75:t=fill,drawbox=x=0:y=ih*0.72:w=iw:h=ih*0.28:color=0x17151F@0.82:t=fill\" -an -c:v libx264 -preset {profile.Preset} -crf {profile.Crf} -pix_fmt yuv420p \"{output}\"";
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
                        args = $"-y -stream_loop -1 -i \"{media}\" -t {duration} -vf \"{vf}\" -an -c:v libx264 -preset {profile.Preset} -crf {profile.Crf} -pix_fmt yuv420p \"{output}\"";
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

    private static string BuildStillRenderArgs(string media, string output, double seconds, int w, int h, string motion, VideoProject project, ProjectScene scene)
    {
        var duration = F(seconds);
        var frames = Math.Max(30, (int)Math.Ceiling(seconds * 30));
        if (motion == "parallax" && project.Options.SmartParallax)
            return BuildParallaxArgs(media, output, duration, frames, w, h, project, scene);

        var vf = $"scale={w}:{h}:force_original_aspect_ratio=increase:flags=lanczos,crop={w}:{h}";
        if (project.Options.AddMotion && !motion.Equals("static", StringComparison.OrdinalIgnoreCase))
            vf += "," + BuildZoomPanFilter(motion, frames, w, h, project.Options);
        else
            vf += ",fps=30";

        var styleFx = BuildStyleEffectFilter(project, scene);
        if (!string.IsNullOrWhiteSpace(styleFx)) vf += "," + styleFx;
        var profile = GetRenderProfile(project);
        return $"-y -loop 1 -i \"{media}\" -t {duration} -vf \"{vf}\" -an -c:v libx264 -preset {profile.Preset} -crf {profile.Crf} -pix_fmt yuv420p \"{output}\"";
    }

    private static string BuildParallaxArgs(string media, string output, string duration, int frames, int w, int h, VideoProject project, ProjectScene scene)
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
        var styleFx = BuildStyleEffectFilter(project, scene);
        var tailFx = string.IsNullOrWhiteSpace(styleFx) ? "" : "," + styleFx;

        var filter = $"[0:v]scale={w}:{h}:force_original_aspect_ratio=increase:flags=lanczos,crop={w}:{h},split=2[bg0][fg0];" +
                     $"[bg0]zoompan=z='{bgZoom}+0.009*sin(on*{s})':x='(iw-iw/zoom)*(0.5+{bgAmp}*sin(on*{s}))':y='(ih-ih/zoom)*(0.5+{bgAmp}*cos(on*{s}))':d={frames}:s={w}x{h}:fps=30,gblur=sigma={blur}[bg];" +
                     $"[fg0]zoompan=z='{fgZoom}+0.022*sin(on*{s})':x='(iw-iw/zoom)*(0.5+{fgAmp}*sin(on*{s}))':y='(ih-ih/zoom)*(0.5+{fgAmp}*cos(on*{s}))':d={frames}:s={w}x{h}:fps=30,format=rgba[fg];" +
                     $"color=c=black:s={w}x{h}:r=30:d={duration},format=gray,drawbox=x=iw*0.09:y=ih*0.04:w=iw*0.82:h=ih*0.92:color=white:t=fill,gblur=sigma={feather}[mask];" +
                     $"[fg][mask]alphamerge[fgm];[bg][fgm]overlay=0:0:format=auto{tailFx},fps=30[outv]";

        var profile = GetRenderProfile(project);
        return $"-y -loop 1 -i \"{media}\" -t {duration} -filter_complex \"{filter}\" -map \"[outv]\" -an -c:v libx264 -preset {profile.Preset} -crf {profile.Crf} -pix_fmt yuv420p \"{output}\"";
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

    private static string BuildStyleEffectFilter(VideoProject project, ProjectScene? scene = null)
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

    private static string ChooseMotionPreset(VideoProject project, ProjectScene scene, RenderEffectsState state, bool allowDirector = true)
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

    private static string ChooseTransitionPreset(VideoProject project, ProjectScene scene, RenderEffectsState state, bool allowDirector = true)
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

    private static List<string> AutomaticTransitions(string? style, string? role = null, bool roleAware = true)
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

    private static double EffectStrength(ProjectOptions options, ProjectScene? scene = null)
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

    private static string F(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

    private async Task<string> MasterNarrationAsync(string source, int chapterNumber, ZeroVideoSettings settings, string renderDir, Action<string>? log, CancellationToken ct)
    {
        var output = Path.Combine(renderDir, $"chapter-{chapterNumber:00}-mastered.m4a");
        var filter = "highpass=f=70,acompressor=threshold=0.125:ratio=2.5:attack=20:release=250,loudnorm=I=-16:TP=-1.5:LRA=11,alimiter=limit=0.95";
        var result = await runner.RunAsync(settings.FfmpegPath, $"-y -i \"{source}\" -vn -af \"{filter}\" -c:a aac -b:a 192k \"{output}\"", renderDir, ct);
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

        var videoInfo = await runner.RunAsync(settings.FfprobePath, $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=s=x:p=0 \"{path}\"", ct: ct);
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

    private static void NormalizeSceneDurations(ProjectChapter chapter, double actualAudioSeconds, ProjectOptions options)
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
            var words = Math.Max(1, Regex.Matches(scene.Narration ?? "", @"\b[\p{L}\p{N}][\p{L}\p{N}'’-]*\b").Count);
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

    private readonly record struct RenderProfile(int Width, int Height, string Preset, int Crf);

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
