import CryptoKit
import Foundation

// Cloud sync through Supabase, end-to-end encrypted (see CloudCrypto). The cloud holds the shared copy (alarms,
// occurrences and the PC's settings) and a queue of actions. One device at a time, the applier, applies the actions
// and publishes the changes: the PC whenever it's running, otherwise whichever iPhone syncs. The records here mirror
// shared/cloud/schema.sql, and the applier's rules mirror Web/SyncApi.cs in the Windows app.

/// One record of the shared copy as pulled: sealed, or with no payload once deleted.
public struct CloudRow: Codable, Hashable, Sendable {
    public var id: UUID
    public var kind: String
    public var seq: Int64
    public var deleted: Bool
    public var payload: String?

    public init(id: UUID, kind: String, seq: Int64, deleted: Bool, payload: String?) {
        self.id = id
        self.kind = kind
        self.seq = seq
        self.deleted = deleted
        self.payload = payload
    }
}

/// An occurrence as the cloud holds it: the network sync's form, plus whether the app (not a person) last changed it.
public struct CloudOccurrence: Codable, Hashable, Sendable {
    public var occurrence: AlarmOccurrence
    public var automatic: Bool

    public init(occurrence: AlarmOccurrence, automatic: Bool) {
        self.occurrence = occurrence
        self.automatic = automatic
    }
}

/// A record of the shared copy, opened.
public enum CloudRecord: Hashable, Sendable {
    case alarm(AlarmDefinition)
    case occurrence(AlarmOccurrence, automatic: Bool)
    case settings(PhoneSettings)
    case deletedAlarm
    case deletedOccurrence

    public var kind: String {
        switch self {
        case .alarm, .deletedAlarm: "alarm"
        case .occurrence, .deletedOccurrence: "occurrence"
        case .settings: "settings"
        }
    }

    public var isDeleted: Bool { self == .deletedAlarm || self == .deletedOccurrence }

    /// Opens a pulled row with the data key.
    public static func open(_ row: CloudRow, key: SymmetricKey, userId: String) throws -> CloudRecord {
        guard let payload = row.payload, !row.deleted else { return row.kind == "alarm" ? .deletedAlarm : .deletedOccurrence }
        let context = CloudCrypto.recordContext(userId, row.id)
        switch row.kind {
        case "alarm": return .alarm(try CloudCrypto.open(AlarmDefinition.self, payload, key: key, context: context))
        case "occurrence":
            let sealed = try CloudCrypto.open(CloudOccurrence.self, payload, key: key, context: context)
            return .occurrence(sealed.occurrence, automatic: sealed.automatic)
        default: return .settings(try CloudCrypto.open(PhoneSettings.self, payload, key: key, context: context))
        }
    }

    /// Sealed for publishing; nil for a deletion.
    public func seal(id: UUID, key: SymmetricKey, userId: String) throws -> String? {
        let context = CloudCrypto.recordContext(userId, id)
        switch self {
        case .alarm(var definition):
            definition.revision = 0
            return try CloudCrypto.seal(definition, key: key, context: context)
        case .occurrence(var occurrence, let automatic):
            occurrence.revision = 0
            return try CloudCrypto.seal(CloudOccurrence(occurrence: occurrence, automatic: automatic), key: key, context: context)
        case .settings(let settings): return try CloudCrypto.seal(settings, key: key, context: context)
        case .deletedAlarm, .deletedOccurrence: return nil
        }
    }
}

/// The shared copy as this device last pulled it. Each record's revision is its cloud seq, which is what actions
/// name as their base.
public struct CloudCopy: Codable, Hashable, Sendable {
    public var definitions: [UUID: AlarmDefinition] = [:]
    public var occurrences: [UUID: AlarmOccurrence] = [:]
    /// Occurrences last changed by the app rather than a person, so an action made before still applies.
    public var automatic: Set<UUID> = []
    /// Alarms deleted, so an older edit doesn't bring one back.
    public var deletedAlarms: Set<UUID> = []
    /// The PC's settings, once a PC has published them.
    public var settings: PhoneSettings?
    /// The highest seq pulled so far.
    public var cursor: Int64 = 0

    public init() {}

    /// Takes in pulled records, and returns the changes in the form the network sync's reply carries them.
    public mutating func take(_ rows: [(id: UUID, seq: Int64, record: CloudRecord)])
        -> (definitions: [AlarmDefinition], occurrences: [AlarmOccurrence], tombstones: [SyncTombstone]) {
        var definitions: [UUID: AlarmDefinition] = [:]
        var occurrences: [UUID: AlarmOccurrence] = [:]
        var tombstones: [SyncTombstone] = []
        for (id, seq, record) in rows {
            cursor = max(cursor, seq)
            switch record {
            case .alarm(var definition):
                definition.id = id
                definition.revision = seq
                self.definitions[id] = definition
                deletedAlarms.remove(id)
                definitions[id] = definition
            case .occurrence(var occurrence, let isAutomatic):
                occurrence.id = id
                occurrence.revision = seq
                self.occurrences[id] = occurrence
                if isAutomatic { automatic.insert(id) } else { automatic.remove(id) }
                occurrences[id] = occurrence
            case .settings(let settings):
                self.settings = settings
            case .deletedAlarm:
                self.definitions[id] = nil
                deletedAlarms.insert(id)
                for other in self.occurrences.values where other.definitionId == id { self.occurrences[other.id] = nil }
                definitions[id] = nil
                tombstones.append(SyncTombstone(id: id, kind: SyncTombstone.alarmKind, revision: seq))
            case .deletedOccurrence:
                self.occurrences[id] = nil
                automatic.remove(id)
                occurrences[id] = nil
                tombstones.append(SyncTombstone(id: id, kind: SyncTombstone.occurrenceKind, revision: seq))
            }
        }
        return (Array(definitions.values), Array(occurrences.values), tombstones)
    }

    /// Everything, as a full reply replacing the phone's view: for its first cloud sync.
    public var everything: (definitions: [AlarmDefinition], occurrences: [AlarmOccurrence]) {
        (Array(definitions.values), occurrences.values.filter { definitions[$0.definitionId] != nil })
    }
}

/// Applies actions to the shared copy with the PC's rules (Web/SyncApi.cs), for an iPhone holding the applier role
/// while no PC is running. Conflicts are settled by revision and by how final an answer is, never by clocks:
/// Completed beats Dismissed, which beats Missed, which beats anything still waiting, and a finished occurrence
/// never reopens. Otherwise an action wins unless someone acted on the record since its device last saw it;
/// changes the app made by itself don't count.
public enum CloudApplier {
    public static let missedAction = "Missed: replaced by a later reminder"

    public struct Outcome: Sendable {
        /// The records to publish, by ID.
        public var records: [UUID: CloudRecord] = [:]
        public var results: [SyncOpResult] = []
    }

    public static func apply(_ ops: [SyncOp], to copy: inout CloudCopy) -> Outcome {
        var outcome = Outcome()
        for op in ops {
            let problem: String? = switch op.type {
            case .upsertAlarm: upsert(op, &copy, &outcome)
            case .deleteAlarm: delete(op, &copy, &outcome)
            case .complete, .dismiss, .snooze, .reschedule: answer(op, &copy, &outcome)
            }
            outcome.results.append(SyncOpResult(opId: op.opId, applied: problem == nil, message: problem))
        }
        return outcome
    }

    private static func answer(_ op: SyncOp, _ copy: inout CloudCopy, _ outcome: inout Outcome) -> String? {
        guard let id = op.occurrenceId else { return "No alarm was given." }
        var occurrence: AlarmOccurrence
        if let stored = copy.occurrences[id] {
            occurrence = stored
        } else {
            // Worked out ahead from the alarm's rule, by this device or the one that acted.
            guard let definitionID = op.definitionId, let definition = copy.definitions[definitionID] else { return "That alarm has been deleted." }
            guard let scheduledFor = op.scheduledFor else { return "That alarm time isn't valid." }
            occurrence = definition.occurrence(at: scheduledFor)
            guard occurrence.id == id else { return "That time isn't part of the alarm's schedule." }
            copy.automatic.insert(id)
        }

        if occurrence.status == .completed { return "It was already completed." }
        if occurrence.status == .dismissed {
            let missed = occurrence.lastAction == missedAction
            if !(op.type == .complete || (op.type == .dismiss && missed)) { return "It was already dismissed." }
        } else if occurrence.revision > op.baseRevision && !copy.automatic.contains(id) {
            return "It was answered on another device first."
        }

        switch op.type {
        case .complete:
            occurrence.status = .completed
            occurrence.lastAction = "Completed"
        case .dismiss:
            occurrence.status = .dismissed
            occurrence.lastAction = "Dismissed without completion"
        case .snooze, .reschedule:
            guard let until = op.until else { return "No snooze time was given." }
            if let deadline = occurrence.deadline, until > deadline {
                return "This alarm must be completed by \(deadline.iso). Choose a time no later than that."
            }
            occurrence.alertAt = until
            occurrence.status = .snoozed
            if op.type == .snooze {
                occurrence.snoozeCount += 1
                occurrence.lastAction = "Snoozed until \(until.iso)"
            } else {
                occurrence.lastAction = "Rescheduled to \(until.iso)"
            }
        case .upsertAlarm, .deleteAlarm:
            return nil
        }
        copy.occurrences[id] = occurrence
        copy.automatic.remove(id)
        outcome.records[id] = .occurrence(occurrence, automatic: false)
        return nil
    }

    private static func upsert(_ op: SyncOp, _ copy: inout CloudCopy, _ outcome: inout Outcome) -> String? {
        guard var incoming = op.definition else { return "No alarm was given." }
        let existing = copy.definitions[incoming.id]
        if existing == nil && copy.deletedAlarms.contains(incoming.id) { return "That alarm was deleted on another device." }
        if let existing, existing.revision > op.baseRevision { return "That alarm was changed on another device first." }
        if let problem = validate(&incoming) { return problem }

        if let existing {
            // Settings the editor doesn't show stay as they were.
            incoming.enabled = existing.enabled
            incoming.importedFrom = existing.importedFrom
        }
        if let existing, AlarmState.sameSchedule(existing, incoming) {
            // Only the wording or devices changed: upcoming occurrences (and any snooze) stay as they are.
            var kept = existing
            kept.title = incoming.title
            kept.description = incoming.description
            kept.devices = incoming.devices
            kept.important = incoming.important
            kept.autoSnoozeOverride = incoming.autoSnoozeOverride
            copy.definitions[kept.id] = kept
            outcome.records[kept.id] = .alarm(kept)
            return nil
        }
        // As on the PC: occurrences that haven't gone off restart from the edited schedule.
        for occurrence in copy.occurrences.values where occurrence.definitionId == incoming.id && occurrence.status.isWaiting {
            copy.occurrences[occurrence.id] = nil
            outcome.records[occurrence.id] = .deletedOccurrence
        }
        incoming.revision = existing?.revision ?? 0
        copy.definitions[incoming.id] = incoming
        outcome.records[incoming.id] = .alarm(incoming)
        return nil
    }

    private static func delete(_ op: SyncOp, _ copy: inout CloudCopy, _ outcome: inout Outcome) -> String? {
        guard let id = op.definitionId, let existing = copy.definitions[id] else { return nil } // already gone
        if existing.revision > op.baseRevision { return "That alarm was changed on another device first, so it was kept." }
        copy.definitions[id] = nil
        copy.deletedAlarms.insert(id)
        for occurrence in copy.occurrences.values where occurrence.definitionId == id {
            copy.occurrences[occurrence.id] = nil
            outcome.records[occurrence.id] = .deletedOccurrence
        }
        outcome.records[id] = .deletedAlarm
        return nil
    }

    /// The PC's checks on an alarm from another device (SyncApi.ToDefinition), in the same words.
    private static func validate(_ definition: inout AlarmDefinition) -> String? {
        definition.title = definition.title.trimmingCharacters(in: .whitespacesAndNewlines)
        definition.description = definition.description.trimmingCharacters(in: .whitespacesAndNewlines)
        if definition.title.isEmpty || definition.title.count > 160 { return "Enter a title (up to 160 characters)." }
        if definition.description.count > 1000 { return "Keep the description under 1000 characters." }
        let rule = definition.repeatRule
        if !(1...999).contains(rule.interval) { return "Repeat interval must be between 1 and 999." }
        if !(0...10080).contains(definition.reminderMinutes) { return "Reminder must be between 0 and 10080 minutes before." }
        if let minutes = definition.deadlineAfterMinutes, !(1...10080).contains(minutes) {
            return "Deadline must be between 1 and 10080 minutes after each alert."
        }
        if let nag = definition.autoSnoozeOverride, !nag.isEmpty, SnoozeDuration(nag) == nil { return "The nag length isn't valid." }
        return nil
    }
}

/// The iPhone's own settings, shared by every iPhone on the account (not the PC, which keeps its own): how it nags,
/// its alarm hours, its snooze choices and its theme. Every field is optional, so a setting added later doesn't disturb an older
/// app.
public struct IPhoneSettings: Codable, Hashable, Sendable {
    public static let scope = "iphone"

    public var nagInterval: String?
    public var naggingEnabled: Bool?
    /// Sorted, so the same set always digests the same.
    public var noNagAlarms: [UUID]?
    public var alarmHours: AlarmHours?
    /// This phone's own snooze list; empty means it uses the PC's.
    public var snoozeChoices: [String]?
    public var appearance: String?

    public init(nagInterval: String? = nil, naggingEnabled: Bool? = nil, noNagAlarms: [UUID]? = nil, alarmHours: AlarmHours? = nil,
                snoozeChoices: [String]? = nil, appearance: String? = nil) {
        self.nagInterval = nagInterval
        self.naggingEnabled = naggingEnabled
        self.noNagAlarms = noNagAlarms.map { $0.sorted { $0.uuidString < $1.uuidString } }
        self.alarmHours = alarmHours
        self.snoozeChoices = snoozeChoices
        self.appearance = appearance
    }

    /// These settings with every field `other` has taking its place.
    public func overlaid(with other: IPhoneSettings) -> IPhoneSettings {
        IPhoneSettings(nagInterval: other.nagInterval ?? nagInterval, naggingEnabled: other.naggingEnabled ?? naggingEnabled,
                       noNagAlarms: other.noNagAlarms ?? noNagAlarms, alarmHours: other.alarmHours ?? alarmHours,
                       snoozeChoices: other.snoozeChoices ?? snoozeChoices, appearance: other.appearance ?? appearance)
    }

    public var digest: String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = .sortedKeys
        return SHA256.hash(data: (try? encoder.encode(self)) ?? Data()).map { String(format: "%02x", $0) }.joined()
    }
}

/// How the iPhones' settings sync: the newest write wins, except that a device joining takes what the others share.
public enum SettingsSync {
    public enum Step: Equatable {
        case none
        /// Take these from the cloud, at this seq.
        case apply(IPhoneSettings, seq: Int64)
        /// Send this device's.
        case push
    }

    public static func step(local: IPhoneSettings, remote: (seq: Int64, settings: IPhoneSettings)?, seenSeq: Int64?,
                            seenDigest: String?) -> Step {
        guard let remote else { return seenDigest == local.digest ? .none : .push }
        guard let seenSeq else { return .apply(local.overlaid(with: remote.settings), seq: remote.seq) }
        if seenDigest != local.digest { return .push }
        return remote.seq > seenSeq ? .apply(local.overlaid(with: remote.settings), seq: remote.seq) : .none
    }
}

/// Cloud sync's account and bookkeeping on this device. Secrets (the session and the data key) are kept apart, in
/// the Keychain.
public struct CloudBook: Codable, Hashable, Sendable {
    public var projectURL: String
    public var publishableKey: String
    public var email: String
    public var userId: String
    public var vaultId: String
    /// Names this device in the action queue and as applier.
    public var deviceId: String
    public var copy = CloudCopy()
    /// True until this device's first pull has replaced its view with the shared copy.
    public var joining = true
    /// The iPhones' settings row's seq when this device last pushed or applied it; nil until it first has.
    public var settingsSeq: Int64?
    /// A digest of this device's settings as they were then, to tell whether they've changed since.
    public var settingsDigest: String?

    public init(projectURL: String, publishableKey: String, email: String, userId: String, vaultId: String,
                deviceId: String = "iphone-" + UUID().uuidString.lowercased()) {
        self.projectURL = projectURL
        self.publishableKey = publishableKey
        self.email = email
        self.userId = userId
        self.vaultId = vaultId
        self.deviceId = deviceId
    }
}
