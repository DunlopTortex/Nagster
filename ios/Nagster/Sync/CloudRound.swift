import CryptoKit
import Foundation
import NagsterCore

/// One cloud sync from this iPhone, answering in the form the PC's network sync does, so the app merges it the
/// same way: sends the actions waiting here to the cloud's queue, pulls what changed in the shared copy, and, when
/// no PC is running, takes the applier role for a moment to apply everyone's waiting actions itself. Then collects
/// the answers to this device's actions; ones not answered yet (a PC applies them every few seconds) stay waiting.
struct CloudRound {
    var client: CloudClient
    var session: CloudSession
    var key: SymmetricKey

    func run(_ ops: [SyncOp], book: inout CloudBook) async throws -> SyncResponse {
        let userId = book.userId
        let device = book.deviceId
        try await client.submit(session, try ops.map {
            CloudOpRow(id: $0.opId, deviceId: device, payload: try CloudCrypto.seal($0, key: key, context: CloudCrypto.opContext(userId, $0.opId)))
        })

        var changed = Changes()
        try await pull(into: &book, &changed)
        if try await client.claim(session, device: device) {
            do {
                try await applyWaiting(&book)
                try await pull(into: &book, &changed)
            } catch CloudError.notApplier {
                // A PC took over meanwhile: it applies them instead.
            } catch {
                await client.release(session, device: device)
                throw error
            }
            await client.release(session, device: device)
        }

        let answered = try await client.answered(session, device: device)
        let results = answered.compactMap { row in
            row.result.flatMap { try? CloudCrypto.open(SyncOpResult.self, $0, key: key, context: CloudCrypto.resultContext(userId, row.id)) }
        }
        try await client.forget(session, ops: answered.map(\.id))

        let full = book.joining
        book.joining = false
        let everything = book.copy.everything
        return SyncResponse(revision: book.copy.cursor, full: full,
                            definitions: full ? everything.definitions : Array(changed.definitions.values),
                            occurrences: full ? everything.occurrences : Array(changed.occurrences.values),
                            tombstones: full ? [] : changed.tombstones,
                            settings: book.copy.settings ?? PhoneSettings(), results: results)
    }

    /// What changed in the shared copy during this round, newest wins.
    private struct Changes {
        var definitions: [UUID: AlarmDefinition] = [:]
        var occurrences: [UUID: AlarmOccurrence] = [:]
        var tombstones: [SyncTombstone] = []
    }

    private func pull(into book: inout CloudBook, _ changed: inout Changes) async throws {
        while true {
            let page = try await client.records(session, since: book.copy.cursor)
            guard !page.isEmpty else { return }
            let opened = try page.map { (id: $0.id, seq: $0.seq, record: try CloudRecord.open($0, key: key, userId: book.userId)) }
            let taken = book.copy.take(opened)
            for definition in taken.definitions { changed.definitions[definition.id] = definition }
            for occurrence in taken.occurrences { changed.occurrences[occurrence.id] = occurrence }
            for tombstone in taken.tombstones {
                changed.definitions[tombstone.id] = nil
                changed.occurrences[tombstone.id] = nil
                if tombstone.kind == SyncTombstone.alarmKind {
                    changed.occurrences = changed.occurrences.filter { $0.value.definitionId != tombstone.id }
                }
            }
            changed.tombstones += taken.tombstones
            if page.count < CloudClient.pageSize { return }
        }
    }

    /// As applier: applies every device's waiting actions with the PC's rules and publishes the result.
    private func applyWaiting(_ book: inout CloudBook) async throws {
        let userId = book.userId
        let waiting = try await client.waitingOps(session)
        guard !waiting.isEmpty else { return }
        var ops: [SyncOp] = []
        var answers: [SyncOpResult] = []
        for row in waiting {
            if var op = try? CloudCrypto.open(SyncOp.self, row.payload, key: key, context: CloudCrypto.opContext(userId, row.id)) {
                op.opId = row.id
                ops.append(op)
            } else {
                answers.append(SyncOpResult(opId: row.id, applied: false, message: "This action couldn't be read."))
            }
        }
        // On a scratch copy: what's published comes back on the next pull, numbered.
        var scratch = book.copy
        let outcome = CloudApplier.apply(ops, to: &scratch)
        answers += outcome.results
        let records: [[String: Any]] = try outcome.records.map { id, record in
            var row: [String: Any] = ["id": id.uuidString.lowercased(), "kind": record.kind, "deleted": record.isDeleted]
            if let payload = try record.seal(id: id, key: key, userId: userId) { row["payload"] = payload }
            return row
        }
        let sealedAnswers: [[String: Any]] = try answers.map {
            ["id": $0.opId.uuidString.lowercased(), "result": try CloudCrypto.seal($0, key: key, context: CloudCrypto.resultContext(userId, $0.opId))]
        }
        try await client.publish(session, device: book.deviceId, records: records, answers: sealedAnswers)
    }
}
