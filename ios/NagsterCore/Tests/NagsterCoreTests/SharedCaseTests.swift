import Foundation
import Testing
@testable import NagsterCore

/// Checks against the files in the repository's shared folder, which the Windows app's checks also use.
/// They guarantee both apps compute the same repeat times and occurrence IDs and read each other's JSON.
struct SharedCaseTests {
    static func shared(_ name: String) throws -> Data {
        // Tests/NagsterCoreTests/SharedCaseTests.swift -> repository root.
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        return try Data(contentsOf: root.appending(components: "shared", name))
    }

    struct ParityCases: Decodable {
        struct Recurrence: Decodable {
            var name: String
            var rule: RepeatRule
            var anchor: WallTime
            var after: WallTime
            var expected: WallTime?
        }
        struct ID: Decodable {
            var definitionId: UUID
            var scheduledFor: WallTime
            var expected: UUID
        }
        var recurrence: [Recurrence]
        var occurrenceIds: [ID]
    }

    @Test func repeatTimesMatchTheWindowsApp() throws {
        let cases = try JSONDecoder().decode(ParityCases.self, from: Self.shared("parity-cases.json"))
        #expect(!cases.recurrence.isEmpty)
        for item in cases.recurrence {
            #expect(item.rule.next(anchor: item.anchor, after: item.after) == item.expected, "\(item.name)")
        }
    }

    @Test func occurrenceIDsMatchTheWindowsApp() throws {
        let cases = try JSONDecoder().decode(ParityCases.self, from: Self.shared("parity-cases.json"))
        for item in cases.occurrenceIds {
            #expect(OccurrenceID.make(definitionID: item.definitionId, scheduledFor: item.scheduledFor) == item.expected)
        }
    }

    @Test func readsTheReplyThePCSends() throws {
        let reply = try JSONDecoder().decode(SyncResponse.self, from: Self.shared("sync-response-sample.json"))
        #expect(reply.full && reply.revision == 4)
        let tablets = try #require(reply.definitions.first { $0.title == "Take tablets" })
        #expect(tablets.repeatRule.kind == .daily && tablets.deadlineAfterMinutes == 120 && tablets.repeatRule.until == nil)
        let dentist = try #require(reply.definitions.first { $0.title == "Dentist" })
        #expect(dentist.devices == .phone && dentist.important && dentist.eventEnd == WallTime(year: 2027, month: 1, day: 1, hour: 1))
        // The phone derives exactly the occurrences the PC stored.
        for occurrence in reply.occurrences {
            let definition = try #require(reply.definitions.first { $0.id == occurrence.definitionId })
            let derived = definition.occurrence(at: occurrence.scheduledFor)
            #expect(derived.id == occurrence.id && derived.alertAt == occurrence.alertAt && derived.deadline == occurrence.deadline)
        }
        #expect(reply.settings.snoozeDurations.map(\.description) == ["5m", "15m", "30m", "1h", "1d"])
    }

    @Test func writesTheRequestThePCExpects() throws {
        let sample = try JSONSerialization.jsonObject(with: Self.shared("sync-request-sample.json")) as? NSDictionary
        let definition = AlarmDefinition(
            id: UUID(uuidString: "3F2E1D0C-BA98-4765-8321-0FEDCBA98765")!, title: "Call the dentist",
            firstDue: WallTime(year: 2026, month: 10, day: 21, hour: 10), repeatRule: RepeatRule(kind: .weekly, weekdays: 62),
            devices: .phone, important: true)
        let request = SyncRequest(deviceId: "sample-iphone", sinceRevision: 4, ops: [
            SyncOp(opId: UUID(uuidString: "2B1E5C1A-7D3F-4A8B-9C0D-1E2F3A4B5C6D")!, type: .snooze, baseRevision: 2,
                   occurrenceId: UUID(uuidString: "4D8F2C94-0D18-5097-AC64-76E3CFC0EA94")!,
                   definitionId: UUID(uuidString: "6F1C2A8E-4B7D-4E0F-9A3C-2D5E8F1B7C40")!,
                   scheduledFor: WallTime(year: 2026, month: 10, day: 20, hour: 9),
                   until: WallTime(year: 2026, month: 10, day: 20, hour: 9, minute: 15)),
            SyncOp(opId: UUID(uuidString: "8C7B6A59-4837-4261-A5F4-E3D2C1B0A998")!, type: .upsertAlarm, baseRevision: 0, definition: definition)
        ])
        let encoded = try JSONSerialization.jsonObject(with: JSONEncoder().encode(request)) as? NSDictionary
        #expect(encoded == sample)
    }
}
