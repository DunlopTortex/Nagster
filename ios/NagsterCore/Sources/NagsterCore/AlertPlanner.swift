import Foundation

/// One alert to schedule on the phone: an occurrence's first alert or one of the reminders that follow while
/// it goes unanswered.
public struct PlannedAlert: Hashable, Sendable {
    public var occurrence: AlarmOccurrence
    public var fireAt: WallTime
    /// 0 for the first alert, then 1, 2… for each reminder.
    public var nag: Int
    /// The first alert of an Important alarm, which rings through AlarmKit rather than as a notification.
    public var important: Bool

    /// Stable per occurrence and reminder, so rescheduling replaces rather than duplicates.
    public var identifier: String { "\(occurrence.id.uuidString)#\(nag)" }
}

/// Works out the phone's alerts. The PC nags by ringing again after each automatic snooze until someone answers;
/// notifications can't wait for an answer, so the phone schedules those repeats in advance instead, at the phone's
/// own "nag again after" interval. They stop at the deadline, or when the alarm's next occurrence comes due
/// (which replaces it, as on the PC).
public enum AlertPlanner {
    /// iOS keeps only the soonest 64 pending notifications per app; a few are left spare.
    public static let notificationLimit = 60
    /// Reminders per occurrence, so one ignored alarm can't use up the whole allowance.
    public static let maxNags = 24
    public static let lookAheadDays = 14

    /// - Parameter nagEvery: How long between reminders for alarms without their own length; the PC's automatic
    ///   snooze length when nil.
    public static func plan(_ state: AlarmState, now: WallTime, pausedUntil: WallTime? = nil,
                            nagEvery: SnoozeDuration? = nil) -> [PlannedAlert] {
        let end = now.adding(.day, lookAheadDays)
        let step = nagEvery ?? state.settings.autoSnoozeDuration
        let ringsHere = state.waitingOccurrences(through: end).filter { occurrence in
            guard let definition = state.definition(of: occurrence) else { return false }
            return definition.enabled && definition.devices.ringsOnPhone
        }
        var alerts: [PlannedAlert] = []
        for series in Dictionary(grouping: ringsHere, by: \.definitionId).values {
            let ordered = series.sorted { a, b in a.scheduledFor < b.scheduledFor }
            for (index, occurrence) in ordered.enumerated() {
                let replacedAt = index + 1 < ordered.count ? ordered[index + 1].alertAt : nil
                let definition = state.definitions[occurrence.definitionId]
                let important = definition?.important ?? false
                // An alarm's own nag length wins over the device's.
                let nagStep = definition?.nagAfter ?? step
                var fireAt = occurrence.alertAt
                for nag in 0...maxNags {
                    if nag > 0 { fireAt = nagStep.added(to: fireAt) }
                    if let replacedAt, fireAt >= replacedAt { break }
                    if let deadline = occurrence.deadline, fireAt > deadline { break }
                    if fireAt > end { break }
                    if fireAt <= now { continue }
                    if let pausedUntil, fireAt < pausedUntil { continue }
                    alerts.append(PlannedAlert(occurrence: occurrence, fireAt: fireAt, nag: nag, important: important && nag == 0))
                }
            }
        }
        alerts.sort { a, b in (a.fireAt, a.identifier) < (b.fireAt, b.identifier) }
        // Important first alerts ring through AlarmKit, which has no such limit.
        var notifications = 0
        return alerts.filter { alert in
            if alert.important { return true }
            notifications += 1
            return notifications <= notificationLimit
        }
    }
}
