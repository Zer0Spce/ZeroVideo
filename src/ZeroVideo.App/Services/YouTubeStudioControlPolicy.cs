namespace ZeroVideo.App.Services;

/// <summary>
/// Explicit YouTube Studio UI component selectors. Prefer exact component
/// IDs and stable test attributes over role-based or page-wide text matches.
/// Do not identify the toolbar's Create control by visible text alone.
/// </summary>
public static class YouTubeStudioControlPolicy
{
    public static readonly string[] CreateSelectors =
    [
        "ytcp-button#create-icon",
        "#create-icon",
        "ytcp-button[aria-label='Create']",
        "button[aria-label='Create']"
    ];

    public static readonly string[] UploadMenuSelectors =
    [
        "tp-yt-paper-item[test-id='upload-beta']",
        "tp-yt-paper-item[aria-label='Upload videos']"
    ];

    public static bool IsSafeCreateSelector(string? selector)
        => !string.IsNullOrWhiteSpace(selector) &&
            CreateSelectors.Contains(selector, StringComparer.Ordinal);

    public static bool IsSafeUploadMenuSelector(string? selector)
        => !string.IsNullOrWhiteSpace(selector) &&
            UploadMenuSelectors.Contains(selector, StringComparer.Ordinal);

    // Upload inputs in YouTube Studio can be nested in shadow roots. Reject
    // thumbnail/image pickers and inputs outside the upload dialog unless
    // they explicitly accept MP4/video content.
    public static bool IsEligibleUploadInput(
        string? accepts, bool insideUploadDialog, bool disabled)
    {
        if (disabled) return false;
        var accept = (accepts ?? "").ToLowerInvariant();
        var forVideo = accept.Contains("video", StringComparison.Ordinal) ||
            accept.Contains(".mp4", StringComparison.Ordinal);
        var imageOnly = accept.Contains("image", StringComparison.Ordinal) &&
            !forVideo;
        return !imageOnly && (insideUploadDialog || forVideo);
    }
}
