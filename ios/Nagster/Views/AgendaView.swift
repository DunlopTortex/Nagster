import NagsterCore
import SwiftUI

/// Alarms ringing now, coming up and recently answered, like the desktop's main window.
struct AgendaView: View {
    @Environment(AppModel.self) private var model
    @State private var selected: AlarmOccurrence?
    @State private var creating = false
    @State private var showingSettings = false

    var body: some View {
        NavigationStack {
            TimelineView(.periodic(from: .now, by: 30)) { _ in
                list
            }
            .navigationTitle("Nagster")
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("Settings", systemImage: "gearshape") { showingSettings = true }
                }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("New alarm", systemImage: "plus") { creating = true }
                }
            }
            .refreshable { await model.syncNow() }
            .safeAreaInset(edge: .bottom) { SyncStatusBar() }
            .sheet(item: $selected) { occurrence in OccurrenceSheet(occurrence: occurrence) }
            .sheet(isPresented: $creating) { AlarmEditorView(definition: nil) }
            .sheet(isPresented: $showingSettings) { SettingsView() }
        }
        .onChange(of: model.focused) {
            if let focused = model.focused {
                selected = focused
                model.focused = nil
            }
        }
    }

    private var list: some View {
        let ringing = model.ringing
        let upcoming = model.upcoming()
        let recent = model.recent
        return List {
            if model.isPaused {
                Label("Alerts on this iPhone are paused", systemImage: "pause.circle")
                    .foregroundStyle(.secondary)
            }
            if !ringing.isEmpty {
                Section("Going off now") {
                    ForEach(ringing) { row($0) }
                }
            }
            if !upcoming.isEmpty {
                Section("Coming up") {
                    ForEach(upcoming) { row($0) }
                }
            }
            if !recent.isEmpty {
                Section("Answered this week") {
                    ForEach(recent) { row($0) }
                }
            }
        }
        .overlay {
            if ringing.isEmpty && upcoming.isEmpty && recent.isEmpty {
                ContentUnavailableView("No alarms yet", systemImage: "alarm",
                                       description: Text("Alarms you add here or on your PC appear in this list."))
            }
        }
    }

    private func row(_ occurrence: AlarmOccurrence) -> some View {
        Button { selected = occurrence } label: {
            OccurrenceRow(occurrence: occurrence, definition: model.definition(of: occurrence))
        }
        .tint(.primary)
        .swipeActions(edge: .leading) {
            if !occurrence.status.isFinished {
                Button("Complete", systemImage: "checkmark") { model.complete(occurrence) }
                    .tint(.green)
            }
        }
        .swipeActions(edge: .trailing) {
            if !occurrence.status.isFinished, let first = model.state.settings.snoozeDurations.first {
                Button("Snooze \(first.label)", systemImage: "zzz") { model.snooze(occurrence, for: first) }
                    .tint(.orange)
            }
        }
    }
}

struct OccurrenceRow: View {
    var occurrence: AlarmOccurrence
    var definition: AlarmDefinition?

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Image(systemName: definition?.isEvent == true ? "calendar" : "alarm")
                .foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 3) {
                Text(definition?.title ?? "Deleted alarm")
                    .font(.body.weight(.semibold))
                    .strikethrough(occurrence.status == .completed)
                Text(detail)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if definition?.important == true {
                Image(systemName: "bell.badge.fill").foregroundStyle(.orange).accessibilityLabel("Important")
            }
            if definition?.devices == .desktop {
                Image(systemName: "desktopcomputer").foregroundStyle(.secondary).accessibilityLabel("Rings on the PC only")
            }
        }
    }

    private var detail: String {
        switch occurrence.status {
        case .completed: "Completed · was due \(Formatting.dayAndTime(occurrence.scheduledFor))"
        case .dismissed: "\(occurrence.lastAction.isEmpty ? "Dismissed" : occurrence.lastAction) · \(Formatting.dayAndTime(occurrence.scheduledFor))"
        case .snoozed: "Snoozed until \(Formatting.dayAndTime(occurrence.alertAt))"
        case .pending, .active:
            if definition?.isEvent == true {
                "Starts \(Formatting.dayAndTime(occurrence.scheduledFor)) · reminder \(Formatting.time(occurrence.alertAt))"
            } else {
                Formatting.dayAndTime(occurrence.alertAt) + (occurrence.deadline.map { " · complete by \(Formatting.time($0))" } ?? "")
            }
        }
    }
}

/// Shows when the phone last synced with the PC, or why it couldn't.
private struct SyncStatusBar: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            switch model.syncStatus {
            case .notSetUp:
                Label("Not linked to a PC yet. Set it up in Settings.", systemImage: "link")
            case .syncing:
                Label("Syncing with your PC…", systemImage: "arrow.triangle.2.circlepath")
            case .idle(let lastSynced):
                if let lastSynced {
                    Label("Synced \(lastSynced.formatted(.relative(presentation: .named)))", systemImage: "checkmark.circle")
                } else {
                    Label("Not synced yet", systemImage: "arrow.triangle.2.circlepath")
                }
            case .failed(let message):
                Label(message, systemImage: "exclamationmark.triangle")
            }
        }
        .font(.footnote)
        .foregroundStyle(.secondary)
        .lineLimit(2)
        .frame(maxWidth: .infinity)
        .padding(.vertical, 8)
        .background(.bar)
    }
}
