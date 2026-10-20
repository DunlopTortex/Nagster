import Foundation

/// Which devices sound an alarm. Every device lists it; the others just don't ring.
public enum AlarmDevices: String, Codable, Sendable, CaseIterable {
    case all = "All", desktop = "Desktop", phone = "Phone"

    public var label: String {
        switch self {
        case .all: "All devices"
        case .desktop: "PC only"
        case .phone: "iPhone only"
        }
    }

    public var ringsOnPhone: Bool { self != .desktop }
}

public enum OccurrenceStatus: String, Codable, Sendable {
    case pending = "Pending", active = "Active", snoozed = "Snoozed", dismissed = "Dismissed", completed = "Completed"

    public var isFinished: Bool { self == .dismissed || self == .completed }
    public var isWaiting: Bool { self == .pending || self == .snoozed }
}

/// An alarm or event and its schedule, as the PC syncs it (`SyncDefinition` in the Windows app).
public struct AlarmDefinition: Codable, Hashable, Sendable, Identifiable {
    public var id: UUID
    public var title: String
    public var description: String
    public var isEvent: Bool
    public var eventEnd: WallTime?
    /// For events, how long before the start the reminder goes off.
    public var reminderMinutes: Int
    public var firstDue: WallTime
    public var repeatRule: RepeatRule
    public var absoluteDeadline: WallTime?
    /// A deadline this many minutes after each occurrence is due; takes precedence over `absoluteDeadline`.
    public var deadlineAfterMinutes: Int?
    public var devices: AlarmDevices
    /// Ring as a full-screen AlarmKit alarm rather than a notification.
    public var important: Bool
    public var enabled: Bool
    /// The calendar the PC imported this alarm from, if any.
    public var importedFrom: String?
    /// The PC's revision when this alarm last changed there; 0 for an alarm the PC hasn't seen yet.
    public var revision: Int64

    enum CodingKeys: String, CodingKey {
        case id, title, description, isEvent, eventEnd, reminderMinutes, firstDue, absoluteDeadline, deadlineAfterMinutes,
             devices, important, enabled, importedFrom, revision
        case repeatRule = "repeat"
    }

    public init(id: UUID = UUID(), title: String, description: String = "", isEvent: Bool = false, eventEnd: WallTime? = nil,
                reminderMinutes: Int = 0, firstDue: WallTime, repeatRule: RepeatRule = RepeatRule(), absoluteDeadline: WallTime? = nil,
                deadlineAfterMinutes: Int? = nil, devices: AlarmDevices = .all, important: Bool = false, enabled: Bool = true,
                importedFrom: String? = nil, revision: Int64 = 0) {
        self.id = id
        self.title = title
        self.description = description
        self.isEvent = isEvent
        self.eventEnd = eventEnd
        self.reminderMinutes = reminderMinutes
        self.firstDue = firstDue
        self.repeatRule = repeatRule
        self.absoluteDeadline = absoluteDeadline
        self.deadlineAfterMinutes = deadlineAfterMinutes
        self.devices = devices
        self.important = important
        self.enabled = enabled
        self.importedFrom = importedFrom
        self.revision = revision
    }

    public var isRecurring: Bool { repeatRule.kind != .once }

    /// The occurrence due at `due`, as both apps create it: same ID, alert time and deadline.
    public func occurrence(at due: WallTime) -> AlarmOccurrence {
        AlarmOccurrence(
            id: OccurrenceID.make(definitionID: id, scheduledFor: due),
            definitionId: id,
            scheduledFor: due,
            alertAt: isEvent ? due.adding(.minute, -reminderMinutes) : due,
            deadline: deadlineAfterMinutes.map { due.adding(.minute, $0) } ?? absoluteDeadline)
    }
}

/// One time an alarm goes off (`SyncOccurrence` in the Windows app).
public struct AlarmOccurrence: Codable, Hashable, Sendable, Identifiable {
    public var id: UUID
    public var definitionId: UUID
    public var scheduledFor: WallTime
    /// When it next rings: the scheduled time (less any event reminder) or the time it was snoozed until.
    public var alertAt: WallTime
    public var deadline: WallTime?
    public var status: OccurrenceStatus
    public var snoozeCount: Int
    public var lastAction: String
    /// The PC's revision when this occurrence last changed there; 0 if the PC hasn't stored it yet.
    public var revision: Int64

    public init(id: UUID, definitionId: UUID, scheduledFor: WallTime, alertAt: WallTime, deadline: WallTime? = nil,
                status: OccurrenceStatus = .pending, snoozeCount: Int = 0, lastAction: String = "", revision: Int64 = 0) {
        self.id = id
        self.definitionId = definitionId
        self.scheduledFor = scheduledFor
        self.alertAt = alertAt
        self.deadline = deadline
        self.status = status
        self.snoozeCount = snoozeCount
        self.lastAction = lastAction
        self.revision = revision
    }
}

/// The PC settings the phone follows. Defaults match the Windows app's, for use before the first sync.
public struct PhoneSettings: Codable, Hashable, Sendable {
    public var snoozeChoices: [String]
    public var autoSnooze: String
    public var responseSeconds: Int

    public init(snoozeChoices: [String] = ["5m", "15m", "30m", "1h", "1d"], autoSnooze: String = "1h", responseSeconds: Int = 30) {
        self.snoozeChoices = snoozeChoices
        self.autoSnooze = autoSnooze
        self.responseSeconds = responseSeconds
    }

    public var snoozeDurations: [SnoozeDuration] { snoozeChoices.compactMap(SnoozeDuration.init) }

    /// How long an unanswered alarm waits before nagging again.
    public var autoSnoozeDuration: SnoozeDuration { SnoozeDuration(autoSnooze) ?? SnoozeDuration(amount: 1, unit: .hours) }
}
