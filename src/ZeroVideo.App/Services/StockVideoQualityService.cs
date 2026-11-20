using System.Diagnostics;

namespace ZeroVideo.App.Services;

/// <summary>
/// Filters obviously unusable green/blue-screen templates before a scene is rendered.
/// Uses tiny frame samples; this is a heuristic, not a visual-semantic AI model.
/// </summary>
public static class StockVideoQualityService
{
    private const int Width = 144;
    private const int Height = 81;
    private const int FrameBytes = Width * Height * 3;

    public static bool IsChromaKeyFrame(ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length < FrameBytes) return false;
        int green = 0, blue = 0;
        for (var i = 0; i < FrameBytes; i += 3)
        {
            var red = rgb[i]; var g = rgb[i + 1]; var b = rgb[i + 2];
            // Highly saturated / near-solid screen color. Ordinary green
            // foliage is typically more variable, and often less luminous.
            if (g >= 135 && g >= red * 1.85 && g >= b * 1.65 &&
                g - red >= 85 && g - b >= 85)
                green++;
            if (b >= 155 && b >= red * 2.1 && b >= g * 1.6 &&
                b - red >= 100 && b - g >= 85)
                blue++;
        }
        // A large, flat key-color area is different from normal vegetation.
        return green >= Width * Height * 0.35 || blue >= Width * Height * 0.45;
    }

    public static async Task<bool> ContainsChromaScreenAsync(
        string file, string ffmpegPath, CancellationToken ct)
    {
        if (!File.Exists(file) || !File.Exists(ffmpegPath))
            return false; // Metadata filtering remains active if FFmpeg is unavailable.
        var sampled = 0;
        foreach (var offset in new[] { "0.25", "1.50", "3.00" })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new ProcessStartInfo(ffmpegPath)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var arg in new[] {
                    "-hide_banner", "-v", "error", "-ss", offset, "-i", file,
                    "-frames:v", "1", "-vf", $"scale={Width}:{Height}",
                    "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"
                }) info.ArgumentList.Add(arg);
                using var process = Process.Start(info);
                if (process is null) continue;
                using var cancel = ct.Register(() => {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                });
                var pixels = new byte[FrameBytes];
                var read = 0;
                while (read < pixels.Length)
                {
                    var amount = await process.StandardOutput.BaseStream.ReadAsync(
                        pixels.AsMemory(read), ct);
                    if (amount == 0) break;
                    read += amount;
                }
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0 || read != FrameBytes) continue;
                sampled++;
                if (IsChromaKeyFrame(pixels)) return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* A bad video should be handled by FFmpeg at render time. */ }
        }
        return false;
    }
}
