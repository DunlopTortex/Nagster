import Foundation
import Testing
@testable import NagsterCore

struct AlarmStateTests {
    let nine = WallTime(year: 2026, month: 10, day: 20, hour: 9)

    func state(with definitions: AlarmDefinition...) -> AlarmState {
        var state = AlarmState()
        for definition in definitions { state.definitions[definition.id] = definition }
        return state
    }

    @Test func projectsAnAlarmAddedOnThePhone() {
        let daily = AlarmDefinition(title: "Stretch", firstDue: nine, repeatRule: RepeatRule(kind: .daily))
        let upcoming = state(with: daily).waitingOccurrences(through: nine.adding(.day, 2))
        #expect(upcoming.map(\.scheduledFor) == [nine, nine.adding(.day, 1), nine.adding(.day, 2)])
        #expect(upcoming.first?.id == OccurrenceID.make(definitionID: daily.id, scheduledFor: nine))
    }

    @Test func continuesAfterWhatThePCStored() {
        let daily = AlarmDefinition(title: "Stretch", firstDue: nine, repeatRule: RepeatRule(kind: .daily))
        var state = state(with: daily)
        var stored = daily.occurrence(at: nine.adding(.day, 1))
        stored.revision = 5
        state.occurrences[stored.id] = stored
        let times = state.waitingOccurrences(through: nine.adding(.day, 3)).map(\.scheduledFor)
        // Nothing before the stored one is invented; the series carries on after it.
        #expect(times == [nine.adding(.day, 1), nine.adding(.day, 2), nine.adding(.day, 3)])
    }

    @Test func honoursStopAfterACount() {
        let three = AlarmDefinition(title: "Course", firstDue: nine, repeatRule: RepeatRule(kind: .daily, count: 3))
        #expect(state(with: three).waitingOccurrences(through: nine.adding(.day, 10)).count == 3)
    }

    @Test func onlyTheLatestOfSeveralDueOccurrencesRings() {
        let hourly = AlarmDefinition(title: "Water", firstDue: nine, repeatRule: RepeatRule(kind: .hours))
        let desktopOnly = AlarmDefinition(title: "PC thing", firstDue: nine, devices: .desktop)
        let ringing = state(with: hourly, desktopOnly).ringing(at: nine.adding(.hour, 3))
        #expect(ringing.map(\.scheduledFor) == [nine.adding(.hour, 3)])
    }

    @Test func snoozeStopsAtTheDeadline() {
        let fifteen = SnoozeDuration(amount: 15, unit: .minutes)
        #expect(AlarmState.snoozeTarget(fifteen, from: nine, deadline: nil) == nine.adding(.minute, 15))
        #expect(AlarmState.snoozeTarget(fifteen, from: nine, deadline: nine.adding(.minute, 10)) == nine.adding(.minute, 10))
        #expect(AlarmState.snoozeTarget(fifteen, from: nine, deadline: nine) == nil)
    }

    @Test func actingOnAProjectedOccurrenceStoresIt() {
        let daily = AlarmDefinition(title: "Stretch", firstDue: nine, repeatRule: RepeatRule(kind: .daily))
        var state = state(with: daily)
        let tomorrow = state.waitingOccurrences(through: nine.adding(.day, 1))[1]
        let op = state.act(.snooze, on: tomorrow, until: tomorrow.alertAt.adding(.minute, 30), now: nine)
        #expect(op.baseRevision == 0 && op.definitionId == daily.id && op.scheduledFor == tomorrow.scheduledFor)
        let stored = state.occurrences[tomorrow.id]
        #expect(stored?.status == .snoozed && stored?.snoozeCount == 1 && stored?.alertAt == tomorrow.alertAt.adding(.minute, 30))
    }

    @Test func completedNeverReopens() {
        let once = AlarmDefinition(title: "Post", firstDue: nine)
        var state = state(with: once)
        let occurrence = once.occurrence(at: nine)
        _ = state.act(.complete, on: occurrence, now: nine)
        _ = state.act(.snooze, on: occurrence, until: nine.adding(.hour, 1), now: nine)
        _ = state.act(.dismiss, on: occurrence, now: nine)
        #expect(state.occurrences[occurrence.id]?.status == .completed)
    }

    @Test func changingTheScheduleRestartsWaitingOccurrences() {
        var daily = AlarmDefinition(title: "Stretch", firstDue: nine, repeatRule: RepeatRule(kind: .daily))
        var state = state(with: daily)
        let first = daily.occurrence(at: nine)
        _ = state.act(.snooze, on: first, until: nine.adding(.minute, 5), now: nine)
        daily.title = "Stretch properly"
        _ = state.save(daily)
        #expect(state.occurrences[first.id]?.status == .snoozed)
        daily.firstDue = nine.adding(.hour, 1)
        _ = state.save(daily)
        #expect(state.occurrences[first.id] == nil)
        #expect(state.waitingOccurrences(through: nine.adding(.hour, 1)).map(\.scheduledFor) == [nine.adding(.hour, 1)])
    }

    @Test func mergeAppliesChangesAndKeepsUnsentActions() {
        let daily = AlarmDefinition(title: "Stretch", firstDue: nine, repeatRule: RepeatRule(kind: .daily), revision: 3)
        let gone = AlarmDefinition(title: "Old", firstDue: nine, revision: 1)
        var state = state(with: daily, gone)
        state.occurrences[gone.occurrence(at: nine).id] = gone.occurrence(at: nine)
        let pending = SyncOp(type: .complete, baseRevision: 0, occurrenceId: daily.occurrence(at: nine).id,
                             definitionId: daily.id, scheduledFor: nine)
        var renamed = daily
        renamed.title = "Stretch (PC)"
        renamed.revision = 9
        let reply = SyncResponse(revision: 9, full: false, definitions: [renamed], occurrences: [],
                                 tombstones: [SyncTombstone(id: gone.id, kind: SyncTombstone.alarmKind, revision: 8)],
                                 settings: PhoneSettings(snoozeChoices: ["10m"]), results: [])
        state.merge(reply, unsent: [pending], now: nine)
        #expect(state.revision == 9 && state.definitions[daily.id]?.title == "Stretch (PC)" && state.definitions[gone.id] == nil)
        #expect(state.occurrences.values.allSatisfy { $0.definitionId != gone.id })
        #expect(state.occurrences[daily.occurrence(at: nine).id]?.status == .completed)
        #expect(state.settings.snoozeChoices == ["10m"])
    }

    @Test func aFullReplyReplacesEverything() {
        var state = state(with: AlarmDefinition(title: "Local only", firstDue: nine))
        let fromPC = AlarmDefinition(title: "From PC", firstDue: nine, revision: 2)
        state.merge(SyncResponse(revision: 2, full: true, definitions: [fromPC], occurrences: [fromPC.occurrence(at: nine)],
                                 tombstones: [], settings: PhoneSettings(), results: []), unsent: [], now: nine)
        #expect(state.definitions.values.map(\.title) == ["From PC"] && state.occurrences.count == 1)
    }
}
