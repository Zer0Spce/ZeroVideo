using System.Globalization;
using System.Text;
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

    public async Task<string> RenderAsync(VideoProject project, ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)
    {
        await ValidateAsync(settings, log, ct);
        var renderDir = Path.Combine(project.ProjectDirectory, "render");
        var exportsDir = Path.Combine(project.ProjectDirectory, "exports");
        Directory.CreateDirectory(renderDir);
        Directory.CreateDirectory(exportsDir);

        var chapterOutputs = new List<string>();
        foreach (var chapter in project.Chapters)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(chapter.AudioPath) || !File.Exists(chapter.AudioPath))
                throw new FileNotFoundException($"Missing audio for chapter {chapter.Number}.", chapter.AudioPath);

            var audioSeconds = await ProbeDurationAsync(chapter.AudioPath, settings, ct);
            if (audioSeconds <= 0) audioSeconds = Math.Max(1, chapter.TargetSeconds);
            NormalizeSceneDurations(chapter, audioSeconds);

            var visual = await RenderChapterVisualAsync(project, chapter, settings, renderDir, log, ct);
            var output = Path.Combine(renderDir, $"chapter-{chapter.Number:00}-final.mp4");
            var muxArgs = $"-y -i \"{visual}\" -i \"{chapter.AudioPath}\" -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 192k -shortest \"{output}\"";
            var mux = await runner.RunAsync(settings.FfmpegPath, muxArgs, renderDir, ct);
            if (mux.ExitCode != 0) throw new InvalidOperationException($"FFmpeg chapter mux failed: {mux.StdErr}");
            chapterOutputs.Add(output);
        }

        var concatList = Path.Combine(renderDir, "chapters.txt");
        await File.WriteAllLinesAsync(concatList, chapterOutputs.Select(p => $"file '{p.Replace("'", "'\\''")}'"), ct);
        var voiceFinal = Path.Combine(renderDir, "video-with-voice.mp4");
        var concat = await runner.RunAsync(settings.FfmpegPath, $"-y -f concat -safe 0 -i \"{concatList}\" -c copy \"{voiceFinal}\"", renderDir, ct);
        if (concat.ExitCode != 0) throw new InvalidOperationException($"FFmpeg concat failed: {concat.StdErr}");

        var finalPath = Path.Combine(exportsDir, Sanitize(project.Title) + ".mp4");
        if (project.Options.AddBackgroundMusic && !string.IsNullOrWhiteSpace(project.Options.BackgroundMusicPath) && File.Exists(project.Options.BackgroundMusicPath))
        {
            var volume = project.Options.BackgroundMusicVolume.ToString("0.###", CultureInfo.InvariantCulture);
            var args = $"-y -i \"{voiceFinal}\" -stream_loop -1 -i \"{project.Options.BackgroundMusicPath}\" -filter_complex \"[1:a]volume={volume}[bg];[0:a][bg]amix=inputs=2:duration=first:dropout_transition=2[a]\" -map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k -shortest \"{finalPath}\"";
            var mixed = await runner.RunAsync(settings.FfmpegPath, args, renderDir, ct);
            if (mixed.ExitCode != 0) throw new InvalidOperationException($"Music mix failed: {mixed.StdErr}");
        }
        else File.Copy(voiceFinal, finalPath, true);

        if (project.Options.GenerateCaptions)
            await WriteSrtAsync(project, Path.ChangeExtension(finalPath, ".srt"), ct);

        return finalPath;
    }

    private async Task<string> RenderChapterVisualAsync(VideoProject project, ProjectChapter chapter, ZeroVideoSettings settings, string renderDir, Action<string>? log, CancellationToken ct)
    {
        var (w, h) = project.AspectRatio == "9:16" ? (1080, 1920) : (1920, 1080);
        var clips = new List<string>();

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
                // This can only happen when the entire project has no visual media at all.
                // Use a purple/graphite project backdrop rather than a black/empty frame.
                var duration = Math.Max(1.0, scene.TargetSeconds).ToString("0.###", CultureInfo.InvariantCulture);
                var output = Path.Combine(renderDir, $"c{chapter.Number:00}-s{scene.Number:000}.mp4");
                log?.Invoke($"Scene {chapter.Number}.{scene.Number}: no media exists anywhere in the project; using the branded project backdrop.");
                var args = $"-y -f lavfi -i \"color=c=0x241C30:s={w}x{h}:r=30:d={duration}\" -vf \"drawbox=x=0:y=0:w=iw:h=ih/3:color=0x34254A@0.75:t=fill,drawbox=x=0:y=ih*0.72:w=iw:h=ih*0.28:color=0x17151F@0.82:t=fill\" -an -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \"{output}\"";
                var result = await runner.RunAsync(settings.FfmpegPath, args, renderDir, ct);
                if (result.ExitCode != 0) throw new InvalidOperationException($"Scene render failed ({chapter.Number}.{scene.Number}): {result.StdErr}");
                clips.Add(output);
                continue;
            }

            var perVisualSeconds = Math.Max(1.0, scene.TargetSeconds / mediaItems.Count);
            for (var mediaIndex = 0; mediaIndex < mediaItems.Count; mediaIndex++)
            {
                var media = mediaItems[mediaIndex];
                var duration = perVisualSeconds.ToString("0.###", CultureInfo.InvariantCulture);
                var suffix = mediaItems.Count > 1 ? $"-v{mediaIndex + 1:00}" : "";
                var output = Path.Combine(renderDir, $"c{chapter.Number:00}-s{scene.Number:000}{suffix}.mp4");
                string args;

                if (IsVideo(media))
                {
                    args = $"-y -stream_loop -1 -i \"{media}\" -t {duration} -vf \"scale={w}:{h}:force_original_aspect_ratio=increase:flags=lanczos,crop={w}:{h},fps=30\" -an -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \"{output}\"";
                }
                else
                {
                    var frames = Math.Max(30, (int)(perVisualSeconds * 30));
                    var zoom = project.Options.AddMotion
                        ? $",zoompan=z='min(zoom+0.0008,1.08)':d={frames}:s={w}x{h}:fps=30"
                        : "";
                    args = $"-y -loop 1 -i \"{media}\" -t {duration} -vf \"scale={w}:{h}:force_original_aspect_ratio=increase:flags=lanczos,crop={w}:{h}{zoom}\" -an -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \"{output}\"";
                }

                var result = await runner.RunAsync(settings.FfmpegPath, args, renderDir, ct);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Scene render failed ({chapter.Number}.{scene.Number}, visual {mediaIndex + 1}): {result.StdErr}");

                clips.Add(output);
            }
        }

        var list = Path.Combine(renderDir, $"chapter-{chapter.Number:00}-visuals.txt");
        await File.WriteAllLinesAsync(list, clips.Select(p => $"file '{p.Replace("'", "'\\''")}'"), ct);
        var chapterVisual = Path.Combine(renderDir, $"chapter-{chapter.Number:00}-visuals.mp4");
        var concat = await runner.RunAsync(settings.FfmpegPath, $"-y -f concat -safe 0 -i \"{list}\" -c copy \"{chapterVisual}\"", renderDir, ct);
        if (concat.ExitCode != 0) throw new InvalidOperationException($"Chapter visual concat failed: {concat.StdErr}");
        return chapterVisual;
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

    private static void NormalizeSceneDurations(ProjectChapter chapter, double actualAudioSeconds)
    {
        if (chapter.Scenes.Count == 0) return;
        var total = chapter.Scenes.Sum(s => Math.Max(1, s.TargetSeconds));
        var factor = actualAudioSeconds / total;
        foreach (var scene in chapter.Scenes)
            scene.TargetSeconds = Math.Max(1, scene.TargetSeconds * factor);
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

    private sealed record TimelineMedia(string Path, int ChapterNumber, int SceneNumber);
}
