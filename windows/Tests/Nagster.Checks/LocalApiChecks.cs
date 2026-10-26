using System.Globalization;
using System.Text.Json;
using Nagster;
using Nagster.Web;

/// <summary>Checks for the local pipe API other apps on this PC use (see LocalApi).</summary>
internal static class LocalApiChecks
{
    public static void Run(Action<bool, string> check)
    {
        SyncChecks.WithEngine(engine => CheckCreateListCancel(check, engine));
        SyncChecks.WithEngine(engine => CheckAnswering(check, engine));
        SyncChecks.WithEngine(engine => CheckBadRequests(check, engine));
    }

    private static JsonElement Send(AlarmEngine engine, string line) =>
        JsonDocument.Parse(LocalApi.HandleLine(new SyncChecks.EngineActions(engine), line, DateTime.Now)).RootElement;

    private static string Local(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    private static void CheckCreateListCancel(Action<bool, string> check, AlarmEngine engine)
    {
        var when = Local(DateTime.Now.AddHours(2));
        var created = Send(engine, $$"""{"command":"create","title":"Call the dentist","when":"{{when}}"}""");
        var upcoming = created.GetProperty("state").GetProperty("upcoming");
        check(created.GetProperty("ok").GetBoolean() && upcoming.GetArrayLength() == 1 &&
              upcoming[0].GetProperty("title").GetString() == "Call the dentist" && !upcoming[0].GetProperty("recurring").GetBoolean(),
            "local API creates a one-off alarm and lists it");

        var id = upcoming[0].GetProperty("id").GetString();
        var cancelled = Send(engine, $$"""{"command":"cancel","id":"{{id}}"}""");
        check(cancelled.GetProperty("ok").GetBoolean() && cancelled.GetProperty("state").GetProperty("upcoming").GetArrayLength() == 0,
            "local API cancels an upcoming one-off alarm");

        Send(engine, $$"""{"command":"create","title":"Stretch","when":"{{when}}","repeat":"Daily"}""");
        var daily = Send(engine, """{"command":"state"}""").GetProperty("state").GetProperty("upcoming")[0].GetProperty("id").GetString();
        var unscoped = Send(engine, $$"""{"command":"cancel","id":"{{daily}}"}""");
        var series = Send(engine, $$"""{"command":"cancel","id":"{{daily}}","scope":"all"}""");
        check(!unscoped.GetProperty("ok").GetBoolean() && series.GetProperty("ok").GetBoolean() &&
              engine.State.Definitions.All(d => d.Title != "Stretch"), "cancelling a repeating alarm needs a scope, and 'all' deletes the series");
    }

    private static void CheckAnswering(Action<bool, string> check, AlarmEngine engine)
    {
        var now = DateTime.Now;
        engine.Add(new AlarmDefinition { Title = "Pills", FirstDue = now.AddMinutes(-1), Repeat = new RepeatRule { Kind = RepeatKind.Once } });
        var ringing = engine.Ready(now).Single();
        var state = Send(engine, """{"command":"state"}""").GetProperty("state");
        check(state.GetProperty("ringing")[0].GetProperty("id").GetString() == ringing.Id.ToString(), "local API lists a ringing alarm");

        var snoozed = Send(engine, $$"""{"command":"snooze","id":"{{ringing.Id}}","duration":"10m"}""");
        check(snoozed.GetProperty("ok").GetBoolean() && ringing.Status == OccurrenceStatus.Snoozed &&
              ringing.AlertAt > now.AddMinutes(9), "local API snoozes a ringing alarm");

        var notRinging = Send(engine, $$"""{"command":"complete","id":"{{ringing.Id}}"}""");
        check(!notRinging.GetProperty("ok").GetBoolean() && notRinging.GetProperty("error").GetString() == WebApi.NotRinging,
            "local API won't complete an alarm that isn't going off");
    }

    private static void CheckBadRequests(Action<bool, string> check, AlarmEngine engine)
    {
        var past = Send(engine, $$"""{"command":"create","title":"Too late","when":"{{Local(DateTime.Now.AddHours(-1))}}"}""");
        var unknown = Send(engine, """{"command":"launch_missiles"}""");
        var garbage = Send(engine, "not json");
        check(!past.GetProperty("ok").GetBoolean() && !unknown.GetProperty("ok").GetBoolean() && !garbage.GetProperty("ok").GetBoolean() &&
              engine.State.Definitions.Count == 0, "local API rejects past times, unknown commands and malformed input");
    }
}
