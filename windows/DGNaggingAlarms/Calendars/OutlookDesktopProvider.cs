using System.Globalization;
using System.Runtime.InteropServices;

namespace DGNaggingAlarms.Calendars;

/// <summary>
/// Reads calendars from classic desktop Outlook on this PC through its COM automation interface, so any account
/// Outlook shows (Exchange, Microsoft 365, IMAP, PST) works without a separate sign-in. The new Outlook for Windows
/// has no COM interface and isn't supported. Only fields that don't trigger Outlook's security prompt are read
/// (notably not the event body).
/// </summary>
public sealed class OutlookDesktopProvider : ICalendarProvider
{
    // Outlook object model constants.
    private const int CalendarFolder = 9;          // olFolderCalendar
    private const int BusyFree = 0;                // olFree
    private const int ResponseDeclined = 4;        // olResponseDeclined
    private const int MeetingCanceled = 5;         // olMeetingCanceled
    private const int MeetingReceivedAndCanceled = 7;
    private const int MaxItemsPerCalendar = 2000;  // Guard against runaway recurrence expansion.

    public static bool IsInstalled => Type.GetTypeFromProgID("Outlook.Application") is not null;

    public Task<IReadOnlyList<CalendarEvent>> FetchAsync(CalendarSource source, CalendarSyncSettings settings,
        DateTime from, DateTime to, CancellationToken token)
    {
        // COM automation needs a single-threaded apartment; a dedicated thread keeps Outlook from blocking the UI.
        var result = new TaskCompletionSource<IReadOnlyList<CalendarEvent>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(Read(source.AllCalendars, from, to, token)); }
            catch (Exception ex) { result.SetException(ex as CalendarException ?? Translate(ex)); }
        }) { IsBackground = true, Name = "Outlook calendar reader" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static CalendarException Translate(Exception ex) => ex switch
    {
        COMException com when (uint)com.HResult == 0x80080005 =>
            new CalendarException("Outlook is busy or running as a different user. Try again in a moment.", ex),
        COMException com => new CalendarException("Outlook couldn't be read: " + com.Message, ex),
        _ => new CalendarException("Outlook couldn't be read: " + ex.Message, ex)
    };

    private static IReadOnlyList<CalendarEvent> Read(bool allCalendars, DateTime from, DateTime to, CancellationToken token)
    {
        var outlookType = Type.GetTypeFromProgID("Outlook.Application")
            ?? throw new CalendarException("Classic Outlook isn't installed on this PC. (The new Outlook for Windows can't be read by other apps.)");
        dynamic outlook = Activator.CreateInstance(outlookType)!; // Attaches to Outlook if it's open, otherwise starts it hidden.
        dynamic session = outlook.GetNamespace("MAPI");
        var events = new List<CalendarEvent>();
        try
        {
            foreach (var folder in CalendarFolders(session, allCalendars))
            {
                try { events.AddRange(ReadFolder(folder, from, to, token)); }
                finally { Marshal.ReleaseComObject(folder); }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(session);
            Marshal.ReleaseComObject(outlook); // Releases our reference only; Outlook keeps running for the user.
        }
        return events;
    }

    /// <summary>The default calendar, or with <paramref name="all"/> the default calendar of every account in Outlook.</summary>
    private static List<object> CalendarFolders(dynamic session, bool all)
    {
        if (!all) return [session.GetDefaultFolder(CalendarFolder)];
        var folders = new List<object>();
        dynamic stores = session.Stores;
        for (var i = 1; i <= (int)stores.Count; i++)
        {
            dynamic store = stores.Item(i);
            try { folders.Add(store.GetDefaultFolder(CalendarFolder)); }
            catch (COMException) { } // Stores without a calendar (e.g. archives) throw; skip them.
            finally { Marshal.ReleaseComObject(store); }
        }
        Marshal.ReleaseComObject(stores);
        return folders;
    }

    private static IEnumerable<CalendarEvent> ReadFolder(dynamic folder, DateTime from, DateTime to, CancellationToken token)
    {
        string calendarName = folder.Name;
        dynamic items = folder.Items;
        // Sort then IncludeRecurrences, in that order, so Restrict expands repeating appointments into occurrences.
        items.Sort("[Start]");
        items.IncludeRecurrences = true;
        // Outlook's filter expects dates in the user's own short date/time format.
        var filter = $"[Start] < '{to.ToString("g", CultureInfo.CurrentCulture)}' AND [End] > '{from.ToString("g", CultureInfo.CurrentCulture)}'";
        dynamic matches = items.Restrict(filter);
        var results = new List<CalendarEvent>();
        try
        {
            var count = 0;
            for (dynamic? item = matches.GetFirst(); item is not null && count++ < MaxItemsPerCalendar; item = matches.GetNext())
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    DateTime start = item.Start;
                    if (start >= to) break; // Sorted by start, so nothing later can be in range.
                    results.Add(Map(item, calendarName, start));
                }
                finally { Marshal.ReleaseComObject(item); }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(matches);
            Marshal.ReleaseComObject(items);
        }
        return results;
    }

    private static CalendarEvent Map(dynamic item, string calendarName, DateTime start)
    {
        DateTime end = item.End;
        bool recurring = item.IsRecurring;
        string globalId = item.GlobalAppointmentID;
        int meetingStatus = item.MeetingStatus;
        // Outlook doesn't expose an occurrence's original start, so a series occurrence is keyed by its current start.
        return new CalendarEvent(
            "outlook:" + globalId + (recurring ? "|" + EventIds.Utc(start) : ""),
            (string?)item.Subject ?? "", (string?)item.Location ?? "", "", start, end, (bool)item.AllDayEvent,
            Declined: (int)item.ResponseStatus == ResponseDeclined,
            Free: (int)item.BusyStatus == BusyFree,
            Cancelled: meetingStatus is MeetingCanceled or MeetingReceivedAndCanceled,
            calendarName);
    }
}
