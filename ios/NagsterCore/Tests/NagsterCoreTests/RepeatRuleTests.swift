import Foundation
import Testing
@testable import NagsterCore

/// Mirrors the recurrence checks in windows/Tests/Nagster.Checks/Program.cs.
struct RepeatRuleTests {
    @Test func weeklySelectedWeekdays() {
        let monday = WallTime(year: 2026, month: 10, day: 12, hour: 9)
        let rule = RepeatRule(kind: .weekly, weekdays: (1 << 1) | (1 << 5))
        #expect(rule.next(anchor: monday, after: monday) == WallTime(year: 2026, month: 10, day: 16, hour: 9))
    }

    @Test func lastFridayOfMonth() {
        let start = WallTime(year: 2026, month: 9, day: 25, hour: 9)
        let rule = RepeatRule(kind: .monthly, nth: -1, nthWeekday: 5)
        #expect(rule.next(anchor: start, after: start) == WallTime(year: 2026, month: 10, day: 30, hour: 9))
    }

    @Test func leapDayFallback() {
        let start = WallTime(year: 2028, month: 2, day: 29, hour: 9)
        let rule = RepeatRule(kind: .yearly)
        #expect(rule.next(anchor: start, after: start) == WallTime(year: 2029, month: 2, day: 28, hour: 9))
    }

    @Test func minutesStepFromAnchor() {
        let anchor = WallTime(year: 2026, month: 10, day: 20, hour: 9)
        let rule = RepeatRule(kind: .minutes, interval: 20)
        #expect(rule.next(anchor: anchor, after: anchor.adding(.minute, 45)) == anchor.adding(.minute, 60))
        #expect(rule.next(anchor: anchor, after: anchor.adding(.minute, -5)) == anchor.adding(.minute, 20))
    }

    @Test func onceAndUntilEndTheSeries() {
        let anchor = WallTime(year: 2026, month: 10, day: 20, hour: 9)
        #expect(RepeatRule().next(anchor: anchor, after: anchor) == nil)
        let daily = RepeatRule(kind: .daily, until: anchor.adding(.hour, 12))
        #expect(daily.next(anchor: anchor, after: anchor) == nil)
    }

    @Test func wallTimeRoundTripsThroughText() throws {
        let time = WallTime(year: 2026, month: 3, day: 29, hour: 1, minute: 30, second: 5)
        #expect(time.iso == "2026-03-29T01:30:05")
        #expect(WallTime(iso: "2026-03-29T01:30:05.1234567+01:00") == time)
        let data = try JSONEncoder().encode([time])
        #expect(try JSONDecoder().decode([WallTime].self, from: data) == [time])
    }
}
