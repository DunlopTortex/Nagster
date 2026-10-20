import OSLog
import SwiftData
import SwiftUI
import UserNotifications

@main
struct NagsterApp: App {
    @Environment(\.scenePhase) private var scenePhase
    @State private var model: AppModel
    /// Kept for the app's lifetime: the notification centre holds its delegate weakly.
    private let responder: NotificationResponder

    init() {
        let container: ModelContainer
        do {
            container = try AlarmRepository.makeContainer()
        } catch {
            // Keeps the app usable (alarms still sync from the PC) if the store can't be opened.
            Logger(subsystem: "com.dgutilities.nagster", category: "storage")
                .fault("Couldn't open the alarm store: \(error.localizedDescription, privacy: .public)")
            container = (try? AlarmRepository.makeContainer(inMemory: true)) ?? Self.emptyContainer()
        }
        let model = AppModel(repository: AlarmRepository(container: container))
        AppModel.current = model
        _model = State(initialValue: model)
        responder = NotificationResponder(model: model)
        UNUserNotificationCenter.current().delegate = responder
    }

    var body: some Scene {
        WindowGroup {
            AgendaView()
                .environment(model)
                .task {
                    await model.prepareAlerts()
                    await model.syncNow()
                }
        }
        .onChange(of: scenePhase) {
            switch scenePhase {
            case .active:
                Task {
                    await model.syncNow()
                    await model.refreshAlerts()
                }
            case .background:
                BackgroundRefresh.schedule()
            default:
                break
            }
        }
        .backgroundTask(.appRefresh(BackgroundRefresh.identifier)) { [model] in
            await model.backgroundRefresh()
        }
    }

    private static func emptyContainer() -> ModelContainer {
        // An in-memory store with this schema can only fail if SwiftData itself is unusable.
        guard let container = try? ModelContainer(for: AlarmRepository.schema, configurations: ModelConfiguration(isStoredInMemoryOnly: true))
        else { preconditionFailure("SwiftData is unavailable") }
        return container
    }
}
