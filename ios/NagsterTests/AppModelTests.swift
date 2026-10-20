import Foundation
import NagsterCore
import SwiftData
import Testing
@testable import Nagster

@MainActor
struct AppModelTests {
    private func makeModel(container: ModelContainer) -> AppModel {
        AppModel(repository: AlarmRepository(container: container), defaults: UserDefaults(suiteName: "nagster-tests-\(UUID())")!)
    }

    @Test func actionsAreSavedAndQueuedForThePC() throws {
        let container = try AlarmRepository.makeContainer(inMemory: true)
        let model = makeModel(container: container)
        model.save(AlarmDefinition(title: "Water the plants", firstDue: WallTime(Date().addingTimeInterval(-60))))
        let ringing = try #require(model.ringing.first)
        model.complete(ringing)
        #expect(model.pendingOps.map(\.type) == [.upsertAlarm, .complete])

        let reloaded = AlarmRepository(container: container).load()
        #expect(reloaded.ops.map(\.type) == [.upsertAlarm, .complete])
        #expect(reloaded.state.occurrences[ringing.id]?.status == .completed)
        #expect(reloaded.state.definitions.values.first?.title == "Water the plants")
    }

    @Test func notificationButtonsFindProjectedOccurrences() throws {
        let model = makeModel(container: try AlarmRepository.makeContainer(inMemory: true))
        let tomorrow = WallTime(Date().addingTimeInterval(86_400))
        let alarm = AlarmDefinition(title: "Stretch", firstDue: tomorrow, repeatRule: RepeatRule(kind: .daily))
        model.save(alarm)
        let dayAfter = alarm.occurrence(at: tomorrow.adding(.day, 1)) // Never stored; only worked out.
        model.snooze(NotificationTarget(occurrence: dayAfter), for: SnoozeDuration(amount: 10, unit: .minutes))
        #expect(model.state.occurrences[dayAfter.id]?.status == .snoozed)
        #expect(model.pendingOps.last?.scheduledFor == dayAfter.scheduledFor)
    }

    @Test func notificationTargetsSurviveTheirTextForms() throws {
        let occurrence = AlarmDefinition(title: "x", firstDue: WallTime(year: 2026, month: 10, day: 20, hour: 9))
            .occurrence(at: WallTime(year: 2026, month: 10, day: 20, hour: 9))
        let target = NotificationTarget(occurrence: occurrence)
        #expect(NotificationTarget(encoded: target.encoded) == target)
        #expect(NotificationTarget(userInfo: target.userInfo) == target)
    }

    @Test func pcAddressesAreReadAsTyped() {
        #expect(SyncClient.baseURL(from: "192.168.1.20")?.absoluteString == "http://192.168.1.20:5972")
        #expect(SyncClient.baseURL(from: " my-pc:6000 ")?.absoluteString == "http://my-pc:6000")
        #expect(SyncClient.baseURL(from: "http://100.101.102.103:5972/")?.absoluteString == "http://100.101.102.103:5972")
        #expect(SyncClient.baseURL(from: "") == nil)
    }
}
