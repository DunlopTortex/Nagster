import Foundation
import Testing
@testable import NagsterCore

/// Mirrors the alarm hours checks in windows/Tests/Nagster.Checks/SyncChecks.cs.
struct AlarmHoursTests {
    let day = WallTime(year: 2026, month: 10, day: 20)
    let daytime = AlarmHours(enabled: true, startMinutes: 8 * 60, endMinutes: 18 * 60)

    @Test func rangeIncludesStartExcludesEndAndCanCrossMidnight() {
        let night = AlarmHours(enabled: true, startMinutes: 22 * 60, endMinutes: 6 * 60)
        #expect(daytime.contains(day.adding(.hour, 8)) && !daytime.contains(day.adding(.hour, 18)))
        #expect(!daytime.contains(day.adding(.minute, 7 * 60 + 30)))
        #expect(night.contains(day.adding(.hour, 23)) && night.contains(day.adding(.hour, 3)) && !night.contains(day.adding(.hour, 12)))
        #expect(AlarmHours(startMinutes: 8 * 60, endMinutes: 18 * 60).contains(day.adding(.hour, 23)))
    }

    @Test func nextStartIsLaterTheSameDayOrTheNextMorning() {
        #expect(daytime.nextStart(onOrAfter: day.adding(.hour, 6)) == day.adding(.hour, 8))
        #expect(daytime.nextStart(onOrAfter: day.adding(.hour, 19)) == day.adding(.day, 1).adding(.hour, 8))
    }

    @Test func onlyImportantAlarmsAlertOutsideTheHours() {
        let now = day.adding(.hour, 17)
        let lateOnPhone = AlarmDefinition(title: "Phone", firstDue: day.adding(.hour, 20), devices: .phone)
        let lateEverywhere = AlarmDefinition(title: "Everywhere", firstDue: day.adding(.hour, 20))
        let lateWithDeadline = AlarmDefinition(title: "Deadline", firstDue: day.adding(.hour, 20), deadlineAfterMinutes: 60)
        let lateImportant = AlarmDefinition(title: "Important", firstDue: day.adding(.hour, 20), important: true)
        var state = AlarmState()
        for definition in [lateOnPhone, lateEverywhere, lateWithDeadline, lateImportant] { state.definitions[definition.id] = definition }
        let plan = AlertPlanner.plan(state, now: now, nagging: false, alarmHours: daytime)
        let tomorrowMorning = day.adding(.day, 1).adding(.hour, 8)
        func firstAlert(_ definition: AlarmDefinition) -> WallTime? {
            plan.first { $0.occurrence.definitionId == definition.id && $0.nag == 0 }?.fireAt
        }
        #expect(firstAlert(lateOnPhone) == tomorrowMorning && firstAlert(lateEverywhere) == tomorrowMorning)
        #expect(firstAlert(lateWithDeadline) == nil)
        #expect(firstAlert(lateImportant) == day.adding(.hour, 20))
        #expect(plan.allSatisfy { $0.occurrence.definitionId == lateImportant.id || daytime.contains($0.fireAt) })
    }

    @Test func remindersOutsideTheHoursWaitForTheStart() {
        let evening = AlarmDefinition(title: "Evening", firstDue: day.adding(.hour, 17), devices: .phone)
        var state = AlarmState()
        state.definitions[evening.id] = evening
        let plan = AlertPlanner.plan(state, now: day.adding(.hour, 16), nagEvery: SnoozeDuration(amount: 30, unit: .minutes),
                                     alarmHours: daytime)
        // 17:00 and 17:30, then the 18:00 reminder waits for 08:00 and nagging carries on from there.
        let reminders = plan.filter { $0.occurrence.scheduledFor == day.adding(.hour, 17) }.map(\.fireAt)
        let morning = day.adding(.day, 1).adding(.hour, 8)
        #expect(Array(reminders.prefix(4)) == [day.adding(.hour, 17), day.adding(.minute, 17 * 60 + 30), morning,
                                               morning.adding(.minute, 30)])
    }
}
