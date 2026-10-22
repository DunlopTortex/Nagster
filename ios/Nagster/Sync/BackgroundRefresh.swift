import BackgroundTasks
import Foundation

/// Asks iOS to wake the app now and then to sync with the PC and top up the scheduled notifications.
/// iOS decides when (and whether) it actually runs, so this only helps; opening the app always syncs.
enum BackgroundRefresh {
    static let identifier = "com.dgutilities.nagster.refresh"

    static func schedule() {
        let request = BGAppRefreshTaskRequest(identifier: identifier)
        request.earliestBeginDate = Date(timeIntervalSinceNow: 15 * 60)
        try? BGTaskScheduler.shared.submit(request)
    }
}
