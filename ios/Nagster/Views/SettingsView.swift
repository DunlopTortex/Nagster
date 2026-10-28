import NagsterCore
import SwiftUI
import UIKit

/// Cloud sync or linking to the PC, pausing this phone's alerts, and the permissions alarms need.
struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var close
    @State private var password = ""
    @State private var signingIn = false
    @State private var signInProblem: String?
    @State private var notificationsAllowed = true
    @AppStorage(Appearance.storageKey) private var appearance = Appearance.auto
    @AppStorage("cloudUseOwnProject") private var useOwnProject = false
    @AppStorage("cloudCarry") private var carry = true
    @AppStorage("cloudProjectURL") private var cloudURL = ""
    @AppStorage("cloudPublishableKey") private var cloudKey = ""
    @AppStorage("cloudEmail") private var cloudEmail = ""
    @State private var cloudPassword = ""
    @State private var passphrase = ""
    @State private var passphraseAgain = ""
    @State private var cloudWorking = false
    @State private var cloudMessage: String?
    @State private var confirmingDelete = false

    private static let pauseChoices: [(String, TimeInterval?)] =
        [("30 minutes", 1800), ("1 hour", 3600), ("4 hours", 14_400), ("1 day", 86_400), ("Until I resume", nil)]

    var body: some View {
        @Bindable var model = model
        NavigationStack {
            Form {
                cloudSection

                if !model.isCloudOn {
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
                }

                Section("Appearance") {
                    Picker("Theme", selection: $appearance) {
                        ForEach(Appearance.allCases) { Text($0.label).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .onChange(of: appearance) { model.settingsChanged() }
                }

                Section {
                    Toggle("Nag until answered", isOn: $model.naggingEnabled)
                    Picker("Nag again after", selection: $model.nagInterval) {
                        ForEach(AppModel.nagIntervalChoices, id: \.self) { Text($0.label).tag($0) }
                    }
                    .disabled(!model.naggingEnabled)
                } header: {
                    Text("Reminders")
                } footer: {
                    Text(model.naggingEnabled
                         ? "How long an unanswered alarm waits before this iPhone reminds you again. Each alarm can have "
                           + "its own length, or none. The PC has its own setting."
                         : "Each alarm alerts once on this iPhone, then waits quietly. The PC still nags.")
                }

                SnoozeChoicesSection()

                Section {
                    Toggle("Only during alarm hours", isOn: $model.alarmHours.enabled)
                    if model.alarmHours.enabled {
                        DatePicker("From", selection: time(\.startMinutes), displayedComponents: .hourAndMinute)
                        DatePicker("To", selection: time(\.endMinutes), displayedComponents: .hourAndMinute)
                    }
                } header: {
                    Text("Alarm hours")
                } footer: {
                    Text("Outside these hours this iPhone stays silent unless the alarm is Important. Anything that comes "
                         + "due waits, and alerts when the hours start. The PC has its own hours.")
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

    @ViewBuilder private var cloudSection: some View {
        Section {
            if let book = model.cloudBook {
                LabeledContent("Account", value: book.email)
                Button("Sync now") { Task { await model.syncNow() } }
                    .disabled(cloudWorking || model.syncStatus == .syncing)
                Button("Turn off") { Task { await cloudAction { try await model.turnOffCloud(deleteAccount: false); return nil } } }
                    .disabled(cloudWorking)
                Button("Delete account…", role: .destructive) { confirmingDelete = true }
                    .disabled(cloudWorking)
                ForEach(model.rejected, id: \.self) { message in
                    Label(message, systemImage: "exclamationmark.circle").font(.footnote).foregroundStyle(.secondary)
                }
            } else {
                TextField("Email", text: $cloudEmail)
                    .textContentType(.username)
                    .keyboardType(.emailAddress)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                SecureField("Account password", text: $cloudPassword)
                    .textContentType(.password)
                SecureField("Sync passphrase", text: $passphrase)
                    .textContentType(.newPassword)
                SecureField("Passphrase again (first device only)", text: $passphraseAgain)
                    .textContentType(.newPassword)
                Button {
                    Task { await turnOnCloud(createAccount: false) }
                } label: {
                    if cloudWorking { ProgressView() } else { Text("Turn on") }
                }
                .disabled(!canTurnOn)
                Button("Create account") { Task { await turnOnCloud(createAccount: true) } }
                    .disabled(!canTurnOn)
                Toggle("Set up my other Apple devices too", isOn: $carry)
                Toggle("Use my own Supabase project", isOn: $useOwnProject)
                if useOwnProject {
                    TextField("Project URL, e.g. https://abcd.supabase.co", text: $cloudURL)
                        .textContentType(.URL)
                        .keyboardType(.URL)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                    TextField("Publishable key", text: $cloudKey)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                }
            }
            if let cloudMessage {
                Text(cloudMessage).foregroundStyle(.red).font(.footnote)
            }
        } header: {
            Text("Cloud sync (optional)")
        } footer: {
            Text("Keeps your iPhones and your PC in step from anywhere, with or without a PC. Everything is encrypted on this "
                 + "iPhone with your sync passphrase (at least \(CloudCrypto.minimumPassphrase) characters) before it's sent; the "
                 + "cloud only ever sees ciphertext, so nobody else can read your alarms, Nagster included. Forget the passphrase "
                 + "and the cloud copy can't be read by anyone; your alarms stay on your devices. With your other Apple devices "
                 + "set up too, your account and key travel in iCloud Keychain (which Apple encrypts end to end), and they join "
                 + "by themselves, with this iPhone's settings. Cloud sync replaces syncing with the PC over your network.")
        }
        .confirmationDialog("Delete your cloud sync account?", isPresented: $confirmingDelete, titleVisibility: .visible) {
            Button("Delete", role: .destructive) {
                Task { await cloudAction { try await model.turnOffCloud(deleteAccount: true); return nil } }
            }
        } message: {
            Text("Your account, everything in the cloud and the encryption vault are deleted for good, and cloud sync turns "
                 + "off on all your devices. The alarms on this iPhone and on each device stay as they are.")
        }
    }

    private var canTurnOn: Bool {
        !cloudWorking && !cloudEmail.isEmpty && !cloudPassword.isEmpty && !passphrase.isEmpty
            && (!useOwnProject || (!cloudURL.isEmpty && !cloudKey.isEmpty))
    }

    private func turnOnCloud(createAccount: Bool) async {
        await cloudAction {
            // Nagster's own project, unless the user brings theirs.
            let message = try await model.turnOnCloud(projectURL: useOwnProject ? cloudURL : CloudClient.defaultProjectURL,
                                                      publishableKey: useOwnProject ? cloudKey : CloudClient.defaultPublishableKey,
                                                      email: cloudEmail, password: cloudPassword, passphrase: passphrase,
                                                      passphraseAgain: passphraseAgain, carry: carry, createAccount: createAccount)
            if message == nil { cloudPassword = ""; passphrase = ""; passphraseAgain = "" }
            return message
        }
    }

    /// Runs a cloud sync action with its buttons disabled; what it returns, or why it failed, shows under them.
    private func cloudAction(_ action: () async throws -> String?) async {
        cloudWorking = true
        cloudMessage = nil
        defer { cloudWorking = false }
        do { cloudMessage = try await action() } catch { cloudMessage = error.localizedDescription }
    }

    /// A time picker's value for one end of the alarm hours, stored as minutes after midnight.
    private func time(_ end: WritableKeyPath<AlarmHours, Int>) -> Binding<Date> {
        Binding(
            get: {
                let minutes = model.alarmHours[keyPath: end]
                return Calendar.current.date(bySettingHour: minutes / 60, minute: minutes % 60, second: 0, of: .now) ?? .now
            },
            set: { date in
                let parts = Calendar.current.dateComponents([.hour, .minute], from: date)
                model.alarmHours[keyPath: end] = (parts.hour ?? 0) * 60 + (parts.minute ?? 0)
            })
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
