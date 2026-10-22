import Testing
@testable import NagsterCore

/// Mirrors the snooze checks in windows/Tests/Nagster.Checks/Program.cs.
struct SnoozeDurationTests {
    @Test func unitsParseAndApply() throws {
        let start = WallTime(year: 2026, month: 1, day: 31, hour: 9)
        let parsed = try #require(SnoozeDuration.list("15, 30s, 2H, 1 d, 1w, 1mo, 1y"))
        #expect(parsed.map { $0.added(to: start) } == [
            start.adding(.minute, 15), start.adding(.second, 30), start.adding(.hour, 2), start.adding(.day, 1),
            start.adding(.day, 7), WallTime(year: 2026, month: 2, day: 28, hour: 9), start.adding(.year, 1)
        ])
    }

    @Test func invalidChoicesRejected() {
        #expect(SnoozeDuration.list("5, 0m") == nil)
        #expect(SnoozeDuration.list("5x") == nil)
        #expect(SnoozeDuration.list("-5m") == nil)
        #expect(SnoozeDuration.list("+5m") == nil)
        #expect(SnoozeDuration.list(" , ") == nil)
    }

    @Test func choicesNormaliseAndLabel() throws {
        let bare = try #require(SnoozeDuration("15"))
        #expect(bare.description == "15m")
        #expect(bare.label == "15 minutes")
        #expect(SnoozeDuration("1mo")?.label == "1 month")
        #expect(bare.unitLabel == "minutes")
        #expect(SnoozeDuration("1d")?.unitLabel == "day")
    }
}
