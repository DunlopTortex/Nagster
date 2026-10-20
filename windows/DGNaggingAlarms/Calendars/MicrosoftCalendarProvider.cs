using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace DGNaggingAlarms.Calendars;

/// <summary>
/// Reads a work or school Microsoft 365 calendar through Microsoft Graph (Calendars.Read). Sign-in uses MSAL with
/// the system browser; MSAL's token cache is encrypted with DPAPI, one cache per calendar.
/// </summary>
public sealed class MicrosoftCalendarProvider : ICalendarProvider
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/Calendars.Read"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    /// <summary>Opens Microsoft's sign-in page in the browser and returns the signed-in user name.</summary>
    public static async Task<string> SignInAsync(CalendarSource source, CalendarSyncSettings settings, CancellationToken token)
    {
        var app = await CreateApp(source, settings);
        try
        {
            var result = await app.AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(token);
            return result.Account.Username;
        }
        catch (MsalException ex)
        {
            throw new CalendarException(ex.ErrorCode == MsalError.AuthenticationCanceledError
                ? "Microsoft sign-in was cancelled."
                : "Microsoft sign-in didn't complete: " + ex.Message, ex);
        }
    }

    public async Task<IReadOnlyList<CalendarEvent>> FetchAsync(CalendarSource source, CalendarSyncSettings settings,
        DateTime from, DateTime to, CancellationToken token)
    {
        var accessToken = await SilentToken(source, settings, token);
        var calendars = source.AllCalendars
            ? (await GetAll(accessToken, "https://graph.microsoft.com/v1.0/me/calendars?$select=id,name", token))
                .Select(c => (Path: $"me/calendars/{c.GetProperty("id").GetString()}", Name: c.GetProperty("name").GetString() ?? "")).ToList()
            : [("me", source.Account)];

        var events = new List<CalendarEvent>();
        foreach (var (path, name) in calendars)
        {
            var url = $"https://graph.microsoft.com/v1.0/{path}/calendarView" +
                      $"?startDateTime={Uri.EscapeDataString(from.ToUniversalTime().ToString("o"))}" +
                      $"&endDateTime={Uri.EscapeDataString(to.ToUniversalTime().ToString("o"))}" +
                      "&$select=iCalUId,subject,bodyPreview,location,start,end,isAllDay,isCancelled,showAs,responseStatus&$top=200";
            events.AddRange((await GetAll(accessToken, url, token)).Select(e => Map(e, name)).OfType<CalendarEvent>());
        }
        return events;
    }

    private static CalendarEvent? Map(JsonElement e, string calendarName)
    {
        var allDay = e.TryGetProperty("isAllDay", out var a) && a.GetBoolean();
        var start = When(e, "start", allDay);
        if (start is null) return null;
        var end = When(e, "end", allDay) ?? start.Value.AddHours(1);
        string Text(string property, string? inner = null)
        {
            if (!e.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return "";
            if (inner is not null) return value.TryGetProperty(inner, out var nested) ? nested.GetString() ?? "" : "";
            return value.GetString() ?? "";
        }
        return new CalendarEvent(
            // Graph gives every occurrence of a series its own iCalUId, so it alone identifies an occurrence.
            "graph:" + Text("iCalUId"),
            Text("subject"), Text("location", "displayName"), Text("bodyPreview"), start.Value, end, allDay,
            Declined: Text("responseStatus", "response") == "declined",
            Free: Text("showAs") == "free",
            Cancelled: e.TryGetProperty("isCancelled", out var c) && c.GetBoolean(),
            calendarName);
    }

    /// <summary>
    /// Graph returns start/end in UTC (see the Prefer header). All-day events are midnight in the organiser's
    /// zone, so only their date is used.
    /// </summary>
    private static DateTime? When(JsonElement e, string property, bool allDay)
    {
        if (!e.TryGetProperty(property, out var value) || !value.TryGetProperty("dateTime", out var text) ||
            !DateTime.TryParse(text.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
            return null;
        return allDay ? DateTime.SpecifyKind(utc.Date, DateTimeKind.Local) : utc.ToLocalTime();
    }

    /// <summary>Follows Graph's @odata.nextLink paging and returns every item.</summary>
    private static async Task<List<JsonElement>> GetAll(string accessToken, string url, CancellationToken token)
    {
        var items = new List<JsonElement>();
        for (string? next = url; next is not null;)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
            using var response = await Http.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new CalendarException(GraphError(body) ?? $"Microsoft Graph returned HTTP {(int)response.StatusCode}.");
            using var document = JsonDocument.Parse(body);
            items.AddRange(document.RootElement.GetProperty("value").EnumerateArray().Select(item => item.Clone()));
            next = document.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
        return items;
    }

    private static string? GraphError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private static async Task<string> SilentToken(CalendarSource source, CalendarSyncSettings settings, CancellationToken token)
    {
        var app = await CreateApp(source, settings);
        var account = (await app.GetAccountsAsync()).FirstOrDefault(a =>
                          string.Equals(a.Username, source.Account, StringComparison.OrdinalIgnoreCase))
                      ?? throw new CalendarException("Not signed in to Microsoft 365 on this PC. Use \"Sign in again\".");
        try
        {
            return (await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(token)).AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            throw new CalendarException("Microsoft 365 needs you to sign in again (your password or your organisation's policy changed).", ex);
        }
    }

    private static async Task<IPublicClientApplication> CreateApp(CalendarSource source, CalendarSyncSettings settings)
    {
        if (settings.MicrosoftClientId.Trim().Length == 0)
            throw new CalendarException("Enter your Microsoft application (client) ID under \"App registrations\" first.");
        var tenant = settings.MicrosoftTenant.Trim().Length > 0 ? settings.MicrosoftTenant.Trim() : "organizations";
        var app = PublicClientApplicationBuilder.Create(settings.MicrosoftClientId.Trim())
            .WithAuthority($"https://login.microsoftonline.com/{tenant}")
            .WithRedirectUri("http://localhost")
            .Build();
        var storage = new StorageCreationPropertiesBuilder($"msal-{source.Id:N}.bin", TokenFiles.Folder).Build();
        Directory.CreateDirectory(TokenFiles.Folder);
        (await MsalCacheHelper.CreateAsync(storage)).RegisterCache(app.UserTokenCache);
        return app;
    }
}
