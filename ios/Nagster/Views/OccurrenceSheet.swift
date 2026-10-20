import NagsterCore
import SwiftUI

/// One alarm occurrence and everything that can be done with it, like the PC's alarm popup.
struct OccurrenceSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var close
    let occurrence: AlarmOccurrence
    @State private var rescheduleTo = Date().addingTimeInterval(3600)
    @State private var problem: String?
    @State private var editing = false
    @State private var confirmingDelete = false

    private var definition: AlarmDefinition? { model.definition(of: occurrence) }
    private var answerable: Bool { !occurrence.status.isFinished }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    LabeledContent(definition?.isEvent == true ? "Starts" : "Due", value: Formatting.dayAndTime(occurrence.scheduledFor))
                    if occurrence.alertAt != occurrence.scheduledFor {
                        LabeledContent(occurrence.status == .snoozed ? "Snoozed until" : "Reminder",
                                       value: Formatting.dayAndTime(occurrence.alertAt))
                    }
                    if let deadline = occurrence.deadline {
                        LabeledContent("Complete by", value: Formatting.dayAndTime(deadline))
                    }
                    if let description = definition?.description, !description.isEmpty {
                        Text(description)
                    }
                    if !occurrence.lastAction.isEmpty {
                        Text(occurrence.lastAction).font(.footnote).foregroundStyle(.secondary)
                    }
                }

                if answerable {
                    Section {
                        Button("Complete", systemImage: "checkmark.circle.fill") { act { model.complete(occurrence) } }
                    }
                    Section("Snooze") {
                        LazyVGrid(columns: [GridItem(.adaptive(minimum: 96))], spacing: 10) {
                            ForEach(model.state.settings.snoozeDurations, id: \.self) { duration in
                                Button(duration.label) { attempt { model.snooze(occurrence, for: duration) } }
                                    .buttonStyle(.bordered)
                            }
                        }
                        .padding(.vertical, 4)
                    }
                    Section("Reschedule") {
                        DatePicker("Ring again at", selection: $rescheduleTo, in: Date()...)
                        Button("Reschedule") { attempt { model.reschedule(occurrence, to: rescheduleTo) } }
                    }
                    Section {
                        Button("Dismiss without completing", role: .destructive) { act { model.dismiss(occurrence) } }
                    }
                }

                if definition != nil {
                    Section {
                        Button("Edit alarm", systemImage: "pencil") { editing = true }
                        Button("Delete alarm", systemImage: "trash", role: .destructive) { confirmingDelete = true }
                    }
                }
            }
            .navigationTitle(definition?.title ?? "Alarm")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("Done") { close() } }
            }
            .alert("Can't do that", isPresented: Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })) {
                Button("OK", role: .cancel) {}
            } message: {
                Text(problem ?? "")
            }
            .confirmationDialog("Delete \"\(definition?.title ?? "this alarm")\" and all its occurrences?",
                                isPresented: $confirmingDelete, titleVisibility: .visible) {
                Button("Delete", role: .destructive) {
                    if let definition { model.delete(definition.id) }
                    close()
                }
            }
            .sheet(isPresented: $editing) {
                if let definition { AlarmEditorView(definition: definition, nextRun: model.nextRun(of: definition.id)) }
            }
        }
    }

    /// Runs an action that can be refused; closes the sheet if it worked, or shows why it didn't.
    private func attempt(_ action: () -> String?) {
        if let reason = action() {
            problem = reason
        } else {
            close()
        }
    }

    private func act(_ action: () -> Void) {
        action()
        close()
    }
}
