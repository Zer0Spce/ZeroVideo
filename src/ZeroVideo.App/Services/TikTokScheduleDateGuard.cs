using System.Text.RegularExpressions;

namespace ZeroVideo.App.Services;

/// <summary>
/// Guarantees that a TikTok calendar click targets a date widget, never
/// a page-wide text match such as a historical date in the video caption.
/// </summary>
public static class TikTokScheduleDateGuard
{
    public static bool IsDateControl(string? elementClass, string? parentClass,
        string? ariaLabel, string? placeholder, string? testId, string? visibleText,
        bool insideEditor, bool insideFinalSubmission)
    {
        if (insideEditor || insideFinalSubmission) return false;
        if ((visibleText ?? "").Length > 85) return false;
        var hints = string.Join(" ", elementClass, parentClass, ariaLabel, placeholder, testId);
        if (!Regex.IsMatch(hints, @"date|calendar", RegexOptions.IgnoreCase))
            return false;
        if (Regex.IsMatch(hints, @"privacy|copyright|music|time-picker|timepicker",
            RegexOptions.IgnoreCase))
            return false;
        return true;
    }
}
