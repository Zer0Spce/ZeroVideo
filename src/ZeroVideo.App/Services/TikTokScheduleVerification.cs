using System.Globalization;
using System.Text.RegularExpressions;

namespace ZeroVideo.App.Services;

/// <summary>
/// Fail-closed verification for TikTok Studio's displayed schedule controls.
/// Text is read from the actual date/time trigger immediately before clicking
/// Schedule; a previous video's selected date must never count as success.
/// </summary>
public static class TikTokScheduleVerification
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    private static readonly string[] TimeFormats =
    [
        "H:mm", "HH:mm", "h:mm tt", "hh:mm tt", "h:mmtt", "hh:mmtt",
        "H:mm:ss", "HH:mm:ss", "h:mm:ss tt", "hh:mm:ss tt"
    ];

    public static bool DateMatches(string? displayed, DateTime expected)
    {
        if (string.IsNullOrWhiteSpace(displayed)) return false;
        var text = displayed.Trim();
        // A month/day without a year is not sufficient evidence for a scheduled
        // post (particularly across the year boundary).
        if (!Regex.IsMatch(text, @"(?<!\d)\d{4}(?!\d)")) return false;
        return DateTime.TryParse(text, Culture, DateTimeStyles.None, out var parsed) &&
               parsed.Date == expected.Date;
    }

    public static bool TimeMatches(string? displayed, DateTime expected)
    {
        if (string.IsNullOrWhiteSpace(displayed)) return false;
        var text = Regex.Replace(displayed.Trim(), @"\s+", " ");
        return DateTime.TryParseExact(text, TimeFormats, Culture,
                   DateTimeStyles.None, out var parsed) &&
               parsed.Hour == expected.Hour && parsed.Minute == expected.Minute;
    }

    public static bool SlotMatches(string? displayedDate, string? displayedTime,
        DateTime expected)
        => DateMatches(displayedDate, expected) && TimeMatches(displayedTime, expected);
}
