import NagsterCore
import SwiftUI
import UIKit

/// Linking to the PC, pausing this phone's alerts, and the permissions alarms need.
struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var close
    @State private var password = ""
    @State private var signingIn = false
    @State private var signInProblem: String?
    @State private var notificationsAllowed = true

    private static let pauseChoices: [(String, TimeInterval?)] =
        [("30 minutes", 1800), ("1 hour", 3600), ("4 hours", 14_400), ("1 day", 86_400), ("Until I resume", nil)]

    var body: some View {
        @Bindable var model = model
        NavigationStack {
            Form {
                Section {
                    TextField("PC address, e.g. 192.168.1.20", text: $model.pcAddress)
                        .textContentType(.URL)
                        .keyboardType(.URL)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                    SecureField("Web access password", text: $password)
                    Button {
                        Task { await signIn() }
                    } label: {
                        if signingIn { ProgressView() } else { Text(SyncClient.isSignedIn ? "Sign in again" : "Sign in") }
                    }
                    .disabled(password.isEmpty || model.pcAddress.isEmpty || signingIn)
                    if let signInProblem {
                        Text(signInProblem).foregroundStyle(.red).font(.footnote)
                    }
                    Button("Sync now") { Task { await model.syncNow() } }
                        .disabled(!SyncClient.isSignedIn)
                    if SyncClient.isSignedIn {
                        Button("Sign out", role: .destructive) { model.signOut() }
                    }
                    ForEach(model.rejected, id: \.self) { message in
                        Label(message, systemImage: "exclamationmark.circle").font(.footnote).foregroundStyle(.secondary)
                    }
                } header: {
                    Text("Your PC")
                } footer: {
                    Text("On the PC, turn on Settings → Web access in Nagster and set a password. Use the PC's address on your home "
                         + "network, or its Tailscale IP address to sync from anywhere.")
                }

                Section("Pause alerts on this iPhone") {
                    if model.isPaused, let until = model.pausedUntil {
                        LabeledContent("Paused until", value: until == .distantFuture ? "you resume" : until.formatted(date: .abbreviated, time: .shortened))
                        Button("Resume") { model.resume() }
                    } else {
                        ForEach(Self.pauseChoices, id: \.0) { choice in
                            Button(choice.0) { model.pause(for: choice.1) }
                        }
                    }
                }

                Section {
                    LabeledContent("Notifications", value: notificationsAllowed ? "Allowed" : "Off")
                    LabeledContent("Important alarms", value: model.importantAlarmsAllowed ? "Allowed" : "Not allowed")
                    if !notificationsAllowed || !model.importantAlarmsAllowed {
                        Button("Open iPhone Settings") {
                            if let url = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(url) }
                        }
                    }
                } header: {
                    Text("Permissions")
                } footer: {
                    Text("Without notifications this iPhone can't nag you. Important alarms ring as ordinary notifications until allowed.")
                }
            }
            .scrollContentBackground(.hidden)
            .background(Theme.canvas)
            .navigationTitle("Settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("Done") { close() } }
            }
            .task { notificationsAllowed = await model.notificationsAllowed() }
        }
    }

    private func signIn() async {
        signingIn = true
        signInProblem = nil
        defer { signingIn = false }
        do {
            try await model.signIn(password: password)
            password = ""
        } catch {
            signInProblem = error.localizedDescription
        }
    }
}
