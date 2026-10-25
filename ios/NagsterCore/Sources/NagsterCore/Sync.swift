import Foundation

// The records exchanged with the PC through POST /api/sync, mirroring Web/SyncProtocol.cs in the Windows app.
// Names are camelCase and times are wall-clock text ("yyyy-MM-ddTHH:mm:ss").

public enum SyncOpType: String, Codable, Sendable {
    case complete, dismiss, snooze, reschedule, upsertAlarm, deleteAlarm
}

/// Something the user did on the phone, queued until the PC acknowledges it.
public struct SyncOp: Codable, Hashable, Sendable, Identifiable {
    public var opId: UUID
    public var type: SyncOpType
    /// The record's PC revision when the phone acted, so the PC can tell whether someone acted there since.
    public var baseRevision: Int64
    public var occurrenceId: UUID?
    public var definitionId: UUID?
    /// For an occurrence the PC may not have stored yet, so it can create it.
    public var scheduledFor: WallTime?
    /// Snooze or reschedule target.
    public var until: WallTime?
    public var definition: AlarmDefinition?

    public var id: UUID { opId }

    public init(opId: UUID = UUID(), type: SyncOpType, baseRevision: Int64, occurrenceId: UUID? = nil, definitionId: UUID? = nil,
                scheduledFor: WallTime? = nil, until: WallTime? = nil, definition: AlarmDefinition? = nil) {
        self.opId = opId
        self.type = type
        self.baseRevision = baseRevision
        self.occurrenceId = occurrenceId
        self.definitionId = definitionId
        self.scheduledFor = scheduledFor
        self.until = until
        self.definition = definition
    }
}

public struct SyncRequest: Codable, Hashable, Sendable {
    public var deviceId: String
    public var sinceRevision: Int64
    public var ops: [SyncOp]

    public init(deviceId: String, sinceRevision: Int64, ops: [SyncOp]) {
        self.deviceId = deviceId
        self.sinceRevision = sinceRevision
        self.ops = ops
    }
}

public struct SyncTombstone: Codable, Hashable, Sendable {
    public static let alarmKind = "Alarm"
    public static let occurrenceKind = "Occurrence"

    public var id: UUID
    public var kind: String
    public var revision: Int64

    public init(id: UUID, kind: String, revision: Int64) {
        self.id = id
        self.kind = kind
        self.revision = revision
    }
}

public struct SyncOpResult: Codable, Hashable, Sendable {
    public var opId: UUID
    public var applied: Bool
    public var message: String?

    public init(opId: UUID, applied: Bool, message: String? = nil) {
        self.opId = opId
        self.applied = applied
        self.message = message
    }
}

public struct SyncResponse: Codable, Hashable, Sendable {
    public var revision: Int64
    /// When set, the reply is everything the PC has (with recent history) and replaces the phone's copy.
    public var full: Bool
    public var definitions: [AlarmDefinition]
    public var occurrences: [AlarmOccurrence]
    public var tombstones: [SyncTombstone]
    public var settings: PhoneSettings
    public var results: [SyncOpResult]

    public init(revision: Int64, full: Bool, definitions: [AlarmDefinition], occurrences: [AlarmOccurrence], tombstones: [SyncTombstone],
                settings: PhoneSettings, results: [SyncOpResult]) {
        self.revision = revision
        self.full = full
        self.definitions = definitions
        self.occurrences = occurrences
        self.tombstones = tombstones
        self.settings = settings
        self.results = results
    }
}
