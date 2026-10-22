import NagsterCore
import SwiftUI

/// One alarm occurrence and everything that can be done with it, like the PC's alarm popup: Complete first and
/// largest, the snooze lengths next, then the quieter choices.
struct OccurrenceSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var close
    let occurrence: AlarmOccurrence
    @State private var rescheduleTo = Date().addingTimeInterval(3600)
    @State private var problem: String?
    @State private var editing = false
    @State private var confirmingDelete = false
    @State private var completed = 0

    private var definition: AlarmDefinition? { model.definition(of: occurrence) }
    private var answerable: Bool { !occurrence.status.isFinished }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 26) {
                    header
                    if let notes = definition?.description, !notes.isEmpty {
                        Text(notes)
                            .foregroundStyle(Theme.ink)
                    }
                    if answerable {
                        Button {
                            completed += 1
                            act { model.complete(occurrence) }
                        } label: {
                            Label("Complete", systemImage: "checkmark")
                                .font(.rounded(.title3, weight: .bold))
                                .frame(maxWidth: .infinity)
                                .padding(.vertical, 16)
                                .foregroundStyle(.white)
                                .background(Theme.brand, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
                        }
                        .sensoryFeedback(.success, trigger: completed)

                        snoozeChoices
                        reschedule

                        Button("Dismiss without completing", role: .destructive) { act { model.dismiss(occurrence) } }
                            .font(.body.weight(.semibold))
                            .foregroundStyle(Theme.nag)
                            .frame(maxWidth: .infinity)
                    } else if !occurrence.lastAction.isEmpty {
                        Label(occurrence.lastAction,
                              systemImage: occurrence.status == .completed ? "checkmark.circle.fill" : "xmark.circle")
                            .foregroundStyle(Theme.secondaryText)
                    }

                    if definition != nil {
                        Divider().overlay(Theme.rail)
                        HStack {
                            Button("Edit alarm", systemImage: "pencil") { editing = true }
                                .tint(Theme.violet)
                            Spacer()
                            Button("Delete alarm", systemImage: "trash", role: .destructive) { confirmingDelete = true }
                                .tint(Theme.nag)
                        }
                        .font(.body.weight(.medium))
                    }
                }
                .padding(20)
            }
            .background(Theme.canvas)
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
                if let definition { AlarmEditorView(definition: definition, nextRun: model.nextRun(of: definition.id),
                                                nagOff: model.noNagAlarms.contains(definition.id)) }
            }
        }
        .presentationCornerRadius(32)
    }

    private var header: some View {
        HStack(alignment: .center, spacing: 16) {
            ClockDial(time: occurrence.alertAt, progress: answerable ? 0.75 : 1,
                      urgent: answerable && occurrence.alertAt <= model.now)
                .frame(width: 88, height: 88)
            VStack(alignment: .leading, spacing: 6) {
                Text(definition?.title ?? "Alarm")
                    .font(.rounded(.title2, weight: .bold))
                    .foregroundStyle(.white)
                    .lineLimit(3)
                Text(whenText)
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(.white.opacity(0.85))
                if let deadline = occurrence.deadline {
                    Label("Complete by \(Formatting.dayAndTime(deadline))", systemImage: "flag.fill")
                        .font(.footnote.weight(.semibold))
                        .padding(.horizontal, 10)
                        .padding(.vertical, 4)
                        .foregroundStyle(.white)
                        .background(Theme.nag.opacity(0.85), in: Capsule())
                }
            }
            Spacer(minLength: 0)
        }
        .padding(18)
        .background(Theme.brand, in: RoundedRectangle(cornerRadius: 28, style: .continuous))
    }

    private var whenText: String {
        let due = "\(definition?.isEvent == true ? "Starts" : "Due") \(Formatting.dayAndTime(occurrence.scheduledFor))"
        if occurrence.status == .snoozed { return "\(due), snoozed until \(Formatting.time(occurrence.alertAt))" }
        return due
    }

    private var snoozeChoices: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Snooze for")
                .font(.rounded(.headline))
                .foregroundStyle(Theme.ink)
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 100), spacing: 10)], spacing: 10) {
                ForEach(model.state.settings.snoozeDurations, id: \.self) { duration in
                    Button { attempt { model.snooze(occurrence, for: duration) } } label: {
                        VStack(spacing: 2) {
                            Text("\(duration.amount)")
                                .font(.rounded(.title3, weight: .bold))
                            Text(duration.unitLabel)
                                .font(.rounded(.footnote, weight: .medium))
                        }
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 12)
                        .foregroundStyle(Theme.ink)
                        .background(Theme.violet.opacity(0.13), in: Capsule())
                    }
                }
            }
        }
    }

    private var reschedule: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Or ring again at")
                .font(.rounded(.headline))
                .foregroundStyle(Theme.ink)
            DatePicker("Ring again at", selection: $rescheduleTo, in: Date()...)
                .labelsHidden()
            Button { attempt { model.reschedule(occurrence, to: rescheduleTo) } } label: {
                Label("Reschedule", systemImage: "calendar.badge.clock")
                    .font(.rounded(.body))
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 12)
                    .foregroundStyle(Theme.ink)
                    .background(Theme.violet.opacity(0.13), in: Capsule())
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
