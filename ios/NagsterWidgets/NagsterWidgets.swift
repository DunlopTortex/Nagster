import ActivityKit
import AlarmKit
import SwiftUI
import WidgetKit

@main
struct NagsterWidgets: WidgetBundle {
    var body: some Widget {
        ImportantAlarmActivity()
    }
}

/// The Lock Screen and Dynamic Island view of an Important alarm while it's snoozed. AlarmKit draws the ringing
/// alarm itself; this covers the countdown in between.
struct ImportantAlarmActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: AlarmAttributes<NagsterAlarmMetadata>.self) { context in
            HStack(spacing: 12) {
                Image(systemName: "alarm.fill")
                    .font(.title2)
                    .foregroundStyle(context.attributes.tintColor)
                VStack(alignment: .leading, spacing: 2) {
                    Text(context.attributes.metadata?.title ?? "Alarm")
                        .font(.headline)
                    Text("Snoozed")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                SnoozeCountdown(state: context.state)
                    .font(.title2.monospacedDigit())
            }
            .padding()
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    Image(systemName: "alarm.fill").foregroundStyle(context.attributes.tintColor)
                }
                DynamicIslandExpandedRegion(.center) {
                    Text(context.attributes.metadata?.title ?? "Alarm").font(.headline)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    SnoozeCountdown(state: context.state).monospacedDigit()
                }
            } compactLeading: {
                Image(systemName: "alarm.fill").foregroundStyle(context.attributes.tintColor)
            } compactTrailing: {
                SnoozeCountdown(state: context.state).monospacedDigit().frame(maxWidth: 52)
            } minimal: {
                Image(systemName: "alarm.fill").foregroundStyle(context.attributes.tintColor)
            }
        }
    }
}

/// Time left until a snoozed alarm rings again.
private struct SnoozeCountdown: View {
    var state: AlarmPresentationState

    var body: some View {
        switch state.mode {
        case .countdown(let countdown):
            Text(timerInterval: Date.now...max(Date.now, countdown.fireDate), countsDown: true)
        default:
            Image(systemName: "bell.fill")
        }
    }
}
