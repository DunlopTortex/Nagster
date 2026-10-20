import Foundation

/// Everything the phone knows: the PC's alarms and occurrences as last synced, with the user's own actions
/// applied on top until the PC confirms them. The PC is the master copy; this mirrors its rules closely
/// enough that the phone behaves the same while it can't reach the PC.
public struct AlarmState: Hashable, Sendable {
    public var definitions: [UUID: AlarmDefinition] = [:]
    public var occurrences: [UUID: AlarmOccurrence] = [:]
    /// The newest PC revision seen; 0 before the first sync.
    public var revision: Int64 = 0
    public var settings = PhoneSettings()

    public init() {}

    public func definition(of occurrence: AlarmOccurrence) -> AlarmDefinition? { definitions[occurrence.definitionId] }

    // MARK: - Working out occurrences

    /// Most occurrences worked out ahead for one alarm, so a minute-repeating alarm can't flood the list.
    public static let projectionLimit = 200

    /// Waiting occurrences that alert no later than `end`: the stored ones plus any the alarm's rule produces
    /// beyond them. The PC stores weeks ahead (days for frequent repeats), so this mostly fills in for alarms
    /// added on the phone and for long stretches away from the PC. Projected occurrences have the IDs the PC
    /// would give them.
    public func waitingOccurrences(through end: WallTime) -> [AlarmOccurrence] {
        var result = occurrences.values.filter { $0.status.isWaiting && $0.alertAt <= end }
        for definition in definitions.values where definition.enabled {
            result += projected(for: definition, through: end)
        }
        return result.sorted { ($0.alertAt, $0.scheduledFor) < ($1.alertAt, $1.scheduledFor) }
    }

    func projected(for definition: AlarmDefinition, through end: WallTime) -> [AlarmOccurrence] {
        // The PC generates a series in order, so carry on after the latest occurrence stored for it.
        let lastStored = occurrences.values.filter { $0.definitionId == definition.id }.map(\.scheduledFor).max()
        let times = seriesTimes(of: definition, after: lastStored)
        var result: [AlarmOccurrence] = []
        while result.count < Self.projectionLimit, let time = times.next() {
            let occurrence = definition.occurrence(at: time)
            if occurrence.alertAt > end { break }
            if occurrences[occurrence.id] == nil { result.append(occurrence) }
        }
        return result
    }

    /// The alarm's series times after `start` (from the first alert when nil), honouring "stop after N".
    func seriesTimes(of definition: AlarmDefinition, after start: WallTime?) -> AnyIterator<WallTime> {
        let rule = definition.repeatRule
        let anchor = definition.firstDue
        if let count = rule.count {
            // Counted series are short; step from the start so the count is right.
            var current: WallTime? = anchor
            var index = 0
            return AnyIterator {
                while let time = current, index < count {
                    index += 1
                    current = rule.next(anchor: anchor, after: time)
                    if start.map({ time > $0 }) ?? true { return time }
                }
                return nil
            }
        }
        var current: WallTime?
        if let start {
            current = anchor > start ? anchor : rule.next(anchor: anchor, after: start)
        } else {
            current = anchor
        }
        return AnyIterator {
            guard let time = current else { return nil }
            current = rule.next(anchor: anchor, after: time)
            return time
        }
    }

    /// Occurrences going off on this phone now, one per alarm: when several of a repeating alarm are due at
    /// once (the phone was off, or alerts were paused) only the latest rings, as on the PC.
    public func ringing(at now: WallTime) -> [AlarmOccurrence] {
        let due = waitingOccurrences(through: now).filter { definitions[$0.definitionId]?.devices.ringsOnPhone == true }
        let latest = Dictionary(grouping: due, by: \.definitionId).values.compactMap { series in
            series.max { a, b in a.scheduledFor < b.scheduledFor }
        }
        return latest.sorted { a, b in (a.deadline ?? .distantFuture, a.alertAt) < (b.deadline ?? .distantFuture, b.alertAt) }
    }

    // MARK: - The user's actions

    /// The time a snooze of `duration` from `now` lands on, or nil if the deadline has already passed.
    /// A snooze never goes past the deadline; one that would is shortened to end at it.
    public static func snoozeTarget(_ duration: SnoozeDuration, from now: WallTime, deadline: WallTime?) -> WallTime? {
        let target = duration.added(to: now)
        guard let deadline else { return target }
        if deadline <= now { return nil }
        return min(target, deadline)
    }

    /// Builds the op for an action on an occurrence (stored or projected) and applies it here straight away.
    public mutating func act(_ type: SyncOpType, on occurrence: AlarmOccurrence, until: WallTime? = nil, now: WallTime) -> SyncOp {
        let op = SyncOp(type: type, baseRevision: occurrences[occurrence.id]?.revision ?? 0, occurrenceId: occurrence.id,
                        definitionId: occurrence.definitionId, scheduledFor: occurrence.scheduledFor, until: until)
        apply(op, now: now)
        return op
    }

    public mutating func save(_ definition: AlarmDefinition) -> SyncOp {
        let op = SyncOp(type: .upsertAlarm, baseRevision: definitions[definition.id]?.revision ?? 0, definition: definition)
        apply(op, now: definition.firstDue)
        return op
    }

    public mutating func delete(_ definitionID: UUID) -> SyncOp {
        let op = SyncOp(type: .deleteAlarm, baseRevision: definitions[definitionID]?.revision ?? 0, definitionId: definitionID)
        apply(op, now: WallTime(Date()))
        return op
    }

    /// Applies one of the user's actions to this copy, as the PC will when it receives it. Used when the action
    /// is taken, and again after each sync for actions the PC hasn't confirmed yet.
    public mutating func apply(_ op: SyncOp, now: WallTime) {
        switch op.type {
        case .upsertAlarm:
            guard let incoming = op.definition else { return }
            if let existing = definitions[incoming.id], Self.sameSchedule(existing, incoming) {
                // Only the wording or devices changed: upcoming occurrences (and any snooze) stay as they are.
                var kept = existing
                kept.title = incoming.title
                kept.description = incoming.description
                kept.devices = incoming.devices
                kept.important = incoming.important
                definitions[incoming.id] = kept
                return
            }
            // As on the PC: occurrences that haven't gone off restart from the edited schedule.
            occurrences = occurrences.filter { $0.value.definitionId != incoming.id || !$0.value.status.isWaiting }
            var updated = incoming
            updated.revision = definitions[incoming.id]?.revision ?? 0
            definitions[incoming.id] = updated
        case .deleteAlarm:
            guard let id = op.definitionId else { return }
            definitions[id] = nil
            occurrences = occurrences.filter { $0.value.definitionId != id }
        case .complete, .dismiss, .snooze, .reschedule:
            guard var occurrence = storedOrProjected(op) else { return }
            if occurrence.status == .completed { return }
            switch op.type {
            case .complete:
                occurrence.status = .completed
                occurrence.lastAction = "Completed"
            case .dismiss:
                occurrence.status = .dismissed
                occurrence.lastAction = "Dismissed without completion"
            case .snooze, .reschedule:
                guard let until = op.until, !occurrence.status.isFinished else { return }
                occurrence.alertAt = until
                occurrence.status = .snoozed
                if op.type == .snooze {
                    occurrence.snoozeCount += 1
                    occurrence.lastAction = "Snoozed until \(until.iso)"
                } else {
                    occurrence.lastAction = "Rescheduled to \(until.iso)"
                }
            case .upsertAlarm, .deleteAlarm:
                return
            }
            occurrences[occurrence.id] = occurrence
        }
    }

    private func storedOrProjected(_ op: SyncOp) -> AlarmOccurrence? {
        guard let id = op.occurrenceId else { return nil }
        if let stored = occurrences[id] { return stored }
        guard let definitionID = op.definitionId, let scheduledFor = op.scheduledFor,
              let definition = definitions[definitionID] else { return nil }
        let occurrence = definition.occurrence(at: scheduledFor)
        return occurrence.id == id ? occurrence : nil
    }

    /// True when an edit leaves the times occurrences fall on unchanged, as the PC's `AlarmEngine.SameSchedule`
    /// decides it: a repeating alarm edited from its next run (a time already in its series) counts as unchanged,
    /// unless the series is counted.
    static func sameSchedule(_ existing: AlarmDefinition, _ edited: AlarmDefinition) -> Bool {
        let length = { (definition: AlarmDefinition) in definition.eventEnd.map { $0 - definition.firstDue } }
        let sameRules = existing.isEvent == edited.isEvent && existing.reminderMinutes == edited.reminderMinutes &&
            existing.absoluteDeadline == edited.absoluteDeadline && existing.deadlineAfterMinutes == edited.deadlineAfterMinutes &&
            existing.repeatRule == edited.repeatRule && length(existing) == length(edited)
        guard sameRules else { return false }
        if existing.firstDue == edited.firstDue { return true }
        return existing.repeatRule.count == nil && edited.firstDue > existing.firstDue &&
            existing.repeatRule.next(anchor: existing.firstDue, after: edited.firstDue.adding(seconds: -1)) == edited.firstDue
    }

    // MARK: - Syncing

    /// Takes in the PC's reply, then re-applies the actions still waiting to be sent (taken while the sync
    /// was under way), so they aren't lost from view.
    public mutating func merge(_ response: SyncResponse, unsent: [SyncOp], now: WallTime) {
        if response.full {
            definitions = Dictionary(response.definitions.map { ($0.id, $0) }, uniquingKeysWith: { _, newer in newer })
            occurrences = Dictionary(response.occurrences.map { ($0.id, $0) }, uniquingKeysWith: { _, newer in newer })
        } else {
            for definition in response.definitions { definitions[definition.id] = definition }
            for occurrence in response.occurrences { occurrences[occurrence.id] = occurrence }
            for tombstone in response.tombstones {
                if tombstone.kind == SyncTombstone.alarmKind {
                    definitions[tombstone.id] = nil
                    occurrences = occurrences.filter { $0.value.definitionId != tombstone.id }
                } else {
                    occurrences[tombstone.id] = nil
                }
            }
        }
        revision = response.revision
        settings = response.settings
        for op in unsent { apply(op, now: now) }
    }
}

extension WallTime {
    /// Later than any real alarm time, for sorting occurrences without a deadline last.
    public static let distantFuture = WallTime(year: 9999, month: 12, day: 31)
}
