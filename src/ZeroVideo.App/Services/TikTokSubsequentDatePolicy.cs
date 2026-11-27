using System.Globalization;
using System.Text.RegularExpressions;

namespace ZeroVideo.App.Services;

/// <summary>
/// TikTok date read-back, scoped to subsequent videos in a sequential batch.
/// A day is valid only when its full calendar date matches the assigned slot.
/// </summary>
public static class TikTokSubsequentDatePolicy
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// The visible closed-picker label wins over stale input values. TikTok
    /// sometimes keeps the prior date in a hidden nested input, especially
    /// when uploading consecutive videos in one authenticated Edge session.
    /// Return a complete date, not captions or labels around the date.
    /// </summary>
    public static string PreferVisibleDate(string? visibleText, string? ownInput,
        string? nestedInput)
    {
        foreach (var candidate in new[] { visibleText, ownInput, nestedInput })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var text = candidate.Trim();
            var month = Regex.Match(text,
                @"\b[A-Za-z]{3,9}\s+\d{1,2}(?:st|nd|rd|th)?,?\s+\d{4}\b",
                RegexOptions.IgnoreCase);
            if (month.Success)
            {
                return Regex.Replace(month.Value, @"(?<=\d)(st|nd|rd|th)\b", "",
                    RegexOptions.IgnoreCase);
            }

            var iso = Regex.Match(text, @"\b\d{4}-\d{2}-\d{2}\b");
            if (iso.Success) return iso.Value;
        }

        return (visibleText ?? ownInput ?? nestedInput ?? "").Trim();
    }

    public static bool MatchesDisplayedDate(string? displayed, DateTime target)
    {
        if (string.IsNullOrWhiteSpace(displayed)) return false;
        var text = displayed.Trim();
        // Never assume the year in an ambiguous month/day-only UI.
        if (!Regex.IsMatch(text, @"(?<!\d)\d{4}(?!\d)")) return false;
        return DateTime.TryParse(text, Culture, DateTimeStyles.None, out var actual) &&
            actual.Date == target.Date;
    }

    public static bool MatchesCalendarDay(string? ariaLabel, string? isoDate, DateTime target)
    {
        if (string.Equals(isoDate?.Trim(), target.ToString("yyyy-MM-dd", Culture),
                StringComparison.Ordinal)) return true;
        if (string.IsNullOrWhiteSpace(ariaLabel)) return false;
        // TikTok has used weekday + ordinal (Friday, November 6th, 2026).
        // Strip *only* ordinal suffixes, not the month or year.
        var normalized = Regex.Replace(ariaLabel.Trim(), @"(?<=\d)(st|nd|rd|th)\b",
            "", RegexOptions.IgnoreCase);
        return MatchesDisplayedDate(normalized, target);
    }

    public static int MonthsToTarget(int shownYear, int shownMonth, DateTime target)
        => (target.Year - shownYear) * 12 + target.Month - shownMonth;
}
