import SwiftUI
import UIKit

/// Shares text between the iPhone and the PC's clipboard, like the Clipboard tab of the PC's web interface.
struct ClipboardView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.scenePhase) private var scenePhase
    @State private var toSend = ""
    @State private var onPC: PCClipboard?
    @State private var loading = false
    @State private var sending = false
    @State private var problem: String?
    /// Why the PC's clipboard couldn't be read (not linked, not reachable), shown in place of its text.
    @State private var unavailable: String?
    @State private var notice: String?
    @State private var checkedAt: Date?
    @FocusState private var editing: Bool

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextEditor(text: $toSend)
                        .frame(minHeight: 120)
                        .focused($editing)
                        .overlay(alignment: .topLeading) {
                            if toSend.isEmpty {
                                Text("Paste or type text to send to the PC's clipboard.")
                                    .foregroundStyle(.tertiary)
                                    .padding(.top, 8)
                                    .padding(.leading, 5)
                                    .allowsHitTesting(false)
                            }
                        }
                    HStack {
                        // The system Paste button reads the iPhone's clipboard without iOS asking for permission.
                        PasteButton(payloadType: String.self) { strings in
                            let pasted = strings.joined(separator: "\n")
                            Task { @MainActor in toSend = pasted }
                        }
                        .labelStyle(.titleAndIcon)
                        Spacer()
                        if !toSend.isEmpty {
                            Button("Clear", role: .destructive) { toSend = "" }
                                .buttonStyle(.borderless)
                        }
                    }
                    Button {
                        Task { await send() }
                    } label: {
                        if sending { ProgressView() } else { Label("Send to PC", systemImage: "arrow.up.doc") }
                    }
                    .disabled(toSend.isEmpty || sending)
                } header: {
                    Text("Send to the PC")
                }

                Section {
                    if let text = onPC?.text {
                        Text(text)
                            .font(.body.monospaced())
                            .textSelection(.enabled)
                            .lineLimit(20)
                        Button("Copy to iPhone", systemImage: "doc.on.doc") {
                            UIPasteboard.general.string = text
                            notice = "Copied to the iPhone's clipboard."
                        }
                    } else if let onPC {
                        Text(onPC.hasOtherContent
                             ? "The PC clipboard holds something other than text, such as an image or files."
                             : "The PC clipboard is empty.")
                            .foregroundStyle(.secondary)
                    } else if let unavailable {
                        Text(unavailable).foregroundStyle(.secondary)
                    } else if !loading {
                        Text("Not checked yet.").foregroundStyle(.secondary)
                    }
                    Button {
                        Task { await load() }
                    } label: {
                        if loading { ProgressView() } else { Label("Refresh", systemImage: "arrow.clockwise") }
                    }
                    .disabled(loading)
                } header: {
                    Text("On the PC clipboard")
                } footer: {
                    if let checkedAt {
                        Text("Checked \(checkedAt.formatted(date: .omitted, time: .standard))")
                    }
                }
            }
            .scrollContentBackground(.hidden)
            .background(Theme.canvas)
            .navigationTitle("Clipboard")
            .toolbar {
                ToolbarItemGroup(placement: .keyboard) {
                    Spacer()
                    Button("Done") { editing = false }
                }
            }
            .refreshable { await load() }
            .task { await load() }
            .onChange(of: scenePhase) {
                if scenePhase == .active { Task { await load() } }
            }
            .alert("Clipboard", isPresented: Binding(get: { problem != nil }, set: { if !$0 { problem = nil } })) {
                Button("OK", role: .cancel) {}
            } message: {
                Text(problem ?? "")
            }
            .overlay(alignment: .bottom) {
                if let notice {
                    Text(notice)
                        .font(.subheadline)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 10)
                        .background(.thinMaterial, in: Capsule())
                        .padding(.bottom, 12)
                        .transition(.opacity)
                        .task {
                            try? await Task.sleep(for: .seconds(2))
                            withAnimation { self.notice = nil }
                        }
                }
            }
        }
    }

    private func load() async {
        guard !loading else { return }
        loading = true
        defer { loading = false }
        do {
            onPC = try await model.readPCClipboard()
            unavailable = nil
            checkedAt = Date()
        } catch {
            onPC = nil
            unavailable = error.localizedDescription
        }
    }

    private func send() async {
        sending = true
        defer { sending = false }
        do {
            try await model.sendToPCClipboard(toSend)
            editing = false
            notice = "Sent to the PC's clipboard."
            await load()
        } catch {
            problem = error.localizedDescription
        }
    }
}
