namespace Nagster;

public sealed class AlarmEngine
{
    private readonly AlarmStore _store;
    private int _batchDepth;
    private bool _batchDirty;
    public AlarmState State { get; }
    public event Action? Changed;

    public AlarmEngine(AlarmStore store)
    {
        _store = store;
        State = store.Load();
        var interrupted = State.Occurrences.Where(o => o.Status == OccurrenceStatus.Active).ToList();
        foreach (var occurrence in interrupted)
        {
            occurrence.Status = OccurrenceStatus.Pending;
            occurrence.AlertAt = DateTime.Now;
            occurrence.LastAction = "Restored after app restart";
            Stamp(occurrence, automatic: true);
        }
        var rekeyed = RekeyUpcomingOccurrences();
        if (interrupted.Count > 0 || rekeyed) _store.Save(State);
    }

    private void Persist()
    {
        if (_batchDepth > 0)
        {
            _batchDirty = true;
            return;
        }
        _store.Save(State);
        Changed?.Invoke();
    }

    /// <summary>
    /// Groups several changes into one save, e.g. applying a whole sync from the phone. Changes made inside
    /// the scope are saved, and <see cref="Changed"/> raised, once when the outermost scope is disposed.
    /// </summary>
    public IDisposable Batch()
    {
        _batchDepth++;
        return new BatchScope(this);
    }

    private sealed class BatchScope(AlarmEngine engine) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (--engine._batchDepth > 0 || !engine._batchDirty) return;
            engine._batchDirty = false;
            engine.Persist();
        }
    }

    private void Stamp(AlarmDefinition definition) => definition.Revision = ++State.Revision;

    private void Stamp(AlarmOccurrence occurrence, bool automatic)
    {
        occurrence.Revision = ++State.Revision;
        occurrence.LastChangeAutomatic = automatic;
    }

    private void AddTombstone(Guid id, string kind) =>
        State.Deleted.Add(new Tombstone { Id = id, Kind = kind, Revision = ++State.Revision, DeletedAt = DateTime.Now });

    public void SaveSettings() => Persist();

    /// <summary>
    /// Records that today's Pushover notice for an alarm is being sent.
    /// </summary>
    /// <returns>False when one has already been sent for this alarm today.</returns>
    public bool TryClaimDailyNotice(AlarmDefinition definition, DateTime now)
    {
        if (definition.PushoverNotifiedOn?.Date == now.Date) return false;
        definition.PushoverNotifiedOn = now.Date;
        Persist();
        return true;
    }

    public void Add(AlarmDefinition definition)
    {
        AddWithoutSaving(definition);
        Persist();
    }

    private void AddWithoutSaving(AlarmDefinition definition)
    {
        definition.Title = definition.Title.Trim();
        if (definition.Title.Length == 0) throw new ArgumentException("An alarm title is required.");
        definition.GeneratedCount = 1;
        definition.NextGenerationAt = definition.Repeat.Next(definition.FirstDue, definition.FirstDue);
        Stamp(definition);
        State.Definitions.Add(definition);
        TryAddOccurrence(definition, definition.FirstDue);
    }

    /// <summary>
    /// Replaces an alarm's settings. If only its wording or devices changed, upcoming occurrences are kept
    /// as they are (including any snooze). Otherwise occurrences that haven't gone off yet (pending or
    /// snoozed) are dropped and the schedule restarts from the edited first alert; ones ringing now and
    /// past history are kept.
    /// </summary>
    public void Update(Guid definitionId, AlarmDefinition changes)
    {
        var index = State.Definitions.FindIndex(d => d.Id == definitionId);
        if (index < 0) throw new ArgumentException("That alarm no longer exists.", nameof(definitionId));
        var existing = State.Definitions[index];
        if (SameSchedule(existing, changes))
        {
            UpdateDetails(existing, changes);
            Persist();
            return;
        }
        // Keep the alarm's identity and bookkeeping that the editor doesn't show.
        changes.Id = existing.Id;
        changes.Enabled = existing.Enabled;
        changes.ImportedFrom = existing.ImportedFrom;
        changes.PushoverNotifiedOn = existing.PushoverNotifiedOn;
        foreach (var dropped in State.Occurrences.Where(o => o.DefinitionId == definitionId &&
                                                             (o.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed)).ToList())
        {
            State.Occurrences.Remove(dropped);
            AddTombstone(dropped.Id, Tombstone.OccurrenceKind);
        }
        State.Definitions.RemoveAt(index);
        AddWithoutSaving(changes);
        // "Stop after N occurrences" counts the whole series, including the ones that already happened.
        changes.GeneratedCount += State.Occurrences.Count(o => o.DefinitionId == definitionId) - 1;
        // AddWithoutSaving appends; put the alarm back where it was so lists keep their order.
        State.Definitions.Remove(changes);
        State.Definitions.Insert(index, changes);
        Persist();
    }

    /// <summary>True when an edit leaves every setting that decides when occurrences fall unchanged.</summary>
    private static bool SameSchedule(AlarmDefinition existing, AlarmDefinition changes) =>
        existing.FirstDue == changes.FirstDue && existing.IsEvent == changes.IsEvent && existing.EventEnd == changes.EventEnd &&
        existing.ReminderMinutes == changes.ReminderMinutes && existing.AbsoluteDeadline == changes.AbsoluteDeadline &&
        existing.DeadlineAfterMinutes == changes.DeadlineAfterMinutes &&
        System.Text.Json.JsonSerializer.Serialize(existing.Repeat) == System.Text.Json.JsonSerializer.Serialize(changes.Repeat);

    private void UpdateDetails(AlarmDefinition existing, AlarmDefinition changes)
    {
        var title = changes.Title.Trim();
        if (title.Length == 0) throw new ArgumentException("An alarm title is required.");
        existing.Title = title;
        existing.Description = changes.Description;
        existing.Sound = changes.Sound;
        existing.ResponseSecondsOverride = changes.ResponseSecondsOverride;
        existing.AutoSnoozeOverride = changes.AutoSnoozeOverride;
        existing.Devices = changes.Devices;
        existing.Important = changes.Important;
        Stamp(existing);
    }

    /// <summary>
    /// Moves one upcoming occurrence of a repeating alarm into its own one-off alarm, so it can be changed
    /// without touching the rest of the series.
    /// </summary>
    public void Detach(AlarmOccurrence occurrence, AlarmDefinition single)
    {
        if (single.Repeat.Kind != RepeatKind.Once) throw new ArgumentException("A detached occurrence must be a one-off alarm.", nameof(single));
        State.Occurrences.Remove(occurrence);
        AddTombstone(occurrence.Id, Tombstone.OccurrenceKind);
        AddWithoutSaving(single);
        Persist();
    }

    /// <summary>The alarm's next occurrence that hasn't gone off yet, if any.</summary>
    public AlarmOccurrence? NextPending(Guid definitionId) =>
        State.Occurrences.Where(o => o.DefinitionId == definitionId && (o.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed))
            .OrderBy(o => o.ScheduledFor).FirstOrDefault();

    /// <summary>Identities of calendar events imported so far (provider IDs and cross-service fingerprints).</summary>
    public HashSet<string> ImportedEventKeys() => State.ImportedEvents.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Adds the alarms planned from a calendar and remembers each event's identities so it is never imported
    /// again, even if the alarm is later deleted. Saves once for the whole batch.
    /// </summary>
    /// <returns>How many alarms were created.</returns>
    public int Import(IEnumerable<Calendars.PlannedEvent> plans, DateTime now)
    {
        var created = 0;
        foreach (var plan in plans.Where(p => p.Alarm is not null))
        {
            AddWithoutSaving(plan.Alarm!);
            State.ImportedEvents.Add(new Calendars.ImportedEvent { Key = plan.Event.Id, Start = plan.Event.Start });
            State.ImportedEvents.Add(new Calendars.ImportedEvent { Key = plan.Event.Fingerprint, Start = plan.Event.Start });
            created++;
        }
        // Events well in the past can't be fetched again, so their identities no longer need keeping.
        var pruned = State.ImportedEvents.RemoveAll(e => e.Start < now.AddDays(-60));
        if (created > 0 || pruned > 0) Persist();
        return created;
    }

    /// <summary>
    /// An occurrence's ID, derived from its alarm and scheduled time so the phone can name an occurrence it
    /// worked out for itself and the PC recognises it. Name-based (version 5 style) over SHA-1 of
    /// "{alarm id}|{yyyy-MM-ddTHH:mm:ss}"; the iPhone app computes exactly the same.
    /// </summary>
    public static Guid OccurrenceId(Guid definitionId, DateTime scheduledFor)
    {
        var name = $"{definitionId:D}|{SyncTime.Format(scheduledFor)}";
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        // Parsed from hex so the bytes keep RFC 4122 order (Guid's byte constructor reorders the first fields).
        return Guid.ParseExact(Convert.ToHexString(hash, 0, 16), "N");
    }

    public static AlarmOccurrence CreateOccurrence(AlarmDefinition definition, DateTime due) => new()
    {
        Id = OccurrenceId(definition.Id, due),
        DefinitionId = definition.Id,
        ScheduledFor = due,
        AlertAt = definition.IsEvent ? due.AddMinutes(-definition.ReminderMinutes) : due,
        Deadline = definition.DeadlineAfterMinutes is int minutes ? due.AddMinutes(minutes) : definition.AbsoluteDeadline
    };

    /// <summary>Adds an alarm's occurrence at <paramref name="due"/> unless one with the same ID is already kept.</summary>
    private AlarmOccurrence? TryAddOccurrence(AlarmDefinition definition, DateTime due)
    {
        var occurrence = CreateOccurrence(definition, due);
        if (State.Occurrences.Any(o => o.Id == occurrence.Id)) return null;
        Stamp(occurrence, automatic: true);
        State.Occurrences.Add(occurrence);
        return occurrence;
    }

    /// <summary>
    /// Gives upcoming occurrences made by older versions (random IDs) their derived IDs, so the phone and PC
    /// agree on them. Does nothing once every upcoming occurrence has been converted.
    /// </summary>
    private bool RekeyUpcomingOccurrences()
    {
        var known = State.Occurrences.Select(o => o.Id).ToHashSet();
        var changed = false;
        foreach (var occurrence in State.Occurrences.Where(o => o.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed))
        {
            var id = OccurrenceId(occurrence.DefinitionId, occurrence.ScheduledFor);
            if (id == occurrence.Id || !known.Add(id)) continue;
            occurrence.Id = id;
            Stamp(occurrence, automatic: true);
            changed = true;
        }
        return changed;
    }

    /// <summary>Most occurrences of one minute- or hour-repeating alarm kept ahead of time.</summary>
    private const int MaxUpcomingPerAlarm = 300;

    /// <summary>Occurrences going off now on this PC; alarms set to ring only on the phone are left out.</summary>
    public IReadOnlyList<AlarmOccurrence> Ready(DateTime now)
    {
        var changed = false;
        foreach (var definition in State.Definitions.Where(d => d.Enabled))
        {
            // Keep a rolling calendar horizon without filling the database with minute-by-minute years. Frequent
            // repeats look 3 days ahead (capped), so the phone has real occurrences while it's away from the PC.
            var frequent = definition.Repeat.Kind is RepeatKind.Minutes or RepeatKind.Hours;
            var horizon = frequent ? now.AddDays(3) : now.AddDays(90);
            if (definition.NextGenerationAt is not DateTime first || first.AddMinutes(-definition.ReminderMinutes) > horizon) continue;
            var upcoming = frequent ? State.Occurrences.Count(o => o.DefinitionId == definition.Id && o.Status == OccurrenceStatus.Pending) : 0;
            var generated = 0;
            while (definition.NextGenerationAt is DateTime next && next.AddMinutes(-definition.ReminderMinutes) <= horizon &&
                   generated++ < 100 && upcoming < MaxUpcomingPerAlarm)
            {
                if (definition.Repeat.Count is int count && definition.GeneratedCount >= count)
                {
                    definition.NextGenerationAt = null;
                    changed = true;
                    break;
                }
                if (TryAddOccurrence(definition, next) is not null) upcoming++;
                definition.GeneratedCount++;
                definition.NextGenerationAt = definition.Repeat.Next(definition.FirstDue, next);
                changed = true;
            }
        }
        changed |= CollapseMissedRepeats(now);
        if (changed) Persist();
        return State.Occurrences.Where(o => (o.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed) && o.AlertAt <= now && RingsHere(o))
            .OrderBy(o => o.Deadline ?? DateTime.MaxValue).ThenBy(o => o.ScheduledFor).ToList();
    }

    /// <summary>True unless the occurrence's alarm is set to ring only on the phone.</summary>
    private bool RingsHere(AlarmOccurrence occurrence) =>
        State.Definitions.FirstOrDefault(d => d.Id == occurrence.DefinitionId)?.RingsOnDesktop ?? true;

    /// <summary>Last action recorded on an occurrence skipped because a later one of the same alarm was also due.</summary>
    public const string MissedAction = "Missed: replaced by a later reminder";

    /// <summary>
    /// When several occurrences of one repeating alarm are waiting to go off at once (after a pause, or while the PC
    /// was off), only the most recent goes off; the earlier ones are marked missed rather than all ringing in a row.
    /// Occurrences already on screen are left alone, and so are phone-only alarms: the phone decides those.
    /// </summary>
    /// <returns>True if any occurrence was marked missed.</returns>
    private bool CollapseMissedRepeats(DateTime now)
    {
        var changed = false;
        var waiting = State.Occurrences.Where(o => (o.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed) && o.AlertAt <= now && RingsHere(o));
        foreach (var series in waiting.GroupBy(o => o.DefinitionId).Where(g => g.Count() > 1).ToList())
        {
            foreach (var earlier in series.OrderByDescending(o => o.ScheduledFor).Skip(1))
            {
                earlier.Status = OccurrenceStatus.Dismissed;
                earlier.RespondedAt = now;
                earlier.LastAction = MissedAction;
                Stamp(earlier, automatic: true);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// True while alarms are paused. A pause doesn't end by itself: when its time runs out (see
    /// <see cref="PauseExpired"/>) the user is asked whether to unpause or keep it going.
    /// </summary>
    public bool IsPaused => State.Settings.PausedUntil is not null;

    /// <summary>True once a timed pause's end time has passed; a "forever" pause never expires.</summary>
    public bool PauseExpired(DateTime now) => State.Settings.PausedUntil is DateTime until && until != DateTime.MaxValue && until <= now;

    /// <summary>
    /// Pauses all alarms until <paramref name="until"/> (DateTime.MaxValue for "until unpaused"). Alarms on screen
    /// go back to waiting, so they go off again, with everything else that came due, when the pause ends.
    /// </summary>
    public void Pause(DateTime until)
    {
        State.Settings.PausedUntil = until;
        foreach (var occurrence in State.Occurrences.Where(o => o.Status == OccurrenceStatus.Active))
        {
            occurrence.Status = OccurrenceStatus.Pending;
            Stamp(occurrence, automatic: true);
        }
        Persist();
    }

    public void Resume()
    {
        State.Settings.PausedUntil = null;
        Persist();
    }

    /// <summary>Start of an occurrence's last action after an automatic snooze ("Automatically snoozed until …").</summary>
    public const string AutoSnoozePrefix = "Automatically snoozed";

    /// <summary>Alarms snoozed (by the user or automatically) that haven't come due again yet, soonest first.</summary>
    public IReadOnlyList<AlarmOccurrence> Snoozed(DateTime now) =>
        State.Occurrences.Where(o => o.Status == OccurrenceStatus.Snoozed && o.AlertAt > now).OrderBy(o => o.AlertAt).ToList();

    /// <summary>
    /// Alarms going off now on this PC: on screen, or due but waiting for space on screen. Most urgent deadline
    /// first. Phone-only alarms are left out.
    /// </summary>
    public IReadOnlyList<AlarmOccurrence> Ringing(DateTime now) =>
        State.Occurrences.Where(o => o.Status == OccurrenceStatus.Active ||
                                     ((o.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed) && o.AlertAt <= now && RingsHere(o)))
            .OrderBy(o => o.Deadline ?? DateTime.MaxValue).ThenBy(o => o.AlertAt).ToList();

    /// <summary>Alarms that will alert after <paramref name="now"/> and within <paramref name="window"/>, soonest first.</summary>
    public IReadOnlyList<AlarmOccurrence> Upcoming(DateTime now, TimeSpan window) =>
        State.Occurrences.Where(o => (o.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed) &&
                                     o.AlertAt > now && o.AlertAt <= now.Add(window))
            .OrderBy(o => o.AlertAt).ToList();

    public AlarmDefinition Definition(AlarmOccurrence occurrence) => State.Definitions.First(d => d.Id == occurrence.DefinitionId);

    public void Delete(Guid definitionId)
    {
        if (State.Definitions.RemoveAll(d => d.Id == definitionId) > 0) AddTombstone(definitionId, Tombstone.AlarmKind);
        State.Occurrences.RemoveAll(o => o.DefinitionId == definitionId);
        Persist();
    }

    public void Activate(AlarmOccurrence occurrence)
    {
        occurrence.Status = OccurrenceStatus.Active;
        Stamp(occurrence, automatic: true);
        Persist();
    }

    public void Complete(AlarmOccurrence occurrence)
    {
        occurrence.Status = OccurrenceStatus.Completed;
        occurrence.RespondedAt = DateTime.Now;
        occurrence.LastAction = "Completed";
        Stamp(occurrence, automatic: false);
        Persist();
    }

    public void Dismiss(AlarmOccurrence occurrence)
    {
        occurrence.Status = OccurrenceStatus.Dismissed;
        occurrence.RespondedAt = DateTime.Now;
        occurrence.LastAction = "Dismissed without completion";
        Stamp(occurrence, automatic: false);
        Persist();
    }

    /// <summary>
    /// Snoozes an occurrence until <paramref name="next"/>, which must be in the future and no later than its deadline.
    /// With <paramref name="replay"/> (an action the phone took while it couldn't reach the PC) a time that has
    /// since passed is accepted and the occurrence is simply due again now.
    /// </summary>
    public bool TrySnooze(AlarmOccurrence occurrence, DateTime next, bool automatic, out string message, bool replay = false)
    {
        if (next <= DateTime.Now && !replay)
        {
            message = "Choose a future snooze time.";
            return false;
        }
        if (occurrence.Deadline is DateTime deadline && next > deadline)
        {
            message = $"This alarm must be completed by {deadline:g}. Choose a snooze no later than that time.";
            return false;
        }
        occurrence.AlertAt = next;
        occurrence.Status = OccurrenceStatus.Snoozed;
        occurrence.SnoozeCount++;
        occurrence.LastAction = automatic ? $"{AutoSnoozePrefix} until {next:g}" : $"Snoozed until {next:g}";
        Stamp(occurrence, automatic);
        Persist();
        message = "";
        return true;
    }

    /// <summary>Moves an occurrence's alert; <paramref name="replay"/> works as for <see cref="TrySnooze"/>.</summary>
    public bool TryReschedule(AlarmOccurrence occurrence, DateTime next, out string message, bool replay = false)
    {
        if (next <= DateTime.Now && !replay)
        {
            message = "Choose a future date and time.";
            return false;
        }
        if (occurrence.Deadline is DateTime deadline && next > deadline)
        {
            message = $"This alarm must be completed by {deadline:g}.";
            return false;
        }
        occurrence.AlertAt = next;
        occurrence.Status = OccurrenceStatus.Snoozed;
        occurrence.LastAction = $"Rescheduled from {occurrence.ScheduledFor:g} to {next:g}";
        Stamp(occurrence, automatic: false);
        Persist();
        message = "";
        return true;
    }

    /// <summary>
    /// Adds an occurrence the phone worked out for itself and then acted on, if it is a genuine occurrence of
    /// the alarm (its ID matches the one derived from the alarm and time) and isn't kept already.
    /// </summary>
    /// <returns>The kept or added occurrence, or null if the ID doesn't belong to that alarm and time.</returns>
    public AlarmOccurrence? AdoptProjected(Guid occurrenceId, AlarmDefinition definition, DateTime scheduledFor)
    {
        if (OccurrenceId(definition.Id, scheduledFor) != occurrenceId) return null;
        var kept = State.Occurrences.FirstOrDefault(o => o.Id == occurrenceId);
        if (kept is not null) return kept;
        var added = TryAddOccurrence(definition, scheduledFor);
        Persist();
        return added;
    }

    /// <summary>
    /// Forgets tombstones and applied-op records older than the phone could still need. A phone that last synced
    /// before the newest pruned tombstone is sent everything again.
    /// </summary>
    public void PruneSyncHistory(DateTime now)
    {
        var old = State.Deleted.Where(t => t.DeletedAt < now.AddDays(-90)).ToList();
        if (old.Count > 0)
        {
            State.PrunedThrough = Math.Max(State.PrunedThrough, old.Max(t => t.Revision));
            State.Deleted.RemoveAll(old.Contains);
        }
        var stale = State.AppliedOps.RemoveAll(o => o.AppliedAt < now.AddDays(-30));
        if (old.Count > 0 || stale > 0) Persist();
    }
}
