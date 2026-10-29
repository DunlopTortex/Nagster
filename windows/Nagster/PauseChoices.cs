namespace Nagster;

/// <summary>
/// The tray's "Pause alarms for" choices. Each works out when the pause ends from the moment it is chosen,
/// either a length of time or a fixed time such as tomorrow morning; DateTime.MaxValue means until Unpause.
/// </summary>
public static class PauseChoices
{
    private static readonly TimeSpan Morning = new(9, 0, 0);
    private static readonly TimeSpan Afternoon = new(12, 0, 0);

    public static readonly (string Label, Func<DateTime, DateTime> Until)[] All =
    [
        ("30 minutes", now => now.AddMinutes(30)), ("1 hour", now => now.AddHours(1)), ("4 hours", now => now.AddHours(4)),
        ("1 day", now => now.AddDays(1)), ("Tomorrow morning", now => now.Date.AddDays(1).Add(Morning)),
        ("Tomorrow afternoon", now => now.Date.AddDays(1).Add(Afternoon)), ("Monday", NextMonday),
        ("Forever", _ => DateTime.MaxValue)
    ];

    /// <summary>9am on the coming Monday; on a Monday, that is a week later.</summary>
    public static DateTime NextMonday(DateTime now)
    {
        var days = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        return now.Date.AddDays(days == 0 ? 7 : days).Add(Morning);
    }
}
