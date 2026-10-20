import Foundation
import SwiftData

// SwiftData rows. Each holds a NagsterCore value as JSON, keyed by its ID: the core types are already the sync
// format, and wall-clock times and repeat rules round-trip exactly that way.

@Model
final class StoredDefinition {
    @Attribute(.unique) var id: UUID
    var json: Data

    init(id: UUID, json: Data) {
        self.id = id
        self.json = json
    }
}

@Model
final class StoredOccurrence {
    @Attribute(.unique) var id: UUID
    var json: Data

    init(id: UUID, json: Data) {
        self.id = id
        self.json = json
    }
}

/// An action not yet confirmed by the PC; `sequence` keeps them in the order the user took them.
@Model
final class StoredOp {
    @Attribute(.unique) var opId: UUID
    var sequence: Int
    var json: Data

    init(opId: UUID, sequence: Int, json: Data) {
        self.opId = opId
        self.sequence = sequence
        self.json = json
    }
}

/// The single row of sync bookkeeping: the newest PC revision seen and the PC's settings.
@Model
final class StoredSyncInfo {
    var revision: Int64
    var settingsJSON: Data

    init(revision: Int64, settingsJSON: Data) {
        self.revision = revision
        self.settingsJSON = settingsJSON
    }
}
