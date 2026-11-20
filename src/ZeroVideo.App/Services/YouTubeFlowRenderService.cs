using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Separate stock-video renderer. Never invokes the legacy caption/subtitle pipeline.
/// Builds uniform six-second silent H.264 shots and muxes only the verified narration.
/// </summary>
public sealed class YouTubeFlowRenderService(ProcessRunner runner, FfmpegService ffmpeg)
{
    public async Task<string> CombineNarrationAsync(
        IReadOnlyList<string> parts, string output,
        ZeroVideoSettings settings, Action<string>? log, CancellationToken ct)
    {
        if (parts.Count == 0) throw new InvalidDataException("No voiceover sections were generated.");
        await ffmpeg.ValidateAsync(settings, log, ct);
        if (parts.Count == 1)
        {
            if (!File.Exists(parts[0]))
                throw new FileNotFoundException("Missing narration part.", parts[0]);
            var args = "-hide_banner -loglevel error -y -i " + Quote(parts[0]) +
                " -vn -map 0:a:0 -ac 2 -ar 48000 -c:a pcm_s16le " + Quote(output);
            var one = await runner.RunAsync(settings.FfmpegPath, args, ct: ct);
            if (one.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException("Could not normalize narration: " + Tail(one.StdErr));
            return output;
        }
        // Normalize the audio format to WAV even for a single Clipchamp section.
        var arguments = new StringBuilder("-hide_banner -loglevel error -y ");
        for (var i = 0; i < parts.Count; i++)
        {
            if (!File.Exists(parts[i]))
                throw new FileNotFoundException("Missing narration part.", parts[i]);
            arguments.Append("-i ").Append(Quote(parts[i])).Append(' ');
        }
        var filters = new List<string>();
        for (var i = 0; i < parts.Count; i++)
            filters.Add($"[{i}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,asetpts=PTS-STARTPTS[a{i}]");
        filters.Add(string.Join("", Enumerable.Range(0, parts.Count).Select(i => $"[a{i}]")) +
            $"concat=n={parts.Count}:v=0:a=1[out]");
        arguments.Append("-filter_complex ").Append(Quote(string.Join(";", filters)))
            .Append(" -map [out] -c:a pcm_s16le -ar 48000 ")
            .Append(Quote(output));
        log?.Invoke($"FFmpeg: joining {parts.Count} voiceover section(s).");
        var result = await runner.RunAsync(settings.FfmpegPath, arguments.ToString(), ct: ct);
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException("Could not join narration: " + Tail(result.StdErr));
        return output;
    }

    public static double[] PlanSceneDurations(
        IReadOnlyList<PixabayFootageScene> scenes, double narrationSeconds)
    {
        if (scenes.Count == 0 || narrationSeconds <= 0) return [];
        // Without a word-aligned speech transcript, spoken word count is a
        // better approximation than assigning identical durations to a short
        // sentence and a much longer paragraph.
        var weights = scenes.Select(scene =>
            Math.Max(1, Regex.Matches(scene.NarrationExcerpt,
                @"\b[\p{L}\p{N}]+\b").Count)).ToArray();
        var sum = weights.Sum();
        return weights.Select(w => narrationSeconds * w / sum).ToArray();
    }

    public async Task<string> RenderAsync(
        IReadOnlyList<PixabayFlowClip> clips, string voiceover, string output,
        string ratio, ZeroVideoSettings settings,
        Action<string>? log, CancellationToken ct,
        IReadOnlyList<PixabayFootageScene>? scenes = null)
    {
        if (clips.Count == 0) throw new InvalidOperationException("No Pixabay video clips available.");
        if (!File.Exists(voiceover)) throw new FileNotFoundException("Narration file missing.", voiceover);
        await ffmpeg.ValidateAsync(settings, log, ct);
        var duration = await ffmpeg.ProbeDurationAsync(voiceover, settings, ct);
        if (duration < 3 || duration > 3600)
            throw new InvalidOperationException("The voiceover must be between 3 seconds and 60 minutes.");
        var width = ratio == "9:16" ? 1080 : 1920;
        var height = ratio == "9:16" ? 1920 : 1080;
        var work = Path.Combine(Path.GetDirectoryName(output)!, "rendered");
        Directory.CreateDirectory(work);
        const double shotLength = 6d;
        var shotDurations = new List<double>(clips.Count);
        if (scenes is not null && scenes.Count > 0)
        {
            // Distribute narration time across its chronological script segments.
            // A group's video shots fill only that segment, preventing the next
            // subject from starting 5-10 seconds before/after its spoken lines.
            var sceneDurations = PlanSceneDurations(scenes, duration);
            var orderedSceneClips = scenes.SelectMany(scene =>
                clips.Where(c => c.SearchQuery.Equals(scene.SearchQuery,
                    StringComparison.OrdinalIgnoreCase))).ToList();
            if (orderedSceneClips.Count != clips.Count ||
                !orderedSceneClips.Select(x => x.Id).SequenceEqual(clips.Select(x => x.Id)))
                throw new InvalidDataException("Pixabay footage is not in the order of the narration storyboard.");
            for (var sceneIndex = 0; sceneIndex < scenes.Count; sceneIndex++)
            {
                var scene = scenes[sceneIndex];
                var count = clips.Count(c => c.SearchQuery.Equals(scene.SearchQuery,
                    StringComparison.OrdinalIgnoreCase));
                if (count == 0) throw new InvalidDataException(
                    $"No relevant footage for narration scene {scene.SceneNumber}.");
                for (var k = 0; k < count; k++)
                    shotDurations.Add(sceneDurations[sceneIndex] / count);
            }
        }
        else
        {
            shotDurations.AddRange(Enumerable.Repeat(shotLength, clips.Count));
        }
        var rendered = new List<string>();
        for (var i = 0; i < clips.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // A resumed project can insert newly recovered clips earlier in the plan.
            // Key the normalized shot by its real Pixabay ID and aspect dimensions,
            // not just the position (shot-01), or we would reuse unrelated footage.
            var seconds = shotDurations[i];
            var frames = Math.Max(1, (int)Math.Round(seconds * 30d));
            var file = Path.Combine(work, $"shot-{i + 1:00}-{clips[i].Id}-{width}x{height}-{frames}f-{(clips[i].IsStillImage ? "photo-v2" : "video")}.mp4");
            if (!File.Exists(file) || new FileInfo(file).Length < 4096)
            {
                var isPhoto = clips[i].IsStillImage;
                var baseFilter = $"scale={width}:{height}:force_original_aspect_ratio=increase," +
                    $"crop={width}:{height},setsar=1";
                string filter;
                if (isPhoto)
                {
                    // Ken Burns: alternate gentle zoom-in, zoom-out and lateral drift.
                    // Fade on photo boundaries; real stock video stays unmodified.
                    var variant = i % 3;
                    var zoom = variant == 1
                        ? "1.11-on*0.00015" : "1.035+on*0.00017";
                    var x = variant == 2
                        ? $"(iw-iw/zoom)*on/{Math.Max(1, frames)}"
                        : "(iw-iw/zoom)/2";
                    var y = "(ih-ih/zoom)/2";
                    var mood = scenes?.FirstOrDefault(x =>
                        x.SearchQuery.Equals(clips[i].SearchQuery,
                            StringComparison.OrdinalIgnoreCase))?.Mood ?? "neutral";
                    var color = mood is "ominous" or "tense" or "somber"
                        ? ",eq=brightness=-0.055:saturation=0.72:contrast=1.12"
                        : mood == "upbeat" ? ",eq=saturation=1.06" : "";
                    var fadeOutAt = Math.Max(0, seconds - 0.23)
                        .ToString("0.000", CultureInfo.InvariantCulture);
                    filter = baseFilter +
                        $",zoompan=z={zoom}:x={x}:y={y}:d=1:s={width}x{height}:fps=30" +
                        color + ",fade=t=in:st=0:d=0.23" +
                        $",fade=t=out:st={fadeOutAt}:d=0.23,format=yuv420p";
                }
                else filter = baseFilter + ",fps=30,format=yuv420p";
                var inputMode = isPhoto
                    ? "-loop 1 -framerate 30 "
                    : "-stream_loop -1 ";
                var args = "-hide_banner -loglevel error -y " + inputMode +
                    "-i " + Quote(clips[i].FilePath) + " -t " +
                    seconds.ToString("0.000", CultureInfo.InvariantCulture) + " -an " +
                    "-vf " + Quote(filter) +
                    " -c:v libx264 -preset veryfast -crf 22 " +
                    "-pix_fmt yuv420p -r 30 " + Quote(file);
                log?.Invoke($"FFmpeg: rendering footage shot {i + 1}/{clips.Count}.");
                var result = await runner.RunAsync(settings.FfmpegPath, args, ct: ct);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"FFmpeg could not normalize stock clip {clips[i].Id}: " + Tail(result.StdErr));
            }
            rendered.Add(file);
        }

        // Short shots provide movement while the exact narration duration is
        // the master clock. Reusing downloaded clips avoids bulk downloads.
        // Exactly one six-second segment per shot. No extra trailing repeat.
        var segments = scenes is null ? (int)Math.Ceiling(duration / shotLength) : rendered.Count;
        var concatList = Path.Combine(work, "shots.txt");
        var sb = new StringBuilder("ffconcat version 1.0\n");
        for (var i = 0; i < segments; i++)
        {
            var path = Path.GetFullPath(rendered[i % rendered.Count])
                .Replace('\\', '/').Replace("'", "'\\''");
            sb.Append("file '").Append(path).Append("'\n");
        }
        await File.WriteAllTextAsync(concatList, sb.ToString(), ct);

        var temp = Path.Combine(work, "youtube-flow-output.part.mp4");
        var end = duration.ToString("0.000", CultureInfo.InvariantCulture);
        var muxArgs = "-hide_banner -loglevel error -y -safe 0 -f concat " +
            "-i " + Quote(concatList) + " -i " + Quote(voiceover) +
            " -map 0:v:0 -map 1:a:0 -t " + end +
            " -c:v copy -c:a aac -b:a 192k -movflags +faststart " + Quote(temp);
        log?.Invoke("FFmpeg: stitching clips and muxing narration (captions disabled).");
        var mux = await runner.RunAsync(settings.FfmpegPath, muxArgs, ct: ct);
        if (mux.ExitCode != 0)
            throw new InvalidOperationException("FFmpeg final merge failed: " + Tail(mux.StdErr));
        if (!File.Exists(temp) || new FileInfo(temp).Length < 8192)
            throw new InvalidDataException("FFmpeg returned an empty video. Original footage remains saved.");
        File.Move(temp, output, true);
        log?.Invoke("YouTube Flow MP4 ready: " + output);
        return output;
    }

    private static string Quote(string text) => "\"" + text.Replace("\"", "\\\"") + "\"";
    private static string Tail(string value)
        => value.Length > 1000 ? value[^1000..] : value;
}
