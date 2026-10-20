namespace Nagster.Calendars;

/// <summary>Reads event occurrences from one kind of calendar. Fetching never prompts the user to sign in.</summary>
public interface ICalendarProvider
{
    /// <summary>Returns occurrences overlapping [<paramref name="from"/>, <paramref name="to"/>), times in local time.</summary>
    /// <exception cref="CalendarException">The calendar can't be read; the message is shown to the user.</exception>
    Task<IReadOnlyList<CalendarEvent>> FetchAsync(CalendarSource source, CalendarSyncSettings settings,
        DateTime from, DateTime to, CancellationToken token);
}

/// <summary>A calendar problem with a message written for the user, e.g. "Sign in again to read this calendar".</summary>
public sealed class CalendarException(string message, Exception? inner = null) : Exception(message, inner);
