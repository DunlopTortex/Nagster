namespace Nagster;

public sealed class AlarmEngine
{
    private readonly AlarmStore _store;
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
        }
        if (interrupted.Count > 0) _store.Save(State);
    }

    private void Persist()
    {
        _store.Save(State);
        Changed?.Invoke();
    }

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
        State.Definitions.Add(definition);
        State.Occurrences.Add(CreateOccurrence(definition, definition.FirstDue));
    }

    /// <summary>
    /// Replaces an alarm's settings. Occurrences that haven't gone off yet (pending or snoozed) are dropped and
    /// the schedule restarts from the edited first alert; ones ringing now and past history are kept.
    /// </summary>
    public void Update(Guid definitionId, AlarmDefinition changes)
    {
        var index = State.Definitions.FindIndex(d => d.Id == definitionId);
        if (index < 0) throw new ArgumentException("That alarm no longer exists.", nameof(definitionId));
        var existing = State.Definitions[index];
        // Keep the alarm's identity and bookkeeping that the editor doesn't show.
        changes.Id = existing.Id;
        changes.Enabled = existing.Enabled;
        changes.ImportedFrom = existing.ImportedFrom;
        changes.PushoverNotifiedOn = existing.PushoverNotifiedOn;
        State.Occurrences.RemoveAll(o => o.DefinitionId == definitionId &&
                                         (o.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed));
        State.Definitions.RemoveAt(index);
        AddWithoutSaving(changes);
        // "Stop after N occurrences" counts the whole series, including the ones that already happened.
        changes.GeneratedCount += State.Occurrences.Count(o => o.DefinitionId == definitionId) - 1;
        // AddWithoutSaving appends; put the alarm back where it was so lists keep their order.
        State.Definitions.Remove(changes);
        State.Definitions.Insert(index, changes);
        Persist();
    }

    /// <summary>
    /// Moves one upcoming occurrence of a repeating alarm into its own one-off alarm, so it can be changed
    /// without touching the rest of the series.
    /// </summary>
    public void Detach(AlarmOccurrence occurrence, AlarmDefinition single)
    {
        if (single.Repeat.Kind != RepeatKind.Once) throw new ArgumentException("A detached occurrence must be a one-off alarm.", nameof(single));
        State.Occurrences.Remove(occurrence);
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

    private static AlarmOccurrence CreateOccurrence(AlarmDefinition definition, DateTime due) => new()
    {
        DefinitionId = definition.Id,
        ScheduledFor = due,
        AlertAt = definition.IsEvent ? due.AddMinutes(-definition.ReminderMinutes) : due,
        Deadline = definition.DeadlineAfterMinutes is int minutes ? due.AddMinutes(minutes) : definition.AbsoluteDeadline
    };

    public IReadOnlyList<AlarmOccurrence> Ready(DateTime now)
    {
        var changed = false;
        foreach (var definition in State.Definitions.Where(d => d.Enabled))
        {
            // Keep a rolling calendar horizon without filling the database with minute-by-minute years.
            var horizon = definition.Repeat.Kind is RepeatKind.Minutes or RepeatKind.Hours
                ? now.AddHours(12) : now.AddDays(90);
            var generated = 0;
            while (definition.NextGenerationAt is DateTime next && next.AddMinutes(-definition.ReminderMinutes) <= horizon && generated++ < 100)
            {
                if (definition.Repeat.Count is int count && definition.GeneratedCount >= count)
                {
                    definition.NextGenerationAt = null;
                    changed = true;
                    break;
                }
                State.Occurrences.Add(CreateOccurrence(definition, next));
                definition.GeneratedCount++;
                definition.NextGenerationAt = definition.Repeat.Next(definition.FirstDue, next);
                changed = true;
            }
        }
        changed |= CollapseMissedRepeats(now);
        if (changed) Persist();
        return State.Occurrences.Where(o => (o.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed) && o.AlertAt <= now)
            .OrderBy(o => o.Deadline ?? DateTime.MaxValue).ThenBy(o => o.ScheduledFor).ToList();
    }

    /// <summary>Last action recorded on an occurrence skipped because a later one of the same alarm was also due.</summary>
    public const string MissedAction = "Missed: replaced by a later reminder";

    /// <summary>
    /// When several occurrences of one repeating alarm are waiting to go off at once (after a pause, or while the PC
    /// was off), only the most recent goes off; the earlier ones are marked missed rather than all ringing in a row.
    /// Occurrences already on screen are left alone.
    /// </summary>
    /// <returns>True if any occurrence was marked missed.</returns>
    private bool CollapseMissedRepeats(DateTime now)
    {
        var changed = false;
        var waiting = State.Occurrences.Where(o => (o.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed) && o.AlertAt <= now);
        foreach (var series in waiting.GroupBy(o => o.DefinitionId).Where(g => g.Count() > 1).ToList())
        {
            foreach (var earlier in series.OrderByDescending(o => o.ScheduledFor).Skip(1))
            {
                earlier.Status = OccurrenceStatus.Dismissed;
                earlier.RespondedAt = now;
                earlier.LastAction = MissedAction;
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
            occurrence.Status = OccurrenceStatus.Pending;
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

    /// <summary>Alarms going off now: on screen, or due but waiting for space on screen. Most urgent deadline first.</summary>
    public IReadOnlyList<AlarmOccurrence> Ringing(DateTime now) =>
        State.Occurrences.Where(o => o.Status == OccurrenceStatus.Active ||
                                     ((o.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed) && o.AlertAt <= now))
            .OrderBy(o => o.Deadline ?? DateTime.MaxValue).ThenBy(o => o.AlertAt).ToList();

    /// <summary>Alarms that will alert after <paramref name="now"/> and within <paramref name="window"/>, soonest first.</summary>
    public IReadOnlyList<AlarmOccurrence> Upcoming(DateTime now, TimeSpan window) =>
        State.Occurrences.Where(o => (o.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed) &&
                                     o.AlertAt > now && o.AlertAt <= now.Add(window))
            .OrderBy(o => o.AlertAt).ToList();

    public AlarmDefinition Definition(AlarmOccurrence occurrence) => State.Definitions.First(d => d.Id == occurrence.DefinitionId);

    public void Delete(Guid definitionId)
    {
        State.Definitions.RemoveAll(d => d.Id == definitionId);
        State.Occurrences.RemoveAll(o => o.DefinitionId == definitionId);
        Persist();
    }

    public void Activate(AlarmOccurrence occurrence)
    {
        occurrence.Status = OccurrenceStatus.Active;
        Persist();
    }

    public void Complete(AlarmOccurrence occurrence)
    {
        occurrence.Status = OccurrenceStatus.Completed;
        occurrence.RespondedAt = DateTime.Now;
        occurrence.LastAction = "Completed";
        Persist();
    }

    public void Dismiss(AlarmOccurrence occurrence)
    {
        occurrence.Status = OccurrenceStatus.Dismissed;
        occurrence.RespondedAt = DateTime.Now;
        occurrence.LastAction = "Dismissed without completion";
        Persist();
    }

    public bool TrySnooze(AlarmOccurrence occurrence, DateTime next, bool automatic, out string message)
    {
        if (next <= DateTime.Now)
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
        Persist();
        message = "";
        return true;
    }

    public bool TryReschedule(AlarmOccurrence occurrence, DateTime next, out string message)
    {
        if (next <= DateTime.Now)
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
        Persist();
        message = "";
        return true;
    }
}
