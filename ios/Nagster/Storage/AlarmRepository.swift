import Foundation
import NagsterCore
import OSLog
import SwiftData

/// Loads and saves the phone's copy of the alarms and its queue of unsent actions in SwiftData.
final class AlarmRepository {
    static let schema = Schema([StoredDefinition.self, StoredOccurrence.self, StoredOp.self, StoredSyncInfo.self])

    private let context: ModelContext
    private let encoder = JSONEncoder()
    private let decoder = JSONDecoder()
    private let log = Logger(subsystem: "com.dgutilities.nagster", category: "storage")

    init(container: ModelContainer) {
        context = ModelContext(container)
        context.autosaveEnabled = false
    }

    /// A store on disk for the app, or in memory for tests and previews.
    static func makeContainer(inMemory: Bool = false) throws -> ModelContainer {
        try ModelContainer(for: schema, configurations: ModelConfiguration(isStoredInMemoryOnly: inMemory))
    }

    func load() -> (state: AlarmState, ops: [SyncOp]) {
        var state = AlarmState()
        do {
            for row in try context.fetch(FetchDescriptor<StoredDefinition>()) {
                if let definition = decode(AlarmDefinition.self, row.json) { state.definitions[definition.id] = definition }
            }
            for row in try context.fetch(FetchDescriptor<StoredOccurrence>()) {
                if let occurrence = decode(AlarmOccurrence.self, row.json) { state.occurrences[occurrence.id] = occurrence }
            }
            if let info = try context.fetch(FetchDescriptor<StoredSyncInfo>()).first {
                state.revision = info.revision
                state.settings = decode(PhoneSettings.self, info.settingsJSON) ?? PhoneSettings()
            }
            let ops = try context.fetch(FetchDescriptor<StoredOp>(sortBy: [SortDescriptor(\.sequence)]))
                .compactMap { decode(SyncOp.self, $0.json) }
            return (state, ops)
        } catch {
            log.error("Couldn't load alarms: \(error.localizedDescription, privacy: .public)")
            return (state, [])
        }
    }

    /// Replaces what's stored with `state` and `ops`. A few hundred small rows, so a full rewrite is simplest.
    func save(_ state: AlarmState, ops: [SyncOp]) {
        do {
            try context.delete(model: StoredDefinition.self)
            try context.delete(model: StoredOccurrence.self)
            try context.delete(model: StoredOp.self)
            try context.delete(model: StoredSyncInfo.self)
            for definition in state.definitions.values {
                context.insert(StoredDefinition(id: definition.id, json: try encoder.encode(definition)))
            }
            for occurrence in state.occurrences.values {
                context.insert(StoredOccurrence(id: occurrence.id, json: try encoder.encode(occurrence)))
            }
            for (sequence, op) in ops.enumerated() {
                context.insert(StoredOp(opId: op.opId, sequence: sequence, json: try encoder.encode(op)))
            }
            context.insert(StoredSyncInfo(revision: state.revision, settingsJSON: try encoder.encode(state.settings)))
            try context.save()
        } catch {
            log.error("Couldn't save alarms: \(error.localizedDescription, privacy: .public)")
            context.rollback()
        }
    }

    private func decode<T: Decodable>(_ type: T.Type, _ data: Data) -> T? {
        do { return try decoder.decode(type, from: data) } catch {
            log.error("Skipping an unreadable \(String(describing: type), privacy: .public): \(error.localizedDescription, privacy: .public)")
            return nil
        }
    }
}
