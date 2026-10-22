import NagsterCore
import SwiftUI

/// The snooze lengths this iPhone offers. It uses the PC's list until one is changed here; from then on the
/// phone keeps its own, and the PC keeps its own.
struct SnoozeChoicesSection: View {
    @Environment(AppModel.self) private var model
    @State private var amount = 1
    @State private var unit = SnoozeUnit.weeks

    private var adding: SnoozeDuration { SnoozeDuration(amount: amount, unit: unit) }
    private var canAdd: Bool { (1...SnoozeDuration.maxAmount).contains(amount) && !model.snoozeChoices.contains(adding) }

    var body: some View {
        Section {
            ForEach(model.snoozeChoices, id: \.self) { Text($0.label) }
                .onDelete { offsets in change { $0.remove(atOffsets: offsets) } }
                .onMove { from, to in change { $0.move(fromOffsets: from, toOffset: to) } }
                .deleteDisabled(model.snoozeChoices.count == 1)
            HStack {
                TextField("Amount", value: $amount, format: .number)
                    .keyboardType(.numberPad)
                    .frame(width: 56)
                Picker("Unit", selection: $unit) {
                    ForEach(SnoozeUnit.allCases, id: \.self) { Text(SnoozeDuration(amount: amount, unit: $0).unitLabel).tag($0) }
                }
                .labelsHidden()
                Spacer()
                Button("Add") { change { $0.append(adding) } }
                    .disabled(!canAdd)
            }
            if model.ownSnoozeChoices != nil && SyncClient.isSignedIn {
                Button("Use the PC's list") { model.ownSnoozeChoices = nil }
            }
        } header: {
            HStack {
                Text("Snooze choices")
                Spacer()
                EditButton().font(.footnote)
            }
        } footer: {
            Text(footer)
        }
    }

    private var footer: String {
        let usage = "The first is the quick snooze and an Important alarm's Snooze; notifications show the first four."
        guard SyncClient.isSignedIn else { return usage }
        return model.ownSnoozeChoices == nil
            ? "\(usage) Using the PC's list; changing it here gives this iPhone its own."
            : "\(usage) This iPhone's own list; the PC keeps its own."
    }

    /// Edits the list shown (the PC's until now) and keeps the result as this phone's own.
    private func change(_ edit: (inout [SnoozeDuration]) -> Void) {
        var choices = model.snoozeChoices
        edit(&choices)
        model.ownSnoozeChoices = choices
    }
}
