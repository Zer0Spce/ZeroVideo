using System.IO.Compression;

namespace ZeroVideo.App.Services;

public sealed class DependencyService
{
    private const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public async Task<string> InstallFfmpegAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        AppPaths.Ensure();
        var root = AppPaths.ManagedFfmpegDirectory;
        var binDir = Path.Combine(root, "bin");
        var bin = Path.Combine(binDir, "ffmpeg.exe");
        var ffprobeBin = Path.Combine(binDir, "ffprobe.exe");
        if (File.Exists(bin) && File.Exists(ffprobeBin)) return bin;

        // Migrate an older app-local install if it is still available. This avoids a
        // re-download for users upgrading from early portable builds.
        var legacyFfmpeg = Path.Combine(AppPaths.BundledFfmpegDirectory, "bin", "ffmpeg.exe");
        var legacyFfprobe = Path.Combine(AppPaths.BundledFfmpegDirectory, "bin", "ffprobe.exe");
        if (File.Exists(legacyFfmpeg) && File.Exists(legacyFfprobe))
        {
            Directory.CreateDirectory(binDir);
            File.Copy(legacyFfmpeg, bin, true);
            File.Copy(legacyFfprobe, ffprobeBin, true);
            log?.Invoke("Migrated FFmpeg to ZeroVideo's persistent tools folder.");
            return bin;
        }

        Directory.CreateDirectory(root);
        var zip = Path.Combine(Path.GetTempPath(), "zerovideo-ffmpeg.zip");
        var extract = Path.Combine(Path.GetTempPath(), "zerovideo-ffmpeg-extract");
        try
        {
            log?.Invoke("Downloading FFmpeg essentials for ZeroVideo...");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            await using (var src = await http.GetStreamAsync(FfmpegUrl, ct))
            await using (var dst = File.Create(zip)) await src.CopyToAsync(dst, ct);

            if (Directory.Exists(extract)) Directory.Delete(extract, true);
            ZipFile.ExtractToDirectory(zip, extract);
            var ffmpeg = Directory.EnumerateFiles(extract, "ffmpeg.exe", SearchOption.AllDirectories).First();
            var ffprobe = Directory.EnumerateFiles(extract, "ffprobe.exe", SearchOption.AllDirectories).First();
            Directory.CreateDirectory(binDir);
            File.Copy(ffmpeg, bin, true);
            File.Copy(ffprobe, ffprobeBin, true);
            log?.Invoke($"FFmpeg installed persistently: {bin}");
            return bin;
        }
        finally
        {
            try { if (File.Exists(zip)) File.Delete(zip); } catch { }
            try { if (Directory.Exists(extract)) Directory.Delete(extract, true); } catch { }
        }
    }
}
