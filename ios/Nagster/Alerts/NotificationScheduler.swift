import Foundation
import NagsterCore
import UserNotifications

/// Schedules the planned alerts as local notifications. Each notification carries buttons like the PC's popup:
/// Complete, the first few snooze choices, and Dismiss.
final class NotificationScheduler {
    static let categoryID = "nagster.alarm"
    static let completeAction = "complete"
    static let dismissAction = "dismiss"
    static let snoozePrefix = "snooze:"
    /// More snooze buttons than this crowd the expanded notification; the app offers every choice.
    static let maxSnoozeButtons = 4

    private let center = UNUserNotificationCenter.current()

    func requestPermission() async -> Bool {
        (try? await center.requestAuthorization(options: [.alert, .sound, .badge])) ?? false
    }

    func isAllowed() async -> Bool {
        let status = await center.notificationSettings().authorizationStatus
        return status == .authorized || status == .provisional
    }

    /// Sets the notification buttons from the phone's snooze choices. Complete comes first, then the first snooze:
    /// the only two iOS shows when space is short.
    func registerActions(for snoozeChoices: [SnoozeDuration]) {
        var actions = [UNNotificationAction(identifier: Self.completeAction, title: "Complete",
                                            icon: UNNotificationActionIcon(systemImageName: "checkmark"))]
        for duration in snoozeChoices.prefix(Self.maxSnoozeButtons) {
            actions.append(UNNotificationAction(identifier: Self.snoozePrefix + duration.description, title: "Snooze \(duration.label)",
                                                icon: UNNotificationActionIcon(systemImageName: "zzz")))
        }
        actions.append(UNNotificationAction(identifier: Self.dismissAction, title: "Dismiss", options: .destructive))
        center.setNotificationCategories([UNNotificationCategory(identifier: Self.categoryID, actions: actions, intentIdentifiers: [])])
    }

    /// Replaces every pending notification with `plan`. Important first alerts are left to AlarmKit unless it
    /// isn't allowed, in which case they become ordinary notifications.
    func schedule(_ plan: [PlannedAlert], state: AlarmState, alarmKitRingsImportant: Bool) async {
        center.removeAllPendingNotificationRequests()
        for alert in plan where !(alert.important && alarmKitRingsImportant) {
            guard let definition = state.definitions[alert.occurrence.definitionId] else { continue }
            let request = UNNotificationRequest(identifier: alert.identifier, content: content(for: alert, definition: definition),
                                                trigger: UNCalendarNotificationTrigger(dateMatching: alert.fireAt.components, repeats: false))
            try? await center.add(request)
        }
    }

    /// Clears an answered occurrence's notifications from Notification Centre.
    func removeDelivered(for occurrenceID: UUID) {
        center.removeDeliveredNotifications(withIdentifiers: (0...AlertPlanner.maxNags).map { "\(occurrenceID.uuidString)#\($0)" })
    }

    private func content(for alert: PlannedAlert, definition: AlarmDefinition) -> UNNotificationContent {
        let content = UNMutableNotificationContent()
        content.title = definition.title
        let due = Formatting.time(alert.occurrence.scheduledFor)
        var lines: [String] = []
        if definition.isEvent { lines.append("Starts \(due)") }
        if alert.nag > 0 { lines.append("Still waiting since \(Formatting.time(alert.occurrence.alertAt))") }
        if let deadline = alert.occurrence.deadline { lines.append("Complete by \(Formatting.dayAndTime(deadline))") }
        if !definition.description.isEmpty { lines.append(definition.description) }
        content.body = lines.isEmpty ? "Due \(due)" : lines.joined(separator: "\n")
        content.sound = .default
        content.interruptionLevel = .timeSensitive
        content.categoryIdentifier = Self.categoryID
        content.threadIdentifier = alert.occurrence.id.uuidString
        content.userInfo = NotificationTarget(occurrence: alert.occurrence).userInfo
        return content
    }
}

/// Identifies the occurrence a notification or Important alarm is for, including ones the phone worked out
/// itself and hasn't stored yet (hence the alarm and time as well as the ID).
nonisolated struct NotificationTarget: Equatable, Sendable {
    var occurrenceID: UUID
    var definitionID: UUID
    var scheduledFor: WallTime

    init(occurrence: AlarmOccurrence) {
        occurrenceID = occurrence.id
        definitionID = occurrence.definitionId
        scheduledFor = occurrence.scheduledFor
    }

    init?(userInfo: [AnyHashable: Any]) {
        guard let occurrence = (userInfo["occurrence"] as? String).flatMap(UUID.init(uuidString:)),
              let definition = (userInfo["definition"] as? String).flatMap(UUID.init(uuidString:)),
              let scheduled = (userInfo["scheduledFor"] as? String).flatMap(WallTime.init(iso:)) else { return nil }
        occurrenceID = occurrence
        definitionID = definition
        scheduledFor = scheduled
    }

    var userInfo: [String: String] {
        ["occurrence": occurrenceID.uuidString, "definition": definitionID.uuidString, "scheduledFor": scheduledFor.iso]
    }

    /// One string, for App Intent parameters: "occurrence|alarm|time".
    var encoded: String { "\(occurrenceID.uuidString)|\(definitionID.uuidString)|\(scheduledFor.iso)" }

    init?(encoded: String) {
        let parts = encoded.split(separator: "|").map(String.init)
        guard parts.count == 3 else { return nil }
        self.init(userInfo: ["occurrence": parts[0], "definition": parts[1], "scheduledFor": parts[2]])
    }
}
