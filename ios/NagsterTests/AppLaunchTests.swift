import NagsterCore
import Testing
@testable import Nagster

/// Proves the app target builds against NagsterCore and its views construct in the Simulator.
@MainActor
struct AppLaunchTests {
    @Test func agendaViewBuilds() {
        _ = AgendaView().body
    }

    @Test func coreIsLinked() {
        #expect(SnoozeDuration("5m")?.label == "5 minutes")
    }
}
