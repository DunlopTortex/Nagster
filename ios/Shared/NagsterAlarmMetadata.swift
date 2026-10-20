import AlarmKit
import Foundation

/// What an Important alarm carries for its Live Activity and buttons: which occurrence it is (alarm and time
/// as well as the ID, as the phone may have worked it out itself). Compiled into the app and the widget.
nonisolated struct NagsterAlarmMetadata: AlarmMetadata {
    var occurrenceID: UUID
    var definitionID: UUID
    /// Wall-clock "yyyy-MM-ddTHH:mm:ss".
    var scheduledFor: String
    var title: String
}
