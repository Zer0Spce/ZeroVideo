namespace ZeroVideo.App.Services;

/// <summary>
/// Exact 16:9 / 9:16 output dimensions for local ComfyUI images.
/// Direct uses the chosen latent size; Upscale generates at the safe
/// 1024x576 working size, then scales the saved PNG with CPU FFmpeg.
/// Changing quality never changes the video export aspect ratio.
/// </summary>
public static class LocalImageResolutionService
{
    public const string Fast = "Fast";
    public const string Balanced = "Balanced";
    public const string High = "High";
    public const string Ultra = "Ultra";

    public const string Direct = "Direct";
    public const string Upscale = "Upscale";

    public readonly record struct Resolution(int Width, int Height)
    {
        public override string ToString() => $"{Width}x{Height}";
    }

    public readonly record struct ResolutionPlan(
        Resolution Working, Resolution Output, bool CpuUpscale)
    {
        public bool UsesHighGpuMemory =>
            !CpuUpscale && Output.Width * Output.Height > 1280 * 720;
    }

    public static bool IsValidPreset(string? preset) =>
        preset is Fast or Balanced or High or Ultra;

    public static bool IsValidMode(string? mode) =>
        mode is Direct or Upscale;

    public static Resolution ResolveOutput(bool portrait, string? preset)
    {
        var (w, h) = preset switch
        {
            Balanced => (1280, 720),
            High => (1600, 900),
            Ultra => (1920, 1080),
            _ => (1024, 576)
        };
        return portrait ? new(h, w) : new(w, h);
    }

    public static ResolutionPlan Resolve(
        bool portrait, string? preset, string? mode)
    {
        var final = ResolveOutput(portrait, preset);
        var cpuUpscale = mode == Upscale && preset != Fast;
        var working = cpuUpscale ?
            ResolveOutput(portrait, Fast) : final;
        return new ResolutionPlan(working, final, cpuUpscale);
    }
}
