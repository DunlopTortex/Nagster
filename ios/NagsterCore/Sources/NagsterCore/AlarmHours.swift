import Foundation

/// The hours this phone alerts in. Outside them only Important alarms alert; anything else waits for them to start
/// (see `AlertPlanner`). Nothing is snoozed or changed, so the PC (with its own hours) isn't held back. Mirrors
/// `AlarmHours` in the Windows app: the hours may cross midnight, and a start equal to the end means all day.
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
