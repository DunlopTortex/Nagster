import Foundation

/// The hours this phone rings alarms set to ring on the iPhone only. One that falls outside them is snoozed until
/// they next start, unless its deadline comes first. Alarms for all devices ignore them, so the PC (with its own
/// hours) isn't held back. Mirrors `AlarmHours` in the Windows app: the hours may cross midnight, and a start equal
/// to the end means all day.
public struct AlarmHours: Hashable, Codable, Sendable {
    public var enabled: Bool
    /// Minutes after midnight.
    public var startMinutes: Int
    public var endMinutes: Int

    public init(enabled: Bool = false, startMinutes: Int = 8 * 60, endMinutes: Int = 21 * 60) {
        self.enabled = enabled
        self.startMinutes = startMinutes
        self.endMinutes = endMinutes
    }

    public func contains(_ time: WallTime) -> Bool {
        if !enabled || startMinutes == endMinutes { return true }
        let minute = Int(time.timeOfDay / 60)
        return startMinutes < endMinutes ? minute >= startMinutes && minute < endMinutes
            : minute >= startMinutes || minute < endMinutes
    }

    /// When the hours next start, at or after `time`: later the same day or the next.
    public func nextStart(onOrAfter time: WallTime) -> WallTime {
        let sameDay = time.startOfDay.adding(.minute, startMinutes)
        return sameDay >= time ? sameDay : sameDay.adding(.day, 1)
    }
}

extension AlarmState {
    /// Snoozes iPhone-only occurrences that would ring outside `hours` in the coming day until the hours next start,
    /// as the PC does for its own. One whose deadline comes first is left alone. Returns the snoozes for the PC.
    public mutating func holdOutsideAlarmHours(_ hours: AlarmHours, now: WallTime) -> [SyncOp] {
        guard hours.enabled else { return [] }
        var ops: [SyncOp] = []
        for occurrence in waitingOccurrences(through: now.adding(.day, 1)) {
            guard definitions[occurrence.definitionId]?.devices == .phone else { continue }
            // An overdue one rings now if now is inside the hours.
            let ringsAt = max(occurrence.alertAt, now)
            if hours.contains(ringsAt) { continue }
            let until = hours.nextStart(onOrAfter: ringsAt)
            if let deadline = occurrence.deadline, until > deadline { continue }
            ops.append(act(.snooze, on: occurrence, until: until, now: now))
        }
        return ops
    }
}
