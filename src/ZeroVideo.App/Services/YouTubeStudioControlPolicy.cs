using System.Globalization;
using System.Text.RegularExpressions;

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

    // In Studio's Visibility step, Schedule is an expandable CARD, not a
    // radio with name='SCHEDULE'. Only search inside that Visibility widget.
    public static readonly string[] ScheduleCardSelectors =
    [
        "#schedule-radio-button",
        "[name='SCHEDULE']",
        "[role='radio'][aria-label='Schedule']"
    ];

    // A private-draft "Save" must NEVER be accepted as a scheduled publish.
    // The completed Schedule card changes YouTube Studio's final CTA to
    // "Schedule"; a regular Public post changes it to "Publish".
    public static bool IsExpectedFinalAction(bool scheduled, string? buttonLabel)
    {
        var text=(buttonLabel??"").Trim();
        return scheduled
            ? text.Equals("Schedule",StringComparison.OrdinalIgnoreCase)
            : text.Equals("Publish",StringComparison.OrdinalIgnoreCase);
    }

    private static readonly CultureInfo StudioCulture =
        CultureInfo.GetCultureInfo("en-US");

    public static bool MatchesScheduleDate(string? text, DateTime target)
    {
        if(string.IsNullOrWhiteSpace(text))return false;
        var value=Regex.Match(text,
            @"\b(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+\d{1,2},?\s+\d{4}\b|\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2}/\d{1,2}/\d{4}\b",
            RegexOptions.IgnoreCase);
        return value.Success &&
            DateTime.TryParse(value.Value,StudioCulture,
                DateTimeStyles.None,out var parsed) &&
            parsed.Date==target.Date;
    }

    public static bool MatchesScheduleTime(string? text, DateTime target)
    {
        if(string.IsNullOrWhiteSpace(text))return false;
        var value=Regex.Match(text,
            @"\b\d{1,2}:\d{2}\s*(?:AM|PM)\b|\b\d{1,2}:\d{2}\b",
            RegexOptions.IgnoreCase);
        return value.Success &&
            DateTime.TryParse(value.Value,StudioCulture,
                DateTimeStyles.None,out var parsed) &&
            parsed.Hour==target.Hour && parsed.Minute==target.Minute;
    }

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
