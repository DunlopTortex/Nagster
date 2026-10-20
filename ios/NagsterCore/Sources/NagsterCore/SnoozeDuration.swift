import Foundation

public enum SnoozeUnit: Sendable, CaseIterable {
    case seconds, minutes, hours, days, weeks, months, years

    fileprivate var suffix: String {
        switch self {
        case .seconds: "s"
        case .minutes: "m"
        case .hours: "h"
        case .days: "d"
        case .weeks: "w"
        case .months: "mo"
        case .years: "y"
        }
    }

    fileprivate var noun: String {
        switch self {
        case .seconds: "second"
        case .minutes: "minute"
        case .hours: "hour"
        case .days: "day"
        case .weeks: "week"
        case .months: "month"
        case .years: "year"
        }
    }
}

/// A snooze length such as "30m", "1d" or "2w", as entered in the desktop's Snooze choices setting.
/// A bare number means minutes. Months and years use calendar arithmetic, so one month from
/// 31 January lands at the end of February. Mirrors `SnoozeDuration` in the Windows app.
public struct SnoozeDuration: Hashable, Sendable, CustomStringConvertible {
    public static let maxAmount = 999

    public let amount: Int
    public let unit: SnoozeUnit

    public init(amount: Int, unit: SnoozeUnit) {
        self.amount = amount
        self.unit = unit
    }

    /// Checked longest first so "mo" isn't read as minutes; "" is the bare-number fallback.
    private static let suffixes: [(String, SnoozeUnit)] =
        [("mo", .months), ("s", .seconds), ("m", .minutes), ("h", .hours), ("d", .days), ("w", .weeks), ("y", .years), ("", .minutes)]

    /// Reads one entry such as "15", "15m", "1 d" or "2W"; units are case-insensitive.
    public init?(_ text: String) {
        let trimmed = text.trimmingCharacters(in: .whitespaces).lowercased()
        for (suffix, unit) in Self.suffixes where trimmed.hasSuffix(suffix) {
            let number = trimmed.dropLast(suffix.count).trimmingCharacters(in: .whitespaces)
            // Digits only, like .NET's NumberStyles.None: Int() alone would also accept a leading "+".
            guard !number.isEmpty, number.allSatisfy({ $0.isASCII && $0.isNumber }), let amount = Int(number),
                  (1...Self.maxAmount).contains(amount) else { return nil }
            self.init(amount: amount, unit: unit)
            return
        }
        return nil
    }

    /// Parses a comma-separated list, returning nil if any entry is invalid or the list is empty.
    public static func list(_ text: String) -> [SnoozeDuration]? {
        let parts = text.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        let parsed = parts.compactMap(SnoozeDuration.init)
        return parsed.isEmpty || parsed.count != parts.count ? nil : parsed
    }

    public func added(to start: WallTime) -> WallTime {
        switch unit {
        case .seconds: start.adding(.second, amount)
        case .minutes: start.adding(.minute, amount)
        case .hours: start.adding(.hour, amount)
        case .days: start.adding(.day, amount)
        case .weeks: start.adding(.day, 7 * amount)
        case .months: start.adding(.month, amount)
        case .years: start.adding(.year, amount)
        }
    }

    /// Human-readable form for buttons, e.g. "1 day" or "15 minutes".
    public var label: String { amount == 1 ? "1 \(unit.noun)" : "\(amount) \(unit.noun)s" }

    /// Canonical setting form, e.g. "15m" or "2mo".
    public var description: String { "\(amount)\(unit.suffix)" }
}
