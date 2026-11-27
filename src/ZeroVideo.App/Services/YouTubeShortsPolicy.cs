using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ZeroVideo.App.Services;

/// <summary>
/// YouTube Shorts browser upload guard. A 100-character title is a platform
/// limit (including hashtags); eligible Shorts are vertical/square and no
/// longer than 3 minutes. Ffprobe is used so regular videos cannot be
/// accidentally submitted through this dedicated Shorts action.
/// </summary>
public static class YouTubeShortsPolicy
{
    public const int MaxTitleCharacters = 100;
    public const int MaxDescriptionUtf8Bytes = 5000;
    public const double MaxShortSeconds = 180.0;

    public static string? ValidateMetadata(string? title, string? description)
    {
        if (string.IsNullOrWhiteSpace(title)) return "YouTube Shorts title is required.";
        if (title.Length > MaxTitleCharacters)
            return $"YouTube Shorts title is {title.Length}/100 characters. " +
                "Shorten the title including hashtags before publishing.";
        if (title.IndexOfAny(['<','>']) >= 0)
            return "YouTube Shorts title cannot contain < or >.";
        if (description is null) description = "";
        if (Encoding.UTF8.GetByteCount(description) > MaxDescriptionUtf8Bytes)
            return "YouTube Shorts description exceeds the safe 5000-byte limit.";
        if (description.IndexOfAny(['<','>']) >= 0)
            return "YouTube description contains invalid < or > characters.";
        return null;
    }

    public static bool IsShort(double durationSeconds, int width, int height)
        => durationSeconds > 0 && durationSeconds <= MaxShortSeconds &&
           width > 0 && height > 0 && width <= height;

    public static async Task<string?> ValidateVideoAsync(
        string videoFile, string ffprobePath, CancellationToken ct)
    {
        if (!File.Exists(videoFile) ||
            !videoFile.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "A valid MP4 file is required.";
        if (!File.Exists(ffprobePath))
            return "FFprobe is not configured or missing. Set the FFPROBE.EXE path in Settings.";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName=ffprobePath,
                Arguments="-v error -select_streams v:0 -show_entries " +
                    "stream=width,height:format=duration -of json " +
                    "\"" + videoFile.Replace("\"", "\\\"") + "\"",
                UseShellExecute=false,
                CreateNoWindow=true,
                RedirectStandardOutput=true,
                RedirectStandardError=true
            }
        };
        process.Start();
        var stdout=process.StandardOutput.ReadToEndAsync(ct);
        var stderr=process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var json=await stdout;
        var errors=await stderr;
        if (process.ExitCode!=0)
            return "FFprobe could not inspect MP4: " + errors[..Math.Min(180,errors.Length)];

        try
        {
            using var doc=JsonDocument.Parse(json);
            var streams=doc.RootElement.GetProperty("streams");
            if (streams.GetArrayLength()<1)
                return "MP4 has no video stream.";
            var stream=streams[0];
            var width=stream.GetProperty("width").GetInt32();
            var height=stream.GetProperty("height").GetInt32();
            var durString=doc.RootElement.GetProperty("format")
                .GetProperty("duration").GetString() ?? "";
            if (!double.TryParse(durString,System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,out var duration))
                return "Cannot determine the Shorts duration from FFprobe.";
            if (!IsShort(duration,width,height))
                return $"YouTube Shorts requires square/vertical MP4 up to 3 minutes. " +
                    $"Detected {width}x{height}, {duration:F1} seconds.";
            return null;
        }
        catch(Exception ex) when(ex is JsonException or KeyNotFoundException or
                                InvalidOperationException or FormatException)
        {
            return "Cannot determine Shorts video dimensions or duration: " + ex.Message;
        }
    }
}
