using System.Globalization;

namespace DGNaggingAlarms.Calendars;

/// <summary>
/// One occurrence of a calendar event, normalised from any provider. Times are local. <see cref="Id"/> is the
/// provider's stable identity for this occurrence (it survives the event being moved); see <see cref="EventIds"/>.
/// </summary>
public sealed record CalendarEvent(
    string Id, string Title, string Location, string Description, DateTime Start, DateTime End,
    bool AllDay, bool Declined, bool Free, bool Cancelled, string CalendarName)
{
    /// <summary>
    /// Identifies the same real-world event across different services (e.g. a meeting in both Google and Outlook),
    /// whose provider IDs differ.
    /// </summary>
    public string Fingerprint =>
        $"fp:{Title.Trim().ToLowerInvariant()}|{(AllDay ? Start.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : EventIds.Utc(Start))}";
}

/// <summary>Builds provider IDs so the same event gets the same key from ICS links and the Google API.</summary>
public static class EventIds
{
    /// <summary>
    /// "uid:{iCalendar UID}" for a one-off event, or "uid:{UID}|{original start}" for one occurrence of a series.
    /// The original start (RECURRENCE-ID) is used so a moved occurrence keeps its identity.
    /// </summary>
    public static string FromUid(string uid, DateTime? originalStart, bool originalIsAllDay = false) =>
        originalStart is DateTime start
            ? $"uid:{uid}|{(originalIsAllDay ? start.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : Utc(start))}"
            : $"uid:{uid}";

    public static string Utc(DateTime value) =>
        (value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime()).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}
