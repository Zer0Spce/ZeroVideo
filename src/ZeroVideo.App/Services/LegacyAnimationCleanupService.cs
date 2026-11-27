using Microsoft.VisualBasic.FileIO;

namespace ZeroVideo.App.Services;

/// <summary>
/// Opt-in cleanup of EXACT legacy animation assets under ZeroVideo's own
/// tool/model locations. Never touches user project media, OmniVoice,
/// SDXL, FLUX, or the global Hugging Face cache. No background deletion.
/// </summary>
public static class LegacyAnimationCleanupService
{
    public sealed record Candidate(string Name, string FullPath, bool IsDirectory,
        long Bytes, bool SharedWithOtherWorkflows);

    private static string ComfyModels =>
        Path.Combine(LocalImageService.PortableRoot, "ComfyUI", "models");

    private static readonly (string Name, string Path, bool Directory, bool Shared)[]
        ExactTargets =
        [
            ("Wan2.2 TI2V 5B diffusion model",
                Path.Combine(ComfyModels, "diffusion_models",
                    "wan2.2_ti2v_5B_fp16.safetensors"), false, false),
            ("Wan2.2 video VAE",
                Path.Combine(ComfyModels, "vae",
                    "wan2.2_vae.safetensors"), false, false),
            ("Wan/other-workflow UMT5 text encoder",
                Path.Combine(ComfyModels, "text_encoders",
                    "umt5_xxl_fp8_e4m3fn_scaled.safetensors"), false, true),
            ("ZeroVideo-managed FramePack package and its model cache",
                Path.Combine(AppPaths.ManagedToolsDirectory, "FramePack"), true, false),
            ("Earlier ZeroVideo-managed FramePack installation",
                Path.Combine(AppPaths.DataDirectory, "Framepack"), true, false)
        ];

    public static IReadOnlyList<Candidate> Inspect(bool includeShared)
    {
        var found = new List<Candidate>();
        foreach (var (name, raw, isDir, shared) in ExactTargets)
        {
            if (shared && !includeShared) continue;
            var full = Path.GetFullPath(raw);
            if (isDir ? !Directory.Exists(full) : !File.Exists(full)) continue;

            // Never follow reparse points or links to external directories.
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                continue;
            long bytes = 0;
            if (isDir)
            {
                try
                {
                    foreach (var path in Directory.EnumerateFiles(
                        full, "*", System.IO.SearchOption.AllDirectories))
                    {
                        // Count contents, but never follow a symlink file.
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                            bytes += new FileInfo(path).Length;
                    }
                }
                catch (IOException) { /* Report only known safe path. */ }
                catch (UnauthorizedAccessException) { }
            }
            else bytes = new FileInfo(full).Length;

            found.Add(new Candidate(name, full, isDir, bytes, shared));
        }
        return found;
    }

    public static (int Removed, int Failed, string Report) Recycle(
        IReadOnlyList<Candidate> approved)
    {
        var outcomes = new List<string>();
        var succeeded = 0;
        var failed = 0;
        foreach (var item in approved)
        {
            // Immutable allowlist: even an accidentally forged UI candidate
            // cannot delete an arbitrary user path.
            if (!ExactTargets.Any(target =>
                target.Directory == item.IsDirectory &&
                string.Equals(Path.GetFullPath(target.Path),
                    Path.GetFullPath(item.FullPath),
                    StringComparison.OrdinalIgnoreCase)))
            {
                failed++;
                outcomes.Add("Not allowed: " + item.Name);
                continue;
            }
            try
            {
                if (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath))
                    continue;
                if ((File.GetAttributes(item.FullPath) &
                     FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Symbolic/reparse paths are not allowed.");
                if (item.IsDirectory)
                    FileSystem.DeleteDirectory(item.FullPath,
                        UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else
                    FileSystem.DeleteFile(item.FullPath,
                        UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                succeeded++;
                outcomes.Add("Recycled: " + item.Name);
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or
                OperationCanceledException or InvalidOperationException)
            {
                failed++;
                outcomes.Add("Could not recycle " + item.Name + ": " + error.Message);
            }
        }
        return (succeeded, failed, string.Join(Environment.NewLine, outcomes));
    }
}
