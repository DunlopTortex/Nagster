import AlarmKit
import Foundation
import NagsterCore
import OSLog
import SwiftUI

/// Rings Important alarms through AlarmKit: full screen, through silent mode and Focus, like the Clock app.
/// The alarm's Stop button completes it; Snooze counts down the first of the PC's snooze choices. Other snooze
/// lengths, Dismiss and Reschedule are in the app.
final class ImportantAlarmScheduler {
    private let manager = AlarmManager.shared
    private let log = Logger(subsystem: "com.dgutilities.nagster", category: "alarmkit")

    var isAuthorized: Bool { manager.authorizationState == .authorized }

    func requestAuthorization() async -> Bool {
        do { return try await manager.requestAuthorization() == .authorized } catch {
            log.error("AlarmKit permission request failed: \(error.localizedDescription, privacy: .public)")
            return false
        }
    }

    /// Makes AlarmKit's scheduled alarms match the plan's Important first alerts. Alarms already ringing or
    /// counting down a snooze are left alone.
    func schedule(_ plan: [PlannedAlert], state: AlarmState) async {
        guard isAuthorized else { return }
        let wanted = plan.filter(\.important)
        let existing = (try? manager.alarms) ?? []
        for alarm in existing where alarm.state == .scheduled {
            try? manager.cancel(id: alarm.id)
        }
        let busy = Set(existing.filter { $0.state != .scheduled }.map(\.id))
        let snooze = state.settings.snoozeDurations.first ?? SnoozeDuration(amount: 10, unit: .minutes)
        for alert in wanted where !busy.contains(alert.occurrence.id) {
            guard let definition = state.definitions[alert.occurrence.definitionId] else { continue }
            do {
                _ = try await manager.schedule(id: alert.occurrence.id,
                                               configuration: configuration(for: alert, definition: definition, snooze: snooze))
            } catch {
                log.error("Couldn't schedule an Important alarm: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    /// Silences and removes an answered occurrence's alarm.
    func stop(_ occurrenceID: UUID) {
        try? manager.stop(id: occurrenceID)
        try? manager.cancel(id: occurrenceID)
    }

    private func configuration(for alert: PlannedAlert, definition: AlarmDefinition,
                               snooze: SnoozeDuration) -> AlarmManager.AlarmConfiguration<NagsterAlarmMetadata> {
        let target = NotificationTarget(occurrence: alert.occurrence)
        let metadata = NagsterAlarmMetadata(occurrenceID: target.occurrenceID, definitionID: target.definitionID,
                                            scheduledFor: target.scheduledFor.iso, title: definition.title)
        let alertContent = AlarmPresentation.Alert(
            title: LocalizedStringResource(stringLiteral: definition.title),
            secondaryButton: AlarmButton(text: "Snooze \(snooze.label)", textColor: .white, systemImageName: "zzz"),
            secondaryButtonBehavior: .countdown)
        let presentation = AlarmPresentation(alert: alertContent,
                                             countdown: AlarmPresentation.Countdown(title: "Snoozed"),
                                             paused: nil)
        let attributes = AlarmAttributes(presentation: presentation, metadata: metadata, tintColor: Color.orange)
        let now = WallTime(Date())
        let snoozeSeconds = snooze.added(to: now) - now
        return AlarmManager.AlarmConfiguration(
            countdownDuration: Alarm.CountdownDuration(preAlert: nil, postAlert: snoozeSeconds),
            schedule: .fixed(alert.fireAt.date()),
            attributes: attributes,
            stopIntent: CompleteImportantAlarmIntent(target: target),
            secondaryIntent: SnoozeImportantAlarmIntent(target: target, duration: snooze.description))
    }
}
