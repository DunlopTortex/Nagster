using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Nagster.Web;

public sealed record AlarmItem(string Id, string Title, string Description, string Kind, string Status,
    string Due, string AlertDay, string AlertTime, string? Deadline, string LastAction, bool Recurring);
/// <summary>
/// An upcoming occurrence's settings for the web editor. <see cref="When"/> is this occurrence's time and
/// <see cref="SeriesWhen"/> the series' next one, where an edit to the whole series restarts.
/// </summary>
public sealed record AlarmDetails(string Title, string Description, string Kind, bool Recurring, string When, string SeriesWhen,
    string Repeat, int Interval, int? DeadlineMinutes, string Devices);
public sealed record SnoozeOption(string Value, string Label);
public sealed record WebState(AlarmItem[] Ringing, AlarmItem[] Upcoming, SnoozeOption[] SnoozeChoices);
public sealed record LoginRequest(string? Password);
public sealed record SnoozeRequest(string? Duration);
public sealed record RescheduleRequest(string? When);
public sealed record ClipboardRequest(string? Text);
/// <summary>A new alarm from the web form; <see cref="Devices"/> names an <see cref="AlarmDevices"/> value, blank for all.</summary>
public sealed record NewAlarmRequest(string? Title, string? Description, string? When, string? Repeat, int Interval = 1, int? DeadlineMinutes = null,
    string? Devices = null);
/// <summary>An edit to an upcoming occurrence; <see cref="Scope"/> is "one" or "all" for a repeating alarm.</summary>
public sealed record EditAlarmRequest(string? Scope, string? Title, string? Description, string? When, string? Repeat,
    int Interval = 1, int? DeadlineMinutes = null, string? Devices = null)
{
    public NewAlarmRequest Alarm => new(Title, Description, When, Repeat, Interval, DeadlineMinutes, Devices);
}
public sealed record ScopeRequest(string? Scope);

/// <summary>
/// The web interface's endpoints. Alarm state belongs to the WPF UI thread, so every read and change is
/// marshalled through the dispatcher and goes through the same App methods the desktop windows use.
/// </summary>
public static class WebApi
{
    public static readonly TimeSpan UpcomingWindow = TimeSpan.FromDays(7);
    private const string LocalTimeFormat = "yyyy-MM-dd'T'HH:mm";
    public const string JustThisOne = "one";
    public const string WholeSeries = "all";
    private const string ScopeProblem = "Choose whether to change just this one or the whole series.";

    /// <summary>Longest text accepted for the clipboard (about a novel's worth).</summary>
    public const int MaxClipboardLength = 1_000_000;

    public static void Map(WebApplication web, App alarms, WebSessions sessions, ITextClipboard clipboard)
    {
        Task<T> Ui<T>(Func<T> action) => alarms.Dispatcher.InvokeAsync(action).Task;

        web.MapGet("/", (HttpContext context) => Results.Content(Page((string)context.Items["nonce"]!), "text/html; charset=utf-8"));

        // Icons are public: iOS fetches the home-screen icon without the session cookie.
        // "-precomposed" is an older name some iOS versions still request.
        web.MapGet("/apple-touch-icon.png", (HttpContext context) => Icon(context, "apple-touch-icon.png"));
        web.MapGet("/apple-touch-icon-precomposed.png", (HttpContext context) => Icon(context, "apple-touch-icon.png"));
        web.MapGet("/favicon.png", (HttpContext context) => Icon(context, "favicon.png"));

        web.MapPost("/api/login", async (LoginRequest request, HttpContext context) =>
        {
            var address = context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None;
            if (sessions.IsLockedOut(address))
                return Results.Json(new { error = "Too many wrong passwords. Try again in a few minutes." }, statusCode: 429);
            var hash = await Ui(() => alarms.Engine.State.Settings.WebAccess.PasswordHash);
            if (string.IsNullOrEmpty(request.Password) || !WebPassword.Verify(request.Password, hash))
            {
                sessions.RecordFailure(address);
                await Task.Delay(750); // Slows scripted guessing without bothering a person who mistyped.
                return Results.Json(new { error = "That password isn't right." }, statusCode: 401);
            }
            sessions.RecordSuccess(address);
            WebServer.SetSessionCookie(context, sessions.Create(context.Request.Headers.UserAgent.ToString()));
            return Results.NoContent();
        });

        web.MapPost("/api/logout", (HttpContext context) =>
        {
            sessions.End(context.Request.Cookies[WebServer.SessionCookie]);
            context.Response.Cookies.Delete(WebServer.SessionCookie);
            return Results.NoContent();
        });

        web.MapGet("/api/state", () => Ui(() => State(alarms.Engine)));

        // The iPhone app's sync: its actions in, everything changed since its last sync out (see SyncApi).
        web.MapPost("/api/sync", (SyncRequest request) => Ui(() => SyncApi.Handle(alarms, request, DateTime.Now)));

        web.MapPost("/api/occurrences/{id:guid}/complete", (Guid id) =>
            Ui(() => Act(alarms, id, occurrence => { alarms.Complete(occurrence); return null; })));

        web.MapPost("/api/occurrences/{id:guid}/dismiss", (Guid id) =>
            Ui(() => Act(alarms, id, occurrence => { alarms.Dismiss(occurrence); return null; })));

        web.MapPost("/api/occurrences/{id:guid}/snooze", (Guid id, SnoozeRequest request) =>
            Ui(() => Act(alarms, id, occurrence =>
            {
                if (!SnoozeDuration.TryParse(request.Duration, out var duration)) return "Choose how long to snooze for.";
                return alarms.TrySnooze(occurrence, duration.AddTo(DateTime.Now), false, out var message) ? null : message;
            })));

        web.MapPost("/api/occurrences/{id:guid}/reschedule", (Guid id, RescheduleRequest request) =>
            Ui(() => Act(alarms, id, occurrence =>
            {
                if (!TryParseLocal(request.When, out var when)) return "Choose a date and time.";
                return alarms.TryReschedule(occurrence, when, out var message) ? null : message;
            })));

        web.MapGet("/api/clipboard", () => Ui(() =>
        {
            try { return Results.Json(clipboard.Read()); }
            catch (System.Runtime.InteropServices.COMException) { return ClipboardBusy(); }
        }));

        web.MapPost("/api/clipboard", (ClipboardRequest request) => Ui(() =>
        {
            if (request.Text is null) return Results.Json(new { error = "Paste some text first." }, statusCode: 400);
            if (request.Text.Length > MaxClipboardLength)
                return Results.Json(new { error = "That's too much text to send in one go." }, statusCode: 400);
            try { clipboard.Write(request.Text); }
            catch (System.Runtime.InteropServices.COMException) { return ClipboardBusy(); }
            return Results.NoContent();
        }));

        web.MapPost("/api/alarms", (NewAlarmRequest request) => Ui(() =>
        {
            if (Validate(request, out var definition) is { } problem) return Problem(problem);
            alarms.Engine.Add(definition!);
            return Results.NoContent();
        }));

        web.MapGet("/api/occurrences/{id:guid}", (Guid id) =>
            Ui(() => Planned(alarms.Engine, id, occurrence => Results.Ok(Details(alarms.Engine, occurrence, DateTime.Now)))));

        web.MapPost("/api/occurrences/{id:guid}/edit", (Guid id, EditAlarmRequest request) =>
            Ui(() => Planned(alarms.Engine, id, occurrence =>
                Edit(alarms.Engine, occurrence, request) is { } problem ? Problem(problem) : Results.NoContent())));

        // Dismisses one upcoming occurrence, or deletes a repeating alarm outright (as the desktop's Delete does).
        web.MapPost("/api/occurrences/{id:guid}/cancel", (Guid id, ScopeRequest request) =>
            Ui(() => Planned(alarms.Engine, id, occurrence =>
            {
                var definition = alarms.Engine.Definition(occurrence);
                if (!IsRecurring(definition) || request.Scope == JustThisOne) alarms.Dismiss(occurrence);
                else if (request.Scope == WholeSeries) alarms.DeleteDefinition(definition.Id);
                else return Problem(ScopeProblem);
                return Results.NoContent();
            })));
    }

    private static IResult ClipboardBusy() =>
        Results.Json(new { error = "Another program is using the PC's clipboard. Try again in a moment." }, statusCode: 503);

    public static WebState State(AlarmEngine engine)
    {
        var now = DateTime.Now;
        AlarmItem[] Items(IEnumerable<AlarmOccurrence> occurrences) =>
            occurrences.Select(o => Item(o, engine.Definition(o))).ToArray();
        var choices = (SnoozeDuration.TryParseList(engine.State.Settings.SnoozeChoices) ?? [])
            .Select(d => new SnoozeOption(d.ToString(), d.Label)).ToArray();
        return new WebState(Items(engine.Ringing(now)), Items(engine.Upcoming(now, UpcomingWindow)), choices);
    }

    private static AlarmItem Item(AlarmOccurrence occurrence, AlarmDefinition definition) => new(
        occurrence.Id.ToString(), definition.Title, definition.Description, definition.IsEvent ? "Event" : "Alarm",
        occurrence.Status.ToString(), Friendly(occurrence.ScheduledFor), Day(occurrence.AlertAt), Time(occurrence.AlertAt),
        occurrence.Deadline is DateTime deadline ? Friendly(deadline) : null, occurrence.LastAction, IsRecurring(definition));

    private static bool IsRecurring(AlarmDefinition definition) => definition.Repeat.Kind != RepeatKind.Once;

    /// <summary>Pending or snoozed and not due yet: the occurrences the web page lets you edit or dismiss in advance.</summary>
    private static bool IsPlanned(AlarmOccurrence occurrence, DateTime now) =>
        occurrence.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed && occurrence.AlertAt > now;

    public static AlarmDetails Details(AlarmEngine engine, AlarmOccurrence occurrence, DateTime now)
    {
        var definition = engine.Definition(occurrence);
        var next = engine.State.Occurrences.Where(o => o.DefinitionId == definition.Id && IsPlanned(o, now))
            .MinBy(o => o.ScheduledFor) ?? occurrence;
        // The web editor only has relative deadlines; a fixed one from the desktop shows as its distance from the alert.
        var deadline = definition.DeadlineAfterMinutes ?? (occurrence.Deadline is DateTime at
            ? Math.Max(1, (int)Math.Ceiling((at - occurrence.ScheduledFor).TotalMinutes)) : null);
        return new AlarmDetails(definition.Title, definition.Description, definition.IsEvent ? "Event" : "Alarm", IsRecurring(definition),
            LocalValue(occurrence.ScheduledFor), LocalValue(next.ScheduledFor), definition.Repeat.Kind.ToString(),
            definition.Repeat.Interval, deadline, definition.Devices.ToString());
    }

    /// <summary>
    /// Applies the web editor's changes. For one occurrence of a repeating alarm, that occurrence becomes a separate
    /// one-off alarm; otherwise the alarm itself changes and its schedule restarts from the edited time. Settings
    /// the web editor doesn't show (sound, event length, weekdays and so on) are kept.
    /// </summary>
    /// <returns>A message describing the problem, or null once saved.</returns>
    public static string? Edit(AlarmEngine engine, AlarmOccurrence occurrence, EditAlarmRequest request)
    {
        var definition = engine.Definition(occurrence);
        var recurring = IsRecurring(definition);
        if (recurring && request.Scope is not (JustThisOne or WholeSeries)) return ScopeProblem;
        var justThis = recurring && request.Scope == JustThisOne;
        var alarm = justThis ? request.Alarm with { Repeat = nameof(RepeatKind.Once), Interval = 1 } : request.Alarm;
        if (Validate(alarm, out var edited) is { } problem) return problem;

        var changes = definition.Copy();
        changes.Title = edited!.Title;
        changes.Description = edited.Description;
        changes.DeadlineAfterMinutes = edited.DeadlineAfterMinutes;
        changes.AbsoluteDeadline = null;
        // A page loaded before device choices existed doesn't send one; keep the alarm's.
        if (request.Devices is not null) changes.Devices = edited.Devices;
        // An event keeps its length from the new start.
        if (changes.EventEnd is DateTime end) changes.EventEnd = edited.FirstDue + (end - definition.FirstDue);
        changes.FirstDue = edited.FirstDue;
        if (justThis)
        {
            changes.Id = Guid.NewGuid();
            changes.Repeat = edited.Repeat;
            changes.PushoverNotifiedOn = null;
            engine.Detach(occurrence, changes);
            return null;
        }
        // Kind-specific options (weekdays, day of month) only carry over while the kind stays the same.
        if (changes.Repeat.Kind == edited.Repeat.Kind) changes.Repeat.Interval = edited.Repeat.Interval;
        else changes.Repeat = new RepeatRule { Kind = edited.Repeat.Kind, Interval = edited.Repeat.Interval,
            Until = definition.Repeat.Until, Count = definition.Repeat.Count };
        engine.Update(definition.Id, changes);
        return null;
    }

    /// <summary>"Today 3:30 PM", "Tomorrow 9:00 AM" or "Wed 22 Oct 9:00 AM".</summary>
    private static string Friendly(DateTime value) => $"{Day(value)} {Time(value)}";

    private static string Time(DateTime value) => value.ToString("t", CultureInfo.CurrentCulture);

    private static string Day(DateTime value) =>
        (value.Date - DateTime.Today).Days switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            _ => value.ToString("ddd d MMM", CultureInfo.CurrentCulture)
        };

    /// <summary>Applies an action to an alarm that is currently going off; returns 400 with a reason if it can't.</summary>
    private static IResult Act(App alarms, Guid id, Func<AlarmOccurrence, string?> action)
    {
        var occurrence = alarms.Engine.Ringing(DateTime.Now).FirstOrDefault(o => o.Id == id);
        if (occurrence is null) return Results.Json(new { error = "That alarm isn't going off any more." }, statusCode: 409);
        return action(occurrence) is { } problem ? Problem(problem) : Results.NoContent();
    }

    /// <summary>Applies an action to an alarm that hasn't gone off yet; returns 409 if it has, or no longer exists.</summary>
    private static IResult Planned(AlarmEngine engine, Guid id, Func<AlarmOccurrence, IResult> action)
    {
        var occurrence = engine.State.Occurrences.FirstOrDefault(o => o.Id == id && IsPlanned(o, DateTime.Now));
        return occurrence is null
            ? Results.Json(new { error = "That alarm has already gone off or been changed." }, statusCode: 409)
            : action(occurrence);
    }

    private static IResult Problem(string message) => Results.Json(new { error = message }, statusCode: 400);

    /// <summary>Checks a new alarm from the web form using the same rules as the desktop editor.</summary>
    /// <returns>A message describing the problem, or null with <paramref name="definition"/> set.</returns>
    public static string? Validate(NewAlarmRequest request, out AlarmDefinition? definition)
    {
        definition = null;
        var title = request.Title?.Trim() ?? "";
        if (title.Length is 0 or > 160) return "Enter a title (up to 160 characters).";
        var description = request.Description?.Trim() ?? "";
        if (description.Length > 1000) return "Keep the description under 1000 characters.";
        if (!TryParseLocal(request.When, out var due) || due <= DateTime.Now) return "Choose a date and time in the future.";
        if (!Enum.TryParse<RepeatKind>(request.Repeat, ignoreCase: true, out var kind) || !Enum.IsDefined(kind)) return "Choose how often it repeats.";
        if (request.Interval is < 1 or > 999) return "Repeat interval must be between 1 and 999.";
        if (request.DeadlineMinutes is < 1 or > 10080) return "Deadline must be between 1 and 10080 minutes after each alert.";
        var devices = AlarmDevices.All;
        if (!string.IsNullOrEmpty(request.Devices) && (!Enum.TryParse(request.Devices, out devices) || !Enum.IsDefined(devices)))
            return "Choose which devices it rings on.";
        definition = new AlarmDefinition
        {
            Title = title, Description = description, FirstDue = due,
            Repeat = new RepeatRule { Kind = kind, Interval = request.Interval },
            DeadlineAfterMinutes = request.DeadlineMinutes, Devices = devices
        };
        return null;
    }

    /// <summary>Reads an HTML datetime-local value ("2026-10-16T14:30") as this PC's local time.</summary>
    private static bool TryParseLocal(string? text, out DateTime value) =>
        DateTime.TryParseExact(text, [LocalTimeFormat, LocalTimeFormat + ":ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static string LocalValue(DateTime value) => value.ToString(LocalTimeFormat, CultureInfo.InvariantCulture);

    private static string Page(string nonce) => System.Text.Encoding.UTF8.GetString(Resource("index.html")).Replace("{{nonce}}", nonce);

    private static IResult Icon(HttpContext context, string name)
    {
        context.Response.Headers.CacheControl = "public, max-age=86400";
        return Results.Bytes(Resource(name), "image/png");
    }

    private static byte[] Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Nagster.Web." + name)
            ?? throw new InvalidOperationException($"Missing embedded web resource '{name}'.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
