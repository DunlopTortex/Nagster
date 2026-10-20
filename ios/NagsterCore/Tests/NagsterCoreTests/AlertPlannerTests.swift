import Foundation
import Testing
@testable import NagsterCore

struct AlertPlannerTests {
    let nine = WallTime(year: 2026, month: 10, day: 20, hour: 9)

    func state(_ definitions: AlarmDefinition..., autoSnooze: String = "1h") -> AlarmState {
        var state = AlarmState()
        state.settings = PhoneSettings(autoSnooze: autoSnooze)
        for definition in definitions { state.definitions[definition.id] = definition }
        return state
    }

    @Test func nagsUntilTheNextOccurrenceTakesOver() {
        let daily = AlarmDefinition(title: "Pills", firstDue: nine, repeatRule: RepeatRule(kind: .daily))
        let plan = AlertPlanner.plan(state(daily, autoSnooze: "6h"), now: nine.adding(.minute, -1))
        let firstDay = plan.filter { $0.occurrence.scheduledFor == nine }
        // 09:00, then reminders at 15:00 and 21:00; 03:00 would also fall before tomorrow's 09:00.
        #expect(firstDay.map(\.fireAt) == [nine, nine.adding(.hour, 6), nine.adding(.hour, 12), nine.adding(.hour, 18)])
        #expect(firstDay.map(\.nag) == [0, 1, 2, 3])
    }

    @Test func nagsStopAtTheDeadline() {
        let due = AlarmDefinition(title: "Submit form", firstDue: nine, deadlineAfterMinutes: 150)
        let plan = AlertPlanner.plan(state(due), now: nine.adding(.minute, -1))
        #expect(plan.map(\.fireAt) == [nine, nine.adding(.hour, 1), nine.adding(.hour, 2)])
    }

    @Test func anOverdueAlarmKeepsNaggingFromWhereItIs() {
        let once = AlarmDefinition(title: "Call back", firstDue: nine)
        let plan = AlertPlanner.plan(state(once), now: nine.adding(.minute, 90))
        #expect(plan.first?.fireAt == nine.adding(.hour, 2) && plan.first?.nag == 2)
    }

    @Test func desktopOnlyAlarmsAreLeftToThePC() {
        let pcOnly = AlarmDefinition(title: "PC", firstDue: nine, devices: .desktop)
        #expect(AlertPlanner.plan(state(pcOnly), now: nine.adding(.minute, -1)).isEmpty)
    }

    @Test func importantAlarmsRingFirstThroughAlarmKit() {
        let wake = AlarmDefinition(title: "Wake up", firstDue: nine, important: true)
        let plan = AlertPlanner.plan(state(wake), now: nine.adding(.minute, -1))
        #expect(plan.first?.important == true && plan.dropFirst().allSatisfy { !$0.important })
    }

    @Test func staysWithinTheNotificationLimit() {
        let often = AlarmDefinition(title: "Often", firstDue: nine, repeatRule: RepeatRule(kind: .minutes, interval: 5))
        let plan = AlertPlanner.plan(state(often, autoSnooze: "1m"), now: nine.adding(.minute, -1))
        #expect(plan.count == AlertPlanner.notificationLimit)
        #expect(zip(plan, plan.dropFirst()).allSatisfy { $0.fireAt <= $1.fireAt })
    }

    @Test func thePhonesOwnIntervalOverridesThePCs() {
        let once = AlarmDefinition(title: "Bins", firstDue: nine)
        let plan = AlertPlanner.plan(state(once, autoSnooze: "1h"), now: nine.adding(.minute, -1),
                                     nagEvery: SnoozeDuration(amount: 10, unit: .minutes))
        #expect(plan.prefix(3).map(\.fireAt) == [nine, nine.adding(.minute, 10), nine.adding(.minute, 20)])
    }

    @Test func pausedAlertsAreSkipped() {
        let once = AlarmDefinition(title: "Later", firstDue: nine)
        let plan = AlertPlanner.plan(state(once), now: nine.adding(.minute, -1), pausedUntil: nine.adding(.minute, 90))
        #expect(plan.first?.fireAt == nine.adding(.hour, 2))
    }
}
