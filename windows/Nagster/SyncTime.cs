using System.Globalization;

namespace Nagster;

/// <summary>
/// The wall-clock text form shared with the iPhone app, "yyyy-MM-ddTHH:mm:ss" with no offset. Alarm times are
/// local times without a time zone, so they travel as such; fractions of a second are dropped.
/// </summary>
public static class SyncTime
{
    public const string Pattern = "yyyy-MM-dd'T'HH:mm:ss";

    public static string Format(DateTime value) => value.ToString(Pattern, CultureInfo.InvariantCulture);

    public static string? Format(DateTime? value) => value is DateTime v ? Format(v) : null;

    public static bool TryParse(string? text, out DateTime value) =>
        DateTime.TryParseExact(text, Pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    /// <summary>Parses an optional time: blank is null; anything else must be valid.</summary>
    public static bool TryParseOptional(string? text, out DateTime? value)
    {
        value = null;
        if (string.IsNullOrEmpty(text)) return true;
        if (!TryParse(text, out var parsed)) return false;
        value = parsed;
        return true;
    }
}
