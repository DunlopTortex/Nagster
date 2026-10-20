import SwiftUI

/// Alarms ringing now, coming up and recently answered, like the desktop's main window.
struct AgendaView: View {
    var body: some View {
        NavigationStack {
            ContentUnavailableView("No alarms yet", systemImage: "alarm",
                                   description: Text("Alarms you add here or on your PC appear in this list."))
                .navigationTitle("Nagster")
        }
    }
}

#Preview {
    AgendaView()
}
