using System.Net.Http;
using DGNaggingAlarms.Notifications;
using Ical.Net;
using Ical.Net.DataTypes;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace DGNaggingAlarms.Calendars;

/// <summary>
/// Reads a calendar's secret iCalendar (ICS) link, as offered by Google ("Secret address in iCal format") and
/// Outlook ("Publish calendar"). Repeating events are expanded by Ical.Net, honouring moved and cancelled instances.
/// </summary>
public sealed class IcsCalendarProvider : ICalendarProvider
{
    private const long MaxDownloadBytes = 20 * 1024 * 1024;
    private static readonly HttpClient Http = CreateClient();

    public async Task<IReadOnlyList<CalendarEvent>> FetchAsync(CalendarSource source, CalendarSyncSettings settings,
        DateTime from, DateTime to, CancellationToken token)
    {
        var url = NormaliseUrl(SecretProtector.Unprotect(source.ProtectedIcsUrl));
        if (url is null) throw new CalendarException("The calendar link isn't a valid web address.");
        string text;
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
                throw new CalendarException($"The calendar link returned HTTP {(int)response.StatusCode}. Check it hasn't been reset.");
            if (response.Content.Headers.ContentLength > MaxDownloadBytes) throw new CalendarException("The calendar is too large to read.");
            text = await response.Content.ReadAsStringAsync(token);
        }
        catch (HttpRequestException ex) { throw new CalendarException("Couldn't download the calendar: " + ex.Message, ex); }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested) { throw new CalendarException("The calendar took too long to download.", ex); }
        return Parse(text, source.Name, from, to);
    }

    /// <summary>Parses ICS text into occurrences in the window. Public so it can be tested without a network.</summary>
    public static IReadOnlyList<CalendarEvent> Parse(string ics, string calendarName, DateTime from, DateTime to)
    {
        Calendar calendar;
        try { calendar = Calendar.Load(ics) ?? throw new CalendarException("The link didn't return a calendar."); }
        catch (Exception ex) when (ex is not CalendarException) { throw new CalendarException("The link didn't return a valid calendar.", ex); }
        var name = calendar.Properties.Get<string>("X-WR-CALNAME") is { Length: > 0 } title ? title : calendarName;

        var events = new List<CalendarEvent>();
        // Start a day early so events already under way, and all-day events in other time zones, are included.
        var start = new CalDateTime(from.ToUniversalTime().AddDays(-1));
        var end = new CalDateTime(to.ToUniversalTime());
        foreach (var occurrence in calendar.GetOccurrences(start, null).TakeWhileBefore(end))
        {
            if (occurrence.Source is not IcalEvent ev) continue;
            var period = occurrence.Period;
            var allDay = !period.StartTime.HasTime;
            var localStart = Local(period.StartTime);
            var localEnd = period.EffectiveEndTime is { } endTime ? Local(endTime) : localStart.AddHours(1);
            if (localEnd <= from || localStart >= to) continue;

            var recurring = ev.RecurrenceRule is not null || ev.RecurrenceDates.GetAllPeriods().Any() || ev.RecurrenceIdentifier is not null;
            // The original start (RECURRENCE-ID) keeps a moved occurrence's identity; a one-off event needs only its UID.
            var originalStart = ev.RecurrenceIdentifier is { } recurrenceId ? Local(recurrenceId.StartTime) : localStart;
            events.Add(new CalendarEvent(
                EventIds.FromUid(ev.Uid ?? "", recurring ? originalStart : null, allDay),
                ev.Summary ?? "", ev.Location ?? "", ev.Description ?? "", localStart, localEnd, allDay,
                Declined: false, // An ICS feed doesn't say which attendee is you.
                Free: string.Equals(ev.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase),
                Cancelled: string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase),
                name));
        }
        return events;
    }

    /// <summary>All-day dates stay as dates; timed values are converted to this PC's time zone.</summary>
    private static DateTime Local(CalDateTime value) =>
        value.HasTime ? value.AsUtc.ToLocalTime() : DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Local);

    /// <summary>Accepts webcal:// links (what calendar apps usually hand out) as https.</summary>
    public static Uri? NormaliseUrl(string text)
    {
        text = text.Trim();
        if (text.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) text = "https://" + text["webcal://".Length..];
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45), MaxResponseContentBufferSize = MaxDownloadBytes };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DGNaggingAlarms/1.0");
        return client;
    }
}
