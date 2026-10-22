import Foundation
import NagsterCore
import UserNotifications

/// Handles taps on notification buttons, even when iOS starts the app in the background to do it, and shows
/// alerts that arrive while the app is open.
///
/// These use the completion-handler forms, not the async ones: iOS can run an async delegate method's completion
/// off the main thread, and when a tap brings the app forward UIKit then crashes ("Call must be made on main
/// thread"). Here the action is applied and iOS is told it's done on the main actor, and syncing follows after.
final class NotificationResponder: NSObject, UNUserNotificationCenterDelegate {
    private let model: AppModel

    init(model: AppModel) {
        self.model = model
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                            withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list, .sound])
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                            withCompletionHandler completionHandler: @escaping () -> Void) {
        let action = response.actionIdentifier
        let target = NotificationTarget(userInfo: response.notification.request.content.userInfo)
        // iOS's handler isn't marked Sendable, but it's only called once, on the main actor.
        nonisolated(unsafe) let done = completionHandler
        Task { @MainActor in
            let answered = target.map { handle(action, for: $0) } ?? false
            done()
            // Give the PC the answer straight away, so its popup closes too.
            if answered { await model.syncNow() }
        }
    }

    /// Applies the tapped action. Returns true if it answered the alarm (rather than just opening the app).
    private func handle(_ action: String, for target: NotificationTarget) -> Bool {
        switch action {
        case NotificationScheduler.completeAction:
            model.complete(target)
        case NotificationScheduler.dismissAction:
            model.dismiss(target)
        case let snooze where snooze.hasPrefix(NotificationScheduler.snoozePrefix):
            guard let duration = SnoozeDuration(String(snooze.dropFirst(NotificationScheduler.snoozePrefix.count))) else { return false }
            model.snooze(target, for: duration)
        case UNNotificationDefaultActionIdentifier:
            model.focus(target)
            return false
        default:
            return false
        }
        return true
    }
}
