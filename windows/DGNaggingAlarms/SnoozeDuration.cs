using System.Globalization;

namespace DGNaggingAlarms;

public enum SnoozeUnit { Seconds, Minutes, Hours, Days, Weeks, Months, Years }

/// <summary>
/// A snooze length such as "30m", "1d" or "2w", as entered in the Snooze choices setting.
/// A bare number means minutes so settings saved before units existed keep working. Months
/// and years use calendar arithmetic, so one month from 31 January lands at the end of February.
/// </summary>
public readonly record struct SnoozeDuration(int Amount, SnoozeUnit Unit)
{
    public const int MaxAmount = 999;

    private static readonly (string Suffix, SnoozeUnit Unit)[] Suffixes =
    [
        ("mo", SnoozeUnit.Months), ("s", SnoozeUnit.Seconds), ("m", SnoozeUnit.Minutes), ("h", SnoozeUnit.Hours),
        ("d", SnoozeUnit.Days), ("w", SnoozeUnit.Weeks), ("y", SnoozeUnit.Years), ("", SnoozeUnit.Minutes)
    ];

    /// <summary>Reads one entry such as "15", "15m", "1 d" or "2W"; units are case-insensitive.</summary>
    public static bool TryParse(string? text, out SnoozeDuration duration)
    {
        duration = default;
        var trimmed = text?.Trim().ToLowerInvariant() ?? "";
        foreach (var (suffix, unit) in Suffixes)
        {
            if (!trimmed.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var number = trimmed[..^suffix.Length].TrimEnd();
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ||
                amount is < 1 or > MaxAmount) return false;
            duration = new SnoozeDuration(amount, unit);
            return true;
        }
        return false;
    }

    /// <summary>Parses a comma-separated list, returning null if any entry is invalid or the list is empty.</summary>
    public static IReadOnlyList<SnoozeDuration>? TryParseList(string? text)
    {
        var parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var result = new List<SnoozeDuration>();
        foreach (var part in parts)
        {
            if (!TryParse(part, out var duration)) return null;
            result.Add(duration);
        }
        return result.Count == 0 ? null : result;
    }

    public DateTime AddTo(DateTime start) => Unit switch
    {
        SnoozeUnit.Seconds => start.AddSeconds(Amount),
        SnoozeUnit.Minutes => start.AddMinutes(Amount),
        SnoozeUnit.Hours => start.AddHours(Amount),
        SnoozeUnit.Days => start.AddDays(Amount),
        SnoozeUnit.Weeks => start.AddDays(7 * Amount),
        SnoozeUnit.Months => start.AddMonths(Amount),
        _ => start.AddYears(Amount)
    };

    /// <summary>Human-readable form for menus, e.g. "1 day" or "15 minutes".</summary>
    public string Label
    {
        get
        {
            var noun = Unit.ToString().ToLowerInvariant();
            return Amount == 1 ? $"1 {noun[..^1]}" : $"{Amount} {noun}";
        }
    }

    /// <summary>Canonical setting form, e.g. "15m" or "2mo".</summary>
    public override string ToString()
    {
        var unit = Unit;
        return Amount + Suffixes.First(s => s.Unit == unit && s.Suffix != "").Suffix;
    }
}
