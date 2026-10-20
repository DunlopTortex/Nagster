using System.IO;
using System.Text.Json;
using Nagster;
using Nagster.Web;

/// <summary>Checks for device targeting and syncing with the iPhone app.</summary>
internal static class SyncChecks
{
    /// <summary>The engine on its own, standing in for the App (which also closes popups and stops sounds).</summary>
    private sealed class EngineActions(AlarmEngine engine) : IAlarmActions
    {
        public AlarmEngine Engine => engine;
        public void Complete(AlarmOccurrence occurrence) => engine.Complete(occurrence);
        public void Dismiss(AlarmOccurrence occurrence) => engine.Dismiss(occurrence);
        public bool TrySnooze(AlarmOccurrence occurrence, DateTime next, bool automatic, out string message, bool replay = false) =>
            engine.TrySnooze(occurrence, next, automatic, out message, replay);
        public bool TryReschedule(AlarmOccurrence occurrence, DateTime next, out string message, bool replay = false) =>
            engine.TryReschedule(occurrence, next, out message, replay);
        public void DeleteDefinition(Guid definitionId) => engine.Delete(definitionId);
    }

    public static void Run(Action<bool, string> check)
    {
        CheckParityCases(check);
        WithEngine(engine => CheckDeviceTargeting(check, engine));
        WithEngine(engine => CheckRevisionsAndTombstones(check, engine));
        WithEngine(engine => CheckAnswersFromThePhone(check, engine));
        WithEngine(engine => CheckAlarmEditsFromThePhone(check, engine));
        CheckRekeyingOldOccurrences(check);
        CheckWireFormat(check);
    }

    private static void WithEngine(Action<AlarmEngine> body)
    {
        var path = Path.Combine(Path.GetTempPath(), "Nagster-sync-" + Guid.NewGuid() + ".db");
        try { body(new AlarmEngine(new AlarmStore(path))); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static SyncResponse Sync(AlarmEngine engine, long since, params SyncOp[] ops) =>
        SyncApi.Handle(new EngineActions(engine), new SyncRequest("test-phone", since, ops), DateTime.Now);

    /// <summary>The cases in shared/parity-cases.json, which the iPhone app's tests check too.</summary>
    private static void CheckParityCases(Action<bool, string> check)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "shared", "parity-cases.json"))) folder = folder.Parent;
        check(folder is not null, "shared parity cases found");
        if (folder is null) return;
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.FullName, "shared", "parity-cases.json")));
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var failures = new List<string>();
        foreach (var item in cases.RootElement.GetProperty("recurrence").EnumerateArray())
        {
            var repeat = item.GetProperty("rule").Deserialize<SyncRepeat>(web)!;
            var wire = new SyncDefinition(Guid.NewGuid(), "x", "", false, null, 0, item.GetProperty("anchor").GetString()!, repeat,
                null, null, "All", false, true, null, 0);
            SyncApi.ToDefinition(wire, null, out var definition);
            SyncTime.TryParse(item.GetProperty("anchor").GetString(), out var anchor);
            SyncTime.TryParse(item.GetProperty("after").GetString(), out var after);
            var actual = SyncTime.Format(definition!.Repeat.Next(anchor, after));
            if (actual != item.GetProperty("expected").GetString()) failures.Add($"{item.GetProperty("name")}: got {actual ?? "none"}");
        }
        foreach (var item in cases.RootElement.GetProperty("occurrenceIds").EnumerateArray())
        {
            SyncTime.TryParse(item.GetProperty("scheduledFor").GetString(), out var scheduledFor);
            var id = AlarmEngine.OccurrenceId(item.GetProperty("definitionId").GetGuid(), scheduledFor);
            if (id != item.GetProperty("expected").GetGuid()) failures.Add($"occurrence ID {id}");
        }
        check(failures.Count == 0, $"repeat times and occurrence IDs match the shared cases {string.Join("; ", failures)}");
    }

    private static void CheckDeviceTargeting(Action<bool, string> check, AlarmEngine engine)
    {
        var now = DateTime.Now;
        var phoneOnly = new AlarmDefinition { Title = "Phone only", FirstDue = now.AddMinutes(-1), Devices = AlarmDevices.Phone };
        var everywhere = new AlarmDefinition { Title = "Everywhere", FirstDue = now.AddMinutes(-1) };
        var repeating = new AlarmDefinition
        {
            Title = "Phone repeats", FirstDue = now.AddMinutes(-30), Devices = AlarmDevices.Phone,
            Repeat = new RepeatRule { Kind = RepeatKind.Minutes, Interval = 10 }
        };
        engine.Add(phoneOnly);
        engine.Add(everywhere);
        engine.Add(repeating);
        var ready = engine.Ready(now);
        check(ready.Count == 1 && engine.Definition(ready[0]) == everywhere &&
              engine.Ringing(now).All(o => engine.Definition(o) == everywhere),
            "phone-only alarms don't ring on the PC");
        check(engine.State.Occurrences.Count(o => o.DefinitionId == repeating.Id && o.AlertAt <= now) > 1 &&
              engine.State.Occurrences.All(o => o.Status != OccurrenceStatus.Dismissed),
            "the PC leaves the phone to decide which phone-only repeats were missed");
        var when = now.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        check(WebApi.Validate(new NewAlarmRequest("Web", "", when, "Once", Devices: "Phone"), out var fromWeb) is null &&
              fromWeb!.Devices == AlarmDevices.Phone &&
              WebApi.Validate(new NewAlarmRequest("Web", "", when, "Once", Devices: "Tablet"), out _) is not null,
            "the web form chooses which devices ring");
        var webAlarm = fromWeb!;
        engine.Add(webAlarm);
        WebApi.Edit(engine, engine.NextPending(webAlarm.Id)!, new EditAlarmRequest(null, "Web edited", "", when, "Once"));
        check(webAlarm.Title == "Web edited" && webAlarm.Devices == AlarmDevices.Phone,
            "an edit from a web page without the device choice keeps the alarm's devices");

        for (var tick = 0; tick < 10; tick++) engine.Ready(now); // The app calls this every second; each call plans up to 100.
        var planned = engine.State.Occurrences.Where(o => o.DefinitionId == repeating.Id && o.Status == OccurrenceStatus.Pending).ToList();
        check(planned.Max(o => o.AlertAt) > now.AddHours(36) && planned.Count == 300,
            "frequent repeats are planned well ahead for the phone, up to a cap");
    }

    private static void CheckRevisionsAndTombstones(Action<bool, string> check, AlarmEngine engine)
    {
        var now = DateTime.Now;
        var alarm = new AlarmDefinition { Title = "Bins", FirstDue = now.AddHours(2), Repeat = new RepeatRule { Kind = RepeatKind.Weekly } };
        engine.Add(alarm);
        var first = engine.NextPending(alarm.Id)!;
        check(first.Id == AlarmEngine.OccurrenceId(alarm.Id, alarm.FirstDue) && alarm.Revision > 0 && first.Revision > 0,
            "new occurrences get derived IDs and revisions");

        var baseline = Sync(engine, 0);
        check(baseline.Full && baseline.Definitions.Length == 1 && baseline.Occurrences.Length == 1 && baseline.Revision == engine.State.Revision,
            "a first sync sends everything");

        engine.TrySnooze(first, now.AddHours(3), false, out _);
        var edited = alarm.Copy();
        edited.Title = "Put the bins out";
        engine.Update(alarm.Id, edited);
        check(engine.NextPending(alarm.Id) is { Status: OccurrenceStatus.Snoozed } kept && kept.Id == first.Id && alarm.Title == "Put the bins out",
            "changing only an alarm's title keeps its snooze");

        var delta = Sync(engine, baseline.Revision);
        check(!delta.Full && delta.Definitions.Single().Title == "Put the bins out" && delta.Occurrences.Single().Status == "Snoozed",
            "a later sync sends only what changed");

        var moved = alarm.Copy();
        moved.FirstDue = now.AddHours(5);
        engine.Update(alarm.Id, moved);
        var afterMove = Sync(engine, delta.Revision);
        check(afterMove.Tombstones.Any(t => t.Id == first.Id && t.Kind == Tombstone.OccurrenceKind) &&
              afterMove.Occurrences.Any(o => o.Id == AlarmEngine.OccurrenceId(alarm.Id, moved.FirstDue)),
            "rescheduling an alarm tells the phone which occurrences went");

        engine.Delete(alarm.Id);
        var afterDelete = Sync(engine, afterMove.Revision);
        check(afterDelete.Tombstones.Any(t => t.Id == alarm.Id && t.Kind == Tombstone.AlarmKind), "deleting an alarm tells the phone");

        engine.State.Deleted.ForEach(t => t.DeletedAt = now.AddDays(-100));
        engine.PruneSyncHistory(now);
        check(engine.State.Deleted.Count == 0 && Sync(engine, afterMove.Revision).Full,
            "a phone that missed pruned deletions gets everything again");
    }

    private static void CheckAnswersFromThePhone(Action<bool, string> check, AlarmEngine engine)
    {
        var now = DateTime.Now;
        var alarm = new AlarmDefinition { Title = "Pills", FirstDue = now.AddMinutes(-2), Repeat = new RepeatRule { Kind = RepeatKind.Daily } };
        engine.Add(alarm);
        var ringing = engine.Ready(now).Single();
        engine.Activate(ringing); // A popup is showing on the PC.
        var seen = Sync(engine, 0);
        check(seen.Occurrences.Single(o => o.Id == ringing.Id).Status == "Pending", "an alarm on the PC's screen reaches the phone as waiting");

        var complete = new SyncOp(Guid.NewGuid(), SyncOps.Complete, seen.Occurrences.Single(o => o.Id == ringing.Id).Revision, ringing.Id);
        var done = Sync(engine, seen.Revision, complete);
        check(done.Results.Single().Applied && ringing.Status == OccurrenceStatus.Completed, "completing on the phone completes on the PC");
        check(Sync(engine, done.Revision, complete).Results.Single().Applied && engine.State.AppliedOps.Count == 1,
            "a resent action is only applied once");

        var snoozeLate = new SyncOp(Guid.NewGuid(), SyncOps.Snooze, 0, ringing.Id, Until: SyncTime.Format(now.AddHours(1)));
        check(!Sync(engine, done.Revision, snoozeLate).Results.Single().Applied && ringing.Status == OccurrenceStatus.Completed,
            "a finished alarm never reopens");

        // Far beyond what the PC has planned: the phone worked this one out for itself.
        var future = alarm.FirstDue.AddDays(200);
        var projectedId = AlarmEngine.OccurrenceId(alarm.Id, future);
        var projected = new SyncOp(Guid.NewGuid(), SyncOps.Dismiss, 0, projectedId, alarm.Id, SyncTime.Format(future));
        check(Sync(engine, 0, projected).Results.Single().Applied &&
              engine.State.Occurrences.Single(o => o.Id == projectedId).Status == OccurrenceStatus.Dismissed,
            "an occurrence the phone worked out ahead of the PC can be answered");
        var forged = projected with { OpId = Guid.NewGuid(), OccurrenceId = Guid.NewGuid() };
        check(!Sync(engine, 0, forged).Results.Single().Applied, "an occurrence ID that doesn't match its alarm is refused");

        var next = engine.State.Occurrences.Where(o => o.DefinitionId == alarm.Id && o.Status == OccurrenceStatus.Pending).MinBy(o => o.ScheduledFor)!;
        var phoneSaw = next.Revision;
        engine.Dismiss(next); // Someone dismissed it on the PC after the phone last synced.
        var staleComplete = new SyncOp(Guid.NewGuid(), SyncOps.Complete, phoneSaw, next.Id);
        check(Sync(engine, 0, staleComplete).Results.Single().Applied && next.Status == OccurrenceStatus.Completed,
            "completing beats dismissing, whichever came first");

        var another = engine.State.Occurrences.Where(o => o.DefinitionId == alarm.Id && o.Status == OccurrenceStatus.Pending).MinBy(o => o.ScheduledFor)!;
        var phoneBase = another.Revision;
        engine.TrySnooze(another, another.AlertAt.AddHours(2), automatic: false, out _);
        var staleSnooze = new SyncOp(Guid.NewGuid(), SyncOps.Snooze, phoneBase, another.Id, Until: SyncTime.Format(now.AddMinutes(30)));
        check(!Sync(engine, 0, staleSnooze).Results.Single().Applied && another.AlertAt > now.AddHours(1),
            "a snooze chosen on the PC since the phone last synced wins");
        engine.TrySnooze(another, another.AlertAt.AddHours(1), automatic: true, out _);
        var afterAuto = new SyncOp(Guid.NewGuid(), SyncOps.Snooze, phoneBase, another.Id, Until: SyncTime.Format(now.AddMinutes(-5)));
        check(Sync(engine, 0, afterAuto).Results.Single().Applied && another.AlertAt < now,
            "an automatic snooze doesn't block the phone, and a snooze made while offline can be in the past");
    }

    private static void CheckAlarmEditsFromThePhone(Action<bool, string> check, AlarmEngine engine)
    {
        var now = DateTime.Today.AddDays(1).AddHours(8);
        var created = new AlarmDefinition
        {
            Title = "Phone alarm", FirstDue = now, Devices = AlarmDevices.Phone, Important = true,
            Repeat = new RepeatRule { Kind = RepeatKind.Weekly, Weekdays = 62 }
        };
        var upsert = new SyncOp(Guid.NewGuid(), SyncOps.UpsertAlarm, 0, Definition: SyncApi.ToWire(created));
        var added = Sync(engine, 0, upsert);
        var stored = engine.State.Definitions.SingleOrDefault(d => d.Id == created.Id);
        check(added.Results.Single().Applied && stored is { Devices: AlarmDevices.Phone, Important: true, Repeat.Weekdays: 62 },
            "an alarm added on the phone is added on the PC");

        stored!.Sound = "builtin:chime";
        var phoneCopy = SyncApi.ToWire(stored) with { Title = "Renamed on phone" };
        var rename = new SyncOp(Guid.NewGuid(), SyncOps.UpsertAlarm, stored.Revision, Definition: phoneCopy);
        check(Sync(engine, 0, rename).Results.Single().Applied && stored.Title == "Renamed on phone" && stored.Sound == "builtin:chime",
            "editing on the phone keeps settings only the PC has");

        var stale = new SyncOp(Guid.NewGuid(), SyncOps.UpsertAlarm, 0, Definition: phoneCopy with { Title = "Stale" });
        check(!Sync(engine, 0, stale).Results.Single().Applied && stored.Title == "Renamed on phone", "an edit made on the PC since wins");

        var invalid = new SyncOp(Guid.NewGuid(), SyncOps.UpsertAlarm, stored.Revision, Definition: phoneCopy with { Title = " " });
        check(!Sync(engine, 0, invalid).Results.Single().Applied, "an invalid alarm from the phone is refused");

        var delete = new SyncOp(Guid.NewGuid(), SyncOps.DeleteAlarm, stored.Revision, DefinitionId: stored.Id);
        var gone = Sync(engine, 0, delete);
        check(gone.Results.Single().Applied && engine.State.Definitions.All(d => d.Id != created.Id), "deleting on the phone deletes on the PC");
        check(!Sync(engine, 0, upsert with { OpId = Guid.NewGuid() }).Results.Single().Applied, "a deleted alarm isn't brought back by an old edit");
    }

    private static void CheckRekeyingOldOccurrences(Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "Nagster-rekey-" + Guid.NewGuid() + ".db");
        try
        {
            var engine = new AlarmEngine(new AlarmStore(path));
            var alarm = new AlarmDefinition { Title = "Old", FirstDue = DateTime.Now.AddDays(1) };
            engine.Add(alarm);
            var occurrence = engine.State.Occurrences.Single();
            occurrence.Id = Guid.NewGuid(); // As saved by a version before syncing existed.
            engine.SaveSettings();
            var reloaded = new AlarmEngine(new AlarmStore(path));
            check(reloaded.State.Occurrences.Single().Id == AlarmEngine.OccurrenceId(alarm.Id, alarm.FirstDue),
                "upcoming occurrences from older versions get derived IDs");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>The iPhone app decodes camelCase names and wall-clock times; this pins the JSON it receives.</summary>
    private static void CheckWireFormat(Action<bool, string> check)
    {
        var occurrence = new AlarmOccurrence
        {
            Id = Guid.Empty, DefinitionId = Guid.Empty, ScheduledFor = new DateTime(2026, 10, 20, 9, 0, 0, 500),
            AlertAt = new DateTime(2026, 10, 20, 9, 0, 0), Status = OccurrenceStatus.Active
        };
        var json = JsonSerializer.Serialize(SyncApi.ToWire(occurrence), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        check(json.Contains("\"scheduledFor\":\"2026-10-20T09:00:00\"") && json.Contains("\"status\":\"Pending\"") &&
              json.Contains("\"definitionId\":"), $"sync JSON uses camelCase names and wall-clock times ({json})");
    }
}
