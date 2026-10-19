using System.IO.Compression;

namespace ZeroVideo.App.Services;

public sealed class DependencyService
{
    private const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public async Task<string> InstallFfmpegAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        var root = AppPaths.BundledFfmpegDirectory;
        var bin = Path.Combine(root, "bin", "ffmpeg.exe");
        if (File.Exists(bin)) return bin;
        Directory.CreateDirectory(root);
        var zip = Path.Combine(Path.GetTempPath(), "zerovideo-ffmpeg.zip");
        log?.Invoke("Downloading FFmpeg essentials...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        await using (var src = await http.GetStreamAsync(FfmpegUrl, ct))
        await using (var dst = File.Create(zip)) await src.CopyToAsync(dst, ct);
        var extract = Path.Combine(Path.GetTempPath(), "zerovideo-ffmpeg-extract");
        if (Directory.Exists(extract)) Directory.Delete(extract, true);
        ZipFile.ExtractToDirectory(zip, extract);
        var ffmpeg = Directory.EnumerateFiles(extract, "ffmpeg.exe", SearchOption.AllDirectories).First();
        var ffprobe = Directory.EnumerateFiles(extract, "ffprobe.exe", SearchOption.AllDirectories).First();
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.Copy(ffmpeg, bin, true);
        File.Copy(ffprobe, Path.Combine(root, "bin", "ffprobe.exe"), true);
        log?.Invoke("FFmpeg installed for ZeroVideo.");
        return bin;
    }
}
