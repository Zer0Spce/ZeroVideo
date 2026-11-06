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
}
