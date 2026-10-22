import Foundation

/// A date and time as read off a wall clock, with no time zone, which is how the desktop app stores
/// every alarm time. Arithmetic is plain calendar arithmetic with no daylight-saving jumps, matching
/// .NET's `DateTime`, so both apps work out identical repeat times. Convert to a real instant only
/// when scheduling, with `date(in:)`.
public struct WallTime: Hashable, Comparable, Sendable {
    /// The instant this wall-clock reading would be in GMT; used only as an arithmetic carrier.
    let floating: Date

    static let calendar: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "GMT")!
        return calendar
    }()

    init(floating: Date) {
        self.floating = floating
    }

    public init(year: Int, month: Int, day: Int, hour: Int = 0, minute: Int = 0, second: Int = 0) {
        let components = DateComponents(year: year, month: month, day: day, hour: hour, minute: minute, second: second)
        floating = Self.calendar.date(from: components)!
    }

    /// The wall-clock reading of `date` in `timeZone`.
    public init(_ date: Date, in timeZone: TimeZone = .current) {
        floating = date.addingTimeInterval(TimeInterval(timeZone.secondsFromGMT(for: date)))
    }

    /// The real instant at which a clock in `timeZone` shows this reading.
    public func date(in timeZone: TimeZone = .current) -> Date {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        return calendar.date(from: components) ?? floating
    }

    public var components: DateComponents {
        Self.calendar.dateComponents([.year, .month, .day, .hour, .minute, .second], from: floating)
    }

    public var year: Int { Self.calendar.component(.year, from: floating) }
    public var month: Int { Self.calendar.component(.month, from: floating) }
    public var day: Int { Self.calendar.component(.day, from: floating) }

    /// Day of the week with Sunday = 0, as .NET's `DayOfWeek` numbers it.
    public var weekday: Int { Self.calendar.component(.weekday, from: floating) - 1 }

    /// Midnight at the start of this reading's day.
    public var startOfDay: WallTime { WallTime(floating: Self.calendar.startOfDay(for: floating)) }

    /// Time since midnight, in seconds.
    public var timeOfDay: TimeInterval { floating.timeIntervalSince(Self.calendar.startOfDay(for: floating)) }

    public var daysInMonth: Int { Self.calendar.range(of: .day, in: .month, for: floating)!.count }

    public func adding(seconds: TimeInterval) -> WallTime { WallTime(floating: floating.addingTimeInterval(seconds)) }

    /// Calendar arithmetic; months and years clamp to the end of shorter months, like .NET's `AddMonths`.
    public func adding(_ component: Calendar.Component, _ amount: Int) -> WallTime {
        WallTime(floating: Self.calendar.date(byAdding: component, value: amount, to: floating)!)
    }

    public static func - (lhs: WallTime, rhs: WallTime) -> TimeInterval { lhs.floating.timeIntervalSince(rhs.floating) }

    public static func < (lhs: WallTime, rhs: WallTime) -> Bool { lhs.floating < rhs.floating }
}

extension WallTime: Codable {
    /// Parses "yyyy-MM-ddTHH:mm:ss"; any fraction of a second or offset after that is ignored.
    public init?(iso text: String) {
        let parts = text.prefix(19).split(whereSeparator: { "-T:".contains($0) }).compactMap { Int($0) }
        guard parts.count == 6 else { return nil }
        self.init(year: parts[0], month: parts[1], day: parts[2], hour: parts[3], minute: parts[4], second: parts[5])
    }

    /// "yyyy-MM-ddTHH:mm:ss", the form the desktop sends and expects.
    public var iso: String {
        let c = components
        return String(format: "%04d-%02d-%02dT%02d:%02d:%02d", c.year!, c.month!, c.day!, c.hour!, c.minute!, c.second!)
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let text = try container.decode(String.self)
        guard let value = WallTime(iso: text) else {
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "Expected yyyy-MM-ddTHH:mm:ss, got \(text)")
        }
        self = value
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(iso)
    }
}
