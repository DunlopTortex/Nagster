namespace DGNaggingAlarms.Calendars;

/// <summary>Why an event was or wasn't turned into an alarm; used by "Check" in the calendar window.</summary>
public enum ImportDecision { Import, AlreadyImported, Past, Cancelled, AllDay, Declined, Free, Rule }

public sealed record PlannedEvent(CalendarEvent Event, ImportDecision Decision, AlarmDefinition? Alarm);

/// <summary>
/// Decides which fetched events become alarms. Pure (no I/O) so the rules are easy to test: an event is
/// imported once, in the future, when no skip setting or ignore rule applies and neither its provider ID nor
/// its cross-service fingerprint has been seen before.
/// </summary>
public static class ImportPlanner
{
    /// <summary>All-day events (when not skipped) remind at this time on the day.</summary>
    public static readonly TimeSpan AllDayReminderTime = TimeSpan.FromHours(9);

    public static IReadOnlyList<PlannedEvent> Plan(IEnumerable<CalendarEvent> events, CalendarSource source,
        CalendarSyncSettings settings, ISet<string> seenKeys, DateTime now)
    {
        var plans = new List<PlannedEvent>();
        // Keys taken earlier in this same batch also count, so a calendar listing an event twice imports it once.
        var taken = new HashSet<string>(seenKeys, StringComparer.Ordinal);
        foreach (var calendarEvent in events.OrderBy(e => e.Start))
        {
            var decision = Decide(calendarEvent, settings, taken, now);
            AlarmDefinition? alarm = null;
            if (decision == ImportDecision.Import)
            {
                alarm = ToAlarm(calendarEvent, source);
                taken.Add(calendarEvent.Id);
                taken.Add(calendarEvent.Fingerprint);
            }
            plans.Add(new PlannedEvent(calendarEvent, decision, alarm));
        }
        return plans;
    }

    private static ImportDecision Decide(CalendarEvent e, CalendarSyncSettings settings, HashSet<string> taken, DateTime now)
    {
        if (e.Cancelled) return ImportDecision.Cancelled;
        if (FirstDue(e) <= now) return ImportDecision.Past;
        if (taken.Contains(e.Id) || taken.Contains(e.Fingerprint)) return ImportDecision.AlreadyImported;
        if (settings.SkipAllDay && e.AllDay) return ImportDecision.AllDay;
        if (settings.SkipDeclined && e.Declined) return ImportDecision.Declined;
        if (settings.SkipFree && e.Free) return ImportDecision.Free;
        if (settings.Rules.Any(rule => rule.Matches(e))) return ImportDecision.Rule;
        return ImportDecision.Import;
    }

    private static DateTime FirstDue(CalendarEvent e) => e.AllDay ? e.Start.Date + AllDayReminderTime : e.Start;

    private static AlarmDefinition ToAlarm(CalendarEvent e, CalendarSource source)
    {
        var start = FirstDue(e);
        var end = e.AllDay ? e.Start.Date.AddDays(1) : e.End;
        var details = new[] { e.Location.Trim().Length > 0 ? "Where: " + e.Location.Trim() : "", e.Description.Trim() }
            .Where(part => part.Length > 0);
        var description = string.Join("\n", details);
        return new AlarmDefinition
        {
            Title = e.Title.Trim().Length > 0 ? Truncate(e.Title.Trim(), 160) : "(No title)",
            Description = Truncate(description, 1000),
            IsEvent = true,
            FirstDue = start,
            EventEnd = end > start ? end : start.AddMinutes(30),
            ReminderMinutes = e.AllDay ? 0 : Math.Clamp(source.LeadMinutes, 0, 10080),
            ImportedFrom = source.Name
        };
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
