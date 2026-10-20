import Foundation
import NagsterCore
import UserNotifications

/// Handles taps on notification buttons, even when iOS starts the app in the background to do it, and shows
/// alerts that arrive while the app is open.
final class NotificationResponder: NSObject, UNUserNotificationCenterDelegate {
    private let model: AppModel

    init(model: AppModel) {
        self.model = model
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter,
                                            willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        let action = response.actionIdentifier
        guard let target = NotificationTarget(userInfo: response.notification.request.content.userInfo) else { return }
        await handle(action, for: target)
    }

    private func handle(_ action: String, for target: NotificationTarget) async {
        switch action {
        case NotificationScheduler.completeAction:
            model.complete(target)
        case NotificationScheduler.dismissAction:
            model.dismiss(target)
        case let snooze where snooze.hasPrefix(NotificationScheduler.snoozePrefix):
            if let duration = SnoozeDuration(String(snooze.dropFirst(NotificationScheduler.snoozePrefix.count))) {
                model.snooze(target, for: duration)
            }
        case UNNotificationDefaultActionIdentifier:
            model.focus(target)
        default:
            return
        }
        // Give the PC the answer straight away, so its popup closes too.
        await model.syncNow()
    }
}
