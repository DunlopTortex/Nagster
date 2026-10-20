import Foundation

public enum RepeatKind: String, Codable, Sendable, CaseIterable {
    case once = "Once", minutes = "Minutes", hours = "Hours", daily = "Daily", weekly = "Weekly",
         monthly = "Monthly", yearly = "Yearly"
}

/// How an alarm repeats. A line-for-line port of `RepeatRule` in the Windows app's Models.cs: both
/// apps must produce the same times, because occurrence IDs are derived from them. The shared
/// cases in `shared/recurrence-cases.json` are checked against both implementations.
public struct RepeatRule: Hashable, Codable, Sendable {
    public var kind: RepeatKind = .once
    public var interval: Int = 1
    /// Bit mask of weekdays for weekly repeats, Sunday = bit 0; 0 means the anchor's weekday.
    public var weekdays: Int = 0
    /// Day of the month for monthly and yearly repeats; 0 means the anchor's day.
    public var dayOfMonth: Int = 0
    /// 1 to 4 for "the nth weekday", -1 for "the last", 0 for a plain day of the month.
    public var nth: Int = 0
    /// Weekday for nth repeats, Sunday = 0.
    public var nthWeekday: Int = 0
    public var useLastDayForShortMonths: Bool = true
    public var until: WallTime?
    public var count: Int?

    public init(kind: RepeatKind = .once, interval: Int = 1, weekdays: Int = 0, dayOfMonth: Int = 0, nth: Int = 0,
                nthWeekday: Int = 0, useLastDayForShortMonths: Bool = true, until: WallTime? = nil, count: Int? = nil) {
        self.kind = kind
        self.interval = interval
        self.weekdays = weekdays
        self.dayOfMonth = dayOfMonth
        self.nth = nth
        self.nthWeekday = nthWeekday
        self.useLastDayForShortMonths = useLastDayForShortMonths
        self.until = until
        self.count = count
    }

    /// The first time after `after` in the series that starts at `anchor`, or nil when it has ended.
    public func next(anchor: WallTime, after: WallTime) -> WallTime? {
        if kind == .once { return nil }
        let interval = max(1, interval)
        if kind == .minutes || kind == .hours {
            let step = TimeInterval(kind == .minutes ? 60 : 3600) * TimeInterval(interval)
            let count = max(1, Int(((after - anchor) / step).rounded(.down)) + 1)
            let next = anchor.adding(seconds: step * TimeInterval(count))
            if let until, next > until { return nil }
            return next
        }
        var day = after.startOfDay
        let end = day.adding(.year, 10)
        let anchorDay = anchor.startOfDay
        while day < end {
            defer { day = day.adding(.day, 1) }
            let next = day.adding(seconds: anchor.timeOfDay)
            if next <= after || next <= anchor { continue }
            if let until, next > until { continue }
            let days = Int(((day - anchorDay) / 86_400).rounded())
            let months = (day.year - anchor.year) * 12 + day.month - anchor.month
            let match: Bool
            switch kind {
            case .daily:
                match = days % interval == 0
            case .weekly:
                let mask = weekdays == 0 ? 1 << anchor.weekday : weekdays
                match = (days / 7) % interval == 0 && (mask & (1 << day.weekday)) != 0
            case .monthly:
                match = months >= 0 && months % interval == 0 && matchesMonthDay(day, anchor: anchor)
            case .yearly:
                match = day.year > anchor.year && (day.year - anchor.year) % interval == 0 &&
                    day.month == anchor.month && matchesMonthDay(day, anchor: anchor)
            case .once, .minutes, .hours:
                match = false
            }
            if match { return next }
        }
        return nil
    }

    private func matchesMonthDay(_ day: WallTime, anchor: WallTime) -> Bool {
        if nth != 0 {
            if day.weekday != nthWeekday { return false }
            return nth == -1 ? day.adding(.day, 7).month != day.month : (day.day - 1) / 7 + 1 == nth
        }
        var target = dayOfMonth == 0 ? anchor.day : dayOfMonth
        if useLastDayForShortMonths { target = min(target, day.daysInMonth) }
        return day.day == target
    }
}
