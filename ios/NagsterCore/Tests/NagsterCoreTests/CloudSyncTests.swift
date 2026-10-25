import CryptoKit
import Foundation
import Testing
@testable import NagsterCore

/// Cloud sync: the encryption against shared/cloud/crypto-vectors.json (the Windows app's checks read the same
/// file), the applier's rules an iPhone uses while no PC is running, and the shared copy.
struct CloudSyncTests {
    struct Vectors: Decodable {
        var userId, recordId, passphrase, salt, kek, dataKey, wrapNonce, wrappedKey, entryNonce, plaintext, payload: String
        var iterations: Int
    }

    static func vectors() throws -> Vectors {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        return try JSONDecoder().decode(Vectors.self, from: Data(contentsOf: root.appending(components: "shared", "cloud", "crypto-vectors.json")))
    }

    private func bytes(_ base64: String) throws -> Data { try #require(Data(base64Encoded: base64)) }

    @Test func encryptionMatchesThePC() throws {
        let v = try Self.vectors()
        let kek = CloudCrypto.deriveKey(passphrase: v.passphrase, salt: try bytes(v.salt), iterations: v.iterations)
        #expect(kek.withUnsafeBytes { Data($0) } == (try bytes(v.kek)))
        let wrapped = try CloudCrypto.seal(try bytes(v.dataKey), key: kek, context: CloudCrypto.vaultContext(v.userId),
                                           nonce: AES.GCM.Nonce(data: try bytes(v.wrapNonce)))
        #expect(wrapped == v.wrappedKey)
        let dataKey = try CloudCrypto.unwrap(v.wrappedKey, with: kek, userId: v.userId.uppercased())
        #expect(dataKey == SymmetricKey(data: try bytes(v.dataKey)))
        let context = CloudCrypto.recordContext(v.userId, try #require(UUID(uuidString: v.recordId)))
        let payload = try CloudCrypto.seal(CloudCrypto.padded(Data(v.plaintext.utf8)), key: dataKey, context: context,
                                           nonce: AES.GCM.Nonce(data: try bytes(v.entryNonce)))
        #expect(payload == v.payload)
        let opened = try CloudCrypto.open(v.payload, key: dataKey, context: context)
        #expect(String(decoding: opened, as: UTF8.self).trimmingCharacters(in: .whitespaces) == v.plaintext)

        let wrong = CloudCrypto.deriveKey(passphrase: "not my passphrase", salt: try bytes(v.salt), iterations: 1000)
        #expect(throws: CloudCryptoError.wrongPassphrase) { try CloudCrypto.unwrap(v.wrappedKey, with: wrong, userId: v.userId) }
        #expect(throws: CloudCryptoError.self) { try CloudCrypto.open(v.payload, key: dataKey, context: CloudCrypto.recordContext(v.userId, UUID())) }
    }

    // MARK: - The applier

    let alarm = AlarmDefinition(title: "Bins", firstDue: WallTime(year: 2026, month: 10, day: 26, hour: 9),
                                repeatRule: RepeatRule(kind: .daily))

    /// A copy holding the alarm and its first occurrence, both at seq 5.
    func copy() -> (CloudCopy, AlarmOccurrence) {
        var copy = CloudCopy()
        var definition = alarm
        definition.revision = 5
        var occurrence = alarm.occurrence(at: alarm.firstDue)
        occurrence.revision = 5
        copy.definitions[alarm.id] = definition
        copy.occurrences[occurrence.id] = occurrence
        return (copy, occurrence)
    }

    func op(_ type: SyncOpType, _ occurrence: AlarmOccurrence, base: Int64, until: WallTime? = nil) -> SyncOp {
        SyncOp(type: type, baseRevision: base, occurrenceId: occurrence.id, definitionId: occurrence.definitionId,
               scheduledFor: occurrence.scheduledFor, until: until)
    }

    @Test func answersApplyAndPublish() {
        var (copy, first) = copy()
        let outcome = CloudApplier.apply([op(.complete, first, base: 5)], to: &copy)
        #expect(outcome.results.map(\.applied) == [true] && copy.occurrences[first.id]?.status == .completed)
        #expect(outcome.records[first.id] == .occurrence(copy.occurrences[first.id]!, automatic: false))
    }

    @Test func aFinishedOccurrenceNeverReopensAndCompletedBeatsDismissed() {
        var (copy, first) = copy()
        _ = CloudApplier.apply([op(.dismiss, first, base: 5)], to: &copy)
        let later = CloudApplier.apply([op(.snooze, first, base: 5, until: first.alertAt.adding(.hour, 1)),
                                        op(.complete, first, base: 5)], to: &copy)
        #expect(later.results.map(\.applied) == [false, true] && later.results[0].message == "It was already dismissed.")
        #expect(copy.occurrences[first.id]?.status == .completed)
    }

    @Test func anActionThatLostARaceIsTurnedDown() {
        var (copy, first) = copy()
        copy.occurrences[first.id]?.revision = 9 // someone snoozed it since this device saw seq 5
        let outcome = CloudApplier.apply([op(.dismiss, first, base: 5)], to: &copy)
        #expect(outcome.results.first?.message == "It was answered on another device first." && outcome.records.isEmpty)
        // A change the app made by itself doesn't count.
        copy.automatic.insert(first.id)
        #expect(CloudApplier.apply([op(.dismiss, first, base: 5)], to: &copy).results.first?.applied == true)
    }

    @Test func aProjectedOccurrenceIsTakenOnAndOneOffTheScheduleIsNot() {
        var (copy, _) = copy()
        let third = alarm.occurrence(at: alarm.firstDue.adding(.day, 2))
        let outcome = CloudApplier.apply([op(.snooze, third, base: 0, until: third.alertAt.adding(.minute, 15))], to: &copy)
        #expect(outcome.results.first?.applied == true && copy.occurrences[third.id]?.status == .snoozed)
        var bogus = third
        bogus.id = UUID()
        #expect(CloudApplier.apply([op(.complete, bogus, base: 0)], to: &copy).results.first?.message == "That time isn't part of the alarm's schedule.")
    }

    @Test func aSnoozeCantPassTheDeadline() {
        var (copy, first) = copy()
        copy.occurrences[first.id]?.deadline = first.alertAt.adding(.minute, 30)
        let outcome = CloudApplier.apply([op(.snooze, first, base: 5, until: first.alertAt.adding(.hour, 1))], to: &copy)
        #expect(outcome.results.first?.applied == false && copy.occurrences[first.id]?.status == .pending)
    }

    @Test func alarmEditsFollowThePCsRules() {
        var (copy, first) = copy()
        // Wording only: the occurrence stays.
        var renamed = alarm
        renamed.title = "  Put the bins out  "
        var outcome = CloudApplier.apply([SyncOp(type: .upsertAlarm, baseRevision: 5, definition: renamed)], to: &copy)
        #expect(outcome.results.first?.applied == true && copy.definitions[alarm.id]?.title == "Put the bins out" && copy.occurrences[first.id] != nil)
        // A new time: waiting occurrences restart from it, and go out as deletions.
        var moved = alarm
        moved.firstDue = alarm.firstDue.adding(.hour, 1)
        outcome = CloudApplier.apply([SyncOp(type: .upsertAlarm, baseRevision: 5, definition: moved)], to: &copy)
        #expect(copy.occurrences[first.id] == nil && outcome.records[first.id] == .deletedOccurrence)
        // An edit against an older copy loses; so does one to a deleted alarm.
        copy.definitions[alarm.id]?.revision = 12
        #expect(CloudApplier.apply([SyncOp(type: .upsertAlarm, baseRevision: 5, definition: alarm)], to: &copy).results.first?.message
                == "That alarm was changed on another device first.")
        let gone = CloudApplier.apply([SyncOp(type: .deleteAlarm, baseRevision: 12, definitionId: alarm.id),
                                       SyncOp(type: .upsertAlarm, baseRevision: 0, definition: alarm)], to: &copy)
        #expect(gone.results.map(\.applied) == [true, false] && gone.records[alarm.id] == .deletedAlarm)
        // Invalid input is refused in the PC's words.
        var blank = AlarmDefinition(title: "   ", firstDue: alarm.firstDue)
        blank.id = UUID()
        #expect(CloudApplier.apply([SyncOp(type: .upsertAlarm, baseRevision: 0, definition: blank)], to: &copy).results.first?.message
                == "Enter a title (up to 160 characters).")
    }

    @Test func theCopyTakesInRecordsAndTombstones() {
        var copy = CloudCopy()
        let occurrence = alarm.occurrence(at: alarm.firstDue)
        var changes = copy.take([(alarm.id, 3, .alarm(alarm)), (occurrence.id, 4, .occurrence(occurrence, automatic: true)),
                                 (UUID(), 5, .settings(PhoneSettings(snoozeChoices: ["5m"])))])
        #expect(changes.definitions.first?.revision == 3 && changes.occurrences.first?.revision == 4 && copy.automatic.contains(occurrence.id))
        #expect(copy.cursor == 5 && copy.settings?.snoozeChoices == ["5m"])
        changes = copy.take([(alarm.id, 6, .deletedAlarm)])
        #expect(copy.definitions.isEmpty && copy.occurrences.isEmpty && changes.tombstones.map(\.kind) == [SyncTombstone.alarmKind])
        #expect(copy.deletedAlarms.contains(alarm.id))
    }

    @Test func recordsSealWithoutRevisionsAndOpenAgain() throws {
        let key = CloudCrypto.newDataKey()
        var stamped = alarm
        stamped.revision = 42
        let row = CloudRow(id: alarm.id, kind: "alarm", seq: 7, deleted: false,
                           payload: try CloudRecord.alarm(stamped).seal(id: alarm.id, key: key, userId: "u"))
        #expect(row.payload?.contains("Bins") == false)
        #expect(try CloudRecord.open(row, key: key, userId: "u") == .alarm(alarm))
        #expect(throws: CloudCryptoError.self) { try CloudRecord.open(row, key: key, userId: "someone else") }
    }

    // MARK: - The iPhones' settings

    @Test func settingsFollowTheNewestChangeButAJoiningDeviceTakesTheShared() {
        let mine = IPhoneSettings(nagInterval: "5m", naggingEnabled: true)
        let shared = IPhoneSettings(nagInterval: "15m")
        #expect(SettingsSync.step(local: mine, remote: nil, seenSeq: nil, seenDigest: nil) == .push)
        #expect(SettingsSync.step(local: mine, remote: (5, shared), seenSeq: nil, seenDigest: nil)
                == .apply(IPhoneSettings(nagInterval: "15m", naggingEnabled: true), seq: 5))
        #expect(SettingsSync.step(local: mine, remote: (5, shared), seenSeq: 5, seenDigest: mine.digest) == .none)
        #expect(SettingsSync.step(local: mine, remote: (9, shared), seenSeq: 5, seenDigest: mine.digest)
                == .apply(IPhoneSettings(nagInterval: "15m", naggingEnabled: true), seq: 9))
        #expect(SettingsSync.step(local: IPhoneSettings(nagInterval: "1m"), remote: (9, shared), seenSeq: 5, seenDigest: mine.digest) == .push)
        let a = UUID(), b = UUID()
        #expect(IPhoneSettings(noNagAlarms: [a, b]).digest == IPhoneSettings(noNagAlarms: [b, a]).digest)
    }
}
