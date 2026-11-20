using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Both vertical Shorts and widescreen Longform need visible captions.
/// Exporting an .srt alone does not show subtitles in an MP4 player.
/// </summary>
public static class CaptionRenderPolicy
{
    public static bool ShouldBurn(ProjectOptions options, string aspectRatio) =>
        options.BurnInBoldCaptions &&
        aspectRatio is "9:16" or "16:9";

    public static bool IsLandscape(string aspectRatio) =>
        aspectRatio == "16:9";

    public static string AssCanvas(string aspectRatio) =>
        IsLandscape(aspectRatio)
            ? "PlayResX: 1920\nPlayResY: 1080"
            : "PlayResX: 1080\nPlayResY: 1920";

    public static string AssStyle(string aspectRatio) =>
        IsLandscape(aspectRatio)
            // 16:9: white, bold, shadow-free black outline, bottom-centered
            // well above the video edge with wide safe horizontal margins.
            ? "Style: Punchy,Arial Black,66,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,5,0,2,195,195,118,1"
            : "Style: Punchy,Arial Black,76,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,6,0,2,145,145,350,1";
}
