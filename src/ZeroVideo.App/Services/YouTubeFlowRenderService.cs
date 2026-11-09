using System.Globalization;
using System.Text;
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

    public async Task<string> RenderAsync(
        IReadOnlyList<PixabayFlowClip> clips, string voiceover, string output,
        string ratio, ZeroVideoSettings settings,
        Action<string>? log, CancellationToken ct)
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
        var rendered = new List<string>();
        for (var i = 0; i < clips.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.Combine(work, "shot-" + (i + 1).ToString("00") + ".mp4");
            if (!File.Exists(file) || new FileInfo(file).Length < 4096)
            {
                var filter = $"scale={width}:{height}:force_original_aspect_ratio=increase," +
                    $"crop={width}:{height},setsar=1,fps=30,format=yuv420p";
                var args = "-hide_banner -loglevel error -y -stream_loop -1 " +
                    "-i " + Quote(clips[i].FilePath) + " -t 6.0 -an " +
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
        var segments = (int)Math.Ceiling((duration + shotLength) / shotLength);
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
