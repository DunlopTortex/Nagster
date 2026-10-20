import AlarmKit
import AppIntents
import Foundation
import NagsterCore

// The buttons on an Important alarm. iOS runs these in the app (starting it in the background if need be), so the
// answer is recorded and synced to the PC like any other.

struct CompleteImportantAlarmIntent: LiveActivityIntent {
    static let title: LocalizedStringResource = "Complete alarm"
    static let isDiscoverable = false

    @Parameter(title: "Occurrence") var target: String

    init() {}

    init(target: NotificationTarget) {
        self.target = target.encoded
    }

    func perform() async throws -> some IntentResult {
        if let target = NotificationTarget(encoded: target) {
            await AppModel.current?.complete(target)
            await AppModel.current?.syncNow()
        }
        return .result()
    }
}

struct SnoozeImportantAlarmIntent: LiveActivityIntent {
    static let title: LocalizedStringResource = "Snooze alarm"
    static let isDiscoverable = false

    @Parameter(title: "Occurrence") var target: String
    @Parameter(title: "Duration") var duration: String

    init() {}

    init(target: NotificationTarget, duration: String) {
        self.target = target.encoded
        self.duration = duration
    }

    func perform() async throws -> some IntentResult {
        // AlarmKit runs the snooze countdown itself; this records it so the PC and the phone's list agree.
        if let target = NotificationTarget(encoded: target), let length = SnoozeDuration(duration) {
            await AppModel.current?.snoozeFromAlarm(target, for: length)
            await AppModel.current?.syncNow()
        }
        return .result()
    }
}
