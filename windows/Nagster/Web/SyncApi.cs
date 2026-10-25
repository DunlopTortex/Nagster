namespace Nagster.Web;

/// <summary>
/// Syncs the iPhone app with this PC, which holds the master copy. The phone sends what the user did there
/// (answered, snoozed, added or edited an alarm) and gets back everything that changed since its last sync.
/// <para>
/// Conflicts are settled by revision and by how final an answer is, never by comparing the two devices' clocks:
/// Completed beats Dismissed, which beats Missed, which beats anything still waiting, and a finished occurrence
/// never reopens. Otherwise a phone action wins unless someone acted on the PC since the phone last saw the
/// record; changes the app made by itself (showing a popup, an automatic snooze) don't count.
/// </para>
/// </summary>
public static class SyncApi
{
    /// <summary>How far back finished occurrences are sent in a full sync; older history stays on the PC.</summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(30);

    public static SyncResponse Handle(IAlarmActions alarms, SyncRequest request, DateTime now)
    {
        var results = Apply(alarms, request.Ops ?? [], now);
        return Changes(alarms.Engine, request.SinceRevision, now) with { Results = [.. results] };
    }

    /// <summary>Applies actions from another device with these rules, as one save; cloud sync uses it too.</summary>
    public static List<SyncOpResult> Apply(IAlarmActions alarms, IEnumerable<SyncOp> ops, DateTime now)
    {
        var engine = alarms.Engine;
        var results = new List<SyncOpResult>();
        using (engine.Batch())
        {
            foreach (var op in ops)
                results.Add(Apply(alarms, op, now));
        }
        engine.PruneSyncHistory(now);
        return results;
    }

    private static SyncOpResult Apply(IAlarmActions alarms, SyncOp op, DateTime now)
    {
        var engine = alarms.Engine;
        // A resent action (the phone missed the reply) has already taken effect.
        if (engine.State.AppliedOps.Any(a => a.Id == op.OpId)) return new SyncOpResult(op.OpId, true, null);
        var problem = op.Type switch
        {
            SyncOps.UpsertAlarm => Upsert(engine, op),
            SyncOps.DeleteAlarm => DeleteAlarm(alarms, op),
            SyncOps.Complete or SyncOps.Dismiss or SyncOps.Snooze or SyncOps.Reschedule => Answer(alarms, op),
            _ => "The PC doesn't recognise that action; update Nagster on the PC."
        };
        if (problem is not null) return new SyncOpResult(op.OpId, false, problem);
        engine.State.AppliedOps.Add(new AppliedOp { Id = op.OpId, AppliedAt = now });
        return new SyncOpResult(op.OpId, true, null);
    }

    private static string? Answer(IAlarmActions alarms, SyncOp op)
    {
        var engine = alarms.Engine;
        if (op.OccurrenceId is not Guid id) return "No alarm was given.";
        var occurrence = engine.State.Occurrences.FirstOrDefault(o => o.Id == id);
        if (occurrence is null)
        {
            // The phone worked this occurrence out ahead of the PC (or the PC hasn't generated it yet).
            var definition = engine.State.Definitions.FirstOrDefault(d => d.Id == op.DefinitionId);
            if (definition is null) return "That alarm has been deleted.";
            if (engine.State.Deleted.Any(t => t.Id == id)) return "That alarm's time was changed on the PC.";
            if (!SyncTime.TryParse(op.ScheduledFor, out var scheduledFor)) return "That alarm time isn't valid.";
            occurrence = engine.AdoptProjected(id, definition, scheduledFor);
            if (occurrence is null) return "That time isn't part of the alarm's schedule.";
        }

        if (occurrence.Status == OccurrenceStatus.Completed) return "It was already completed.";
        if (occurrence.Status == OccurrenceStatus.Dismissed)
        {
            var missed = occurrence.LastAction == AlarmEngine.MissedAction;
            if (!(op.Type == SyncOps.Complete || (op.Type == SyncOps.Dismiss && missed))) return "It was already dismissed.";
        }
        else if (occurrence.Revision > op.BaseRevision && !occurrence.LastChangeAutomatic)
        {
            return "It was answered on the PC first.";
        }

        switch (op.Type)
        {
            case SyncOps.Complete:
                alarms.Complete(occurrence);
                return null;
            case SyncOps.Dismiss:
                alarms.Dismiss(occurrence);
                return null;
        }
        if (!SyncTime.TryParse(op.Until, out var until)) return "No snooze time was given.";
        string message;
        var accepted = op.Type == SyncOps.Snooze
            ? alarms.TrySnooze(occurrence, until, automatic: false, out message, replay: true)
            : alarms.TryReschedule(occurrence, until, out message, replay: true);
        return accepted ? null : message;
    }

    private static string? Upsert(AlarmEngine engine, SyncOp op)
    {
        if (op.Definition is not { } incoming) return "No alarm was given.";
        var existing = engine.State.Definitions.FirstOrDefault(d => d.Id == incoming.Id);
        if (existing is null && engine.State.Deleted.Any(t => t.Id == incoming.Id)) return "That alarm was deleted on the PC.";
        if (existing is not null && existing.Revision > op.BaseRevision) return "That alarm was changed on the PC first.";
        if (ToDefinition(incoming, existing, out var definition) is { } problem) return problem;
        if (existing is null) engine.Add(definition!);
        else engine.Update(existing.Id, definition!);
        return null;
    }

    private static string? DeleteAlarm(IAlarmActions alarms, SyncOp op)
    {
        var existing = alarms.Engine.State.Definitions.FirstOrDefault(d => d.Id == op.DefinitionId);
        if (existing is null) return null; // Already gone, which is what the phone wanted.
        if (existing.Revision > op.BaseRevision) return "That alarm was changed on the PC first, so it was kept.";
        alarms.DeleteDefinition(existing.Id);
        return null;
    }

    /// <summary>
    /// Builds an alarm from the phone's copy. Settings the phone doesn't edit (sound, response time, calendar source)
    /// are kept from the PC's copy, so editing on the phone never clears them. The alarm's own automatic snooze
    /// length is edited on both and comes from the phone's copy.
    /// </summary>
    /// <returns>A message describing the problem, or null with <paramref name="definition"/> set.</returns>
    public static string? ToDefinition(SyncDefinition incoming, AlarmDefinition? existing, out AlarmDefinition? definition)
    {
        definition = null;
        var title = incoming.Title.Trim();
        if (title.Length is 0 or > 160) return "Enter a title (up to 160 characters).";
        if (incoming.Description.Length > 1000) return "Keep the description under 1000 characters.";
        if (!SyncTime.TryParse(incoming.FirstDue, out var firstDue)) return "The alarm time isn't valid.";
        if (!SyncTime.TryParseOptional(incoming.EventEnd, out var eventEnd) ||
            !SyncTime.TryParseOptional(incoming.AbsoluteDeadline, out var absoluteDeadline) ||
            !SyncTime.TryParseOptional(incoming.Repeat.Until, out var until)) return "One of the alarm's times isn't valid.";
        if (!Enum.TryParse<AlarmDevices>(incoming.Devices, out var devices) || !Enum.IsDefined(devices)) return "Choose which devices it rings on.";
        if (!Enum.TryParse<RepeatKind>(incoming.Repeat.Kind, out var kind) || !Enum.IsDefined(kind)) return "Choose how often it repeats.";
        var repeat = incoming.Repeat;
        if (repeat.Interval is < 1 or > 999) return "Repeat interval must be between 1 and 999.";
        if (repeat.Weekdays is < 0 or > 127 || repeat.DayOfMonth is < 0 or > 31 || repeat.Nth is < -1 or > 4 ||
            repeat.NthWeekday is < 0 or > 6 || repeat.Count is < 1) return "The repeat settings aren't valid.";
        if (incoming.ReminderMinutes is < 0 or > 10080) return "Reminder must be between 0 and 10080 minutes before.";
        if (incoming.DeadlineAfterMinutes is < 1 or > 10080) return "Deadline must be between 1 and 10080 minutes after each alert.";
        SnoozeDuration? nagAfter = null;
        if (!string.IsNullOrEmpty(incoming.AutoSnoozeOverride))
        {
            if (!SnoozeDuration.TryParse(incoming.AutoSnoozeOverride, out var parsed)) return "The nag length isn't valid.";
            nagAfter = parsed;
        }

        definition = existing?.Copy() ?? new AlarmDefinition { Id = incoming.Id };
        definition.Title = title;
        definition.Description = incoming.Description.Trim();
        definition.IsEvent = incoming.IsEvent;
        definition.EventEnd = eventEnd;
        definition.ReminderMinutes = incoming.ReminderMinutes;
        definition.FirstDue = firstDue;
        definition.Repeat = new RepeatRule
        {
            Kind = kind, Interval = repeat.Interval, Weekdays = repeat.Weekdays, DayOfMonth = repeat.DayOfMonth, Nth = repeat.Nth,
            NthWeekday = (DayOfWeek)repeat.NthWeekday, UseLastDayForShortMonths = repeat.UseLastDayForShortMonths, Until = until,
            Count = repeat.Count
        };
        definition.AbsoluteDeadline = absoluteDeadline;
        definition.DeadlineAfterMinutes = incoming.DeadlineAfterMinutes;
        definition.Devices = devices;
        definition.Important = incoming.Important;
        definition.AutoSnoozeOverride = nagAfter?.ToString();
        return null;
    }

    /// <summary>
    /// Everything changed since <paramref name="since"/>, or everything (with recent history) when the phone has
    /// never synced, is behind the pruned tombstones, or is ahead of this PC (e.g. its data was restored).
    /// </summary>
    public static SyncResponse Changes(AlarmEngine engine, long since, DateTime now)
    {
        var state = engine.State;
        var full = since <= 0 || since < state.PrunedThrough || since > state.Revision;
        var oldest = now - HistoryWindow;
        var definitions = state.Definitions.Where(d => full || d.Revision > since).Select(ToWire).ToArray();
        var occurrences = state.Occurrences
            .Where(o => full ? !o.IsFinished || o.ScheduledFor >= oldest : o.Revision > since)
            .Select(ToWire).ToArray();
        SyncTombstone[] tombstones = full ? [] : [.. state.Deleted.Where(t => t.Revision > since).Select(t => new SyncTombstone(t.Id, t.Kind, t.Revision))];
        return new SyncResponse(state.Revision, full, definitions, occurrences, tombstones, Settings(state.Settings), []);
    }

    public static SyncSettings Settings(AppSettings settings) => new(
        [.. (SnoozeDuration.TryParseList(settings.SnoozeChoices) ?? []).Select(d => d.ToString())],
        settings.AutoSnooze, settings.ResponseSeconds);

    public static SyncDefinition ToWire(AlarmDefinition d) => new(
        d.Id, d.Title, d.Description, d.IsEvent, SyncTime.Format(d.EventEnd), d.ReminderMinutes, SyncTime.Format(d.FirstDue),
        new SyncRepeat(d.Repeat.Kind.ToString(), d.Repeat.Interval, d.Repeat.Weekdays, d.Repeat.DayOfMonth, d.Repeat.Nth,
            (int)d.Repeat.NthWeekday, d.Repeat.UseLastDayForShortMonths, SyncTime.Format(d.Repeat.Until), d.Repeat.Count),
        SyncTime.Format(d.AbsoluteDeadline), d.DeadlineAfterMinutes, d.Devices.ToString(), d.Important, d.Enabled, d.ImportedFrom,
        d.Revision, d.AutoSnoozeOverride);

    /// <summary>Active only means "on this PC's screen", so the phone sees it as waiting.</summary>
    public static SyncOccurrence ToWire(AlarmOccurrence o) => new(
        o.Id, o.DefinitionId, SyncTime.Format(o.ScheduledFor), SyncTime.Format(o.AlertAt), SyncTime.Format(o.Deadline),
        (o.Status == OccurrenceStatus.Active ? OccurrenceStatus.Pending : o.Status).ToString(), o.SnoozeCount, o.LastAction,
        o.Revision);
}
