using Nagster.Notifications;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using GoogleEvent = Google.Apis.Calendar.v3.Data.Event;

namespace Nagster.Calendars;

/// <summary>
/// Reads a Google account's calendars through the Google Calendar API (read-only scope). Signing in opens the
/// browser once; afterwards the stored refresh token is used silently.
/// </summary>
public sealed class GoogleCalendarProvider : ICalendarProvider
{
    private static readonly string[] Scopes = [CalendarService.Scope.CalendarReadonly];

    /// <summary>Opens Google's sign-in page in the browser and returns the signed-in account's e-mail address.</summary>
    /// <exception cref="CalendarException">Sign-in failed or the app registration is missing.</exception>
    public static async Task<string> SignInAsync(CalendarSource source, CalendarSyncSettings settings, CancellationToken token)
    {
        try
        {
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(Secrets(settings), Scopes, UserKey(source), token,
                new EncryptedGoogleTokenStore());
            using var service = Service(credential);
            var primary = await service.CalendarList.Get("primary").ExecuteAsync(token);
            return primary.Id;
        }
        catch (Exception ex) when (ex is TokenResponseException or GoogleApiException)
        {
            throw new CalendarException("Google sign-in didn't complete: " + ex.Message, ex);
        }
    }

    public async Task<IReadOnlyList<CalendarEvent>> FetchAsync(CalendarSource source, CalendarSyncSettings settings,
        DateTime from, DateTime to, CancellationToken token)
    {
        using var service = Service(await StoredCredential(source, settings, token));
        try
        {
            var calendars = source.AllCalendars
                ? (await service.CalendarList.List().ExecuteAsync(token)).Items.Where(c => c.Selected == true && c.Deleted != true)
                    .Select(c => (c.Id, Name: c.SummaryOverride ?? c.Summary)).ToList()
                : [("primary", source.Account)];
            var events = new List<CalendarEvent>();
            foreach (var (calendarId, calendarName) in calendars)
                events.AddRange(await FetchCalendar(service, calendarId, calendarName ?? calendarId, from, to, token));
            return events;
        }
        catch (TokenResponseException ex)
        {
            throw new CalendarException("Google no longer accepts this sign-in. Use \"Sign in again\".", ex);
        }
        catch (GoogleApiException ex)
        {
            throw new CalendarException("Google Calendar returned an error: " + ex.Message, ex);
        }
    }

    private static async Task<IEnumerable<CalendarEvent>> FetchCalendar(CalendarService service, string calendarId, string calendarName,
        DateTime from, DateTime to, CancellationToken token)
    {
        var request = service.Events.List(calendarId);
        request.SingleEvents = true; // Expands repeating events into individual occurrences.
        request.ShowDeleted = false;
        request.TimeMinDateTimeOffset = new DateTimeOffset(from);
        request.TimeMaxDateTimeOffset = new DateTimeOffset(to);
        request.MaxResults = 250;
        var results = new List<CalendarEvent>();
        do
        {
            var page = await request.ExecuteAsync(token);
            results.AddRange((page.Items ?? []).Select(e => Map(e, calendarName)).OfType<CalendarEvent>());
            request.PageToken = page.NextPageToken;
        } while (request.PageToken is not null);
        return results;
    }

    private static CalendarEvent? Map(GoogleEvent e, string calendarName)
    {
        var allDay = e.Start?.Date is not null;
        var start = When(e.Start);
        if (start is null) return null;
        var end = When(e.End) ?? start.Value.AddHours(1);
        var original = e.RecurringEventId is null ? null : When(e.OriginalStartTime);
        return new CalendarEvent(
            EventIds.FromUid(e.ICalUID ?? e.Id, original, e.OriginalStartTime?.Date is not null),
            e.Summary ?? "", e.Location ?? "", e.Description ?? "", start.Value, end, allDay,
            Declined: e.Attendees?.Any(a => a.Self == true && a.ResponseStatus == "declined") == true,
            Free: e.Transparency == "transparent",
            Cancelled: e.Status == "cancelled",
            calendarName);
    }

    /// <summary>Timed values are converted to local time; all-day values ("2026-10-20") are dates.</summary>
    private static DateTime? When(EventDateTime? value) =>
        value?.DateTimeDateTimeOffset is DateTimeOffset moment ? moment.LocalDateTime
        : DateTime.TryParse(value?.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
            ? DateTime.SpecifyKind(date.Date, DateTimeKind.Local) : null;

    private static async Task<UserCredential> StoredCredential(CalendarSource source, CalendarSyncSettings settings, CancellationToken token)
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = Secrets(settings), Scopes = Scopes, DataStore = new EncryptedGoogleTokenStore()
        });
        var stored = await flow.LoadTokenAsync(UserKey(source), token)
            ?? throw new CalendarException("Not signed in to Google on this PC. Use \"Sign in again\".");
        return new UserCredential(flow, UserKey(source), stored);
    }

    private static ClientSecrets Secrets(CalendarSyncSettings settings)
    {
        var secret = SecretProtector.Unprotect(settings.ProtectedGoogleClientSecret);
        if (settings.GoogleClientId.Trim().Length == 0 || secret.Length == 0)
            throw new CalendarException("Enter your Google client ID and secret under \"App registrations\" first.");
        return new ClientSecrets { ClientId = settings.GoogleClientId.Trim(), ClientSecret = secret };
    }

    private static string UserKey(CalendarSource source) => source.Id.ToString("N");

    private static CalendarService Service(UserCredential credential) =>
        new(new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "Nagster" });
}
