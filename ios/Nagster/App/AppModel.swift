import Foundation
import NagsterCore
import Observation
import OSLog

/// The app's state and everything the user can do. Each action changes the phone's copy straight away, is queued
/// for the PC, saved, and the phone's alerts are re-planned; a sync follows shortly after.
@Observable
final class AppModel {
    /// The running app's model, for App Intents (Important alarm buttons), which iOS creates on its own.
    static var current: AppModel?

    enum SyncStatus: Equatable {
        case notSetUp
        case idle(lastSynced: Date?)
        case syncing
        case failed(String)
    }

    private(set) var state = AlarmState()
    private(set) var pendingOps: [SyncOp] = []
    private(set) var syncStatus: SyncStatus = .notSetUp
    /// Messages for actions the PC turned down at the last sync, e.g. "It was answered on the PC first."
    private(set) var rejected: [String] = []
    /// An occurrence to show, set when the user opens the app from its notification.
    var focused: AlarmOccurrence?

    var pcAddress: String {
        didSet { defaults.set(pcAddress, forKey: Keys.pcAddress) }
    }

    /// While set, this phone sounds nothing until then (the PC keeps its own pause).
    private(set) var pausedUntil: Date? {
        didSet { defaults.set(pausedUntil, forKey: Keys.pausedUntil) }
    }

    /// Whether unanswered alarms remind you again on this phone. Off, each alarm alerts once.
    var naggingEnabled: Bool {
        didSet {
            defaults.set(naggingEnabled, forKey: Keys.naggingEnabled)
            replanAlerts()
        }
    }

    /// Alarms whose reminders are turned off on this phone (not synced: the PC keeps nagging).
    private(set) var noNagAlarms: Set<UUID> {
        didSet { defaults.set(noNagAlarms.map(\.uuidString), forKey: Keys.noNagAlarms) }
    }

    /// The hours this phone rings iPhone-only alarms; ones that fall outside are snoozed to the start.
    var alarmHours: AlarmHours {
        didSet {
            defaults.set(try? JSONEncoder().encode(alarmHours), forKey: Keys.alarmHours)
            replanAlerts()
        }
    }

    /// The choices offered for how long an unanswered alarm waits before this phone reminds you again.
    static let nagIntervalChoices = ["1m", "2m", "5m", "10m", "15m", "30m", "1h"].compactMap(SnoozeDuration.init)
    static let defaultNagInterval = SnoozeDuration(amount: 10, unit: .minutes)

    /// How long an unanswered alarm waits before this phone reminds you again. This phone's own setting,
    /// separate from the PC's automatic snooze length.
    var nagInterval: SnoozeDuration {
        didSet {
            defaults.set(nagInterval.description, forKey: Keys.nagInterval)
            replanAlerts()
        }
    }

    /// This phone's own snooze choices, or nil to use the PC's (or the defaults, without a PC). Not synced:
    /// the PC keeps its own list.
    var ownSnoozeChoices: [SnoozeDuration]? {
        didSet {
            defaults.set(ownSnoozeChoices?.map(\.description), forKey: Keys.snoozeChoices)
            notifications.registerActions(for: snoozeChoices)
            replanAlerts()
        }
    }

    /// The snooze lengths this phone offers, in order; the first is the quick snooze and the Important alarm's.
    var snoozeChoices: [SnoozeDuration] { ownSnoozeChoices ?? state.settings.snoozeDurations }

    @ObservationIgnored private let repository: AlarmRepository
    @ObservationIgnored private let notifications: NotificationScheduler
    @ObservationIgnored private let importantAlarms: ImportantAlarmScheduler
    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private var syncTask: Task<Void, Never>?
    @ObservationIgnored private let log = Logger(subsystem: "com.dgutilities.nagster", category: "model")

    private enum Keys {
        static let pcAddress = "pcAddress"
        static let pausedUntil = "pausedUntil"
        static let nagInterval = "nagInterval"
        static let naggingEnabled = "naggingEnabled"
        static let noNagAlarms = "noNagAlarms"
        static let alarmHours = "alarmHours"
        static let snoozeChoices = "snoozeChoices"
        static let lastSynced = "lastSynced"
        static let deviceID = "deviceID"
    }

    init(repository: AlarmRepository, notifications: NotificationScheduler = NotificationScheduler(),
         importantAlarms: ImportantAlarmScheduler = ImportantAlarmScheduler(), defaults: UserDefaults = .standard) {
        self.repository = repository
        self.notifications = notifications
        self.importantAlarms = importantAlarms
        self.defaults = defaults
        pcAddress = defaults.string(forKey: Keys.pcAddress) ?? ""
        pausedUntil = defaults.object(forKey: Keys.pausedUntil) as? Date
        nagInterval = defaults.string(forKey: Keys.nagInterval).flatMap(SnoozeDuration.init) ?? Self.defaultNagInterval
        naggingEnabled = defaults.object(forKey: Keys.naggingEnabled) as? Bool ?? true
        noNagAlarms = Set((defaults.stringArray(forKey: Keys.noNagAlarms) ?? []).compactMap(UUID.init(uuidString:)))
        alarmHours = defaults.data(forKey: Keys.alarmHours).flatMap { try? JSONDecoder().decode(AlarmHours.self, from: $0) }
            ?? AlarmHours()
        ownSnoozeChoices = defaults.stringArray(forKey: Keys.snoozeChoices).map { $0.compactMap(SnoozeDuration.init) }
            .flatMap { $0.isEmpty ? nil : $0 }
        (state, pendingOps) = repository.load()
        syncStatus = client == nil ? .notSetUp : .idle(lastSynced: defaults.object(forKey: Keys.lastSynced) as? Date)
    }

    var now: WallTime { WallTime(Date()) }

    // MARK: - What the screens show

    func definition(of occurrence: AlarmOccurrence) -> AlarmDefinition? { state.definition(of: occurrence) }

    /// Going off on this phone now.
    var ringing: [AlarmOccurrence] { state.ringing(at: now) }

    /// Coming up in the next `days` days (anything ringing now is left out).
    func upcoming(days: Int = 14) -> [AlarmOccurrence] {
        let now = self.now
        let ringingIDs = Set(state.ringing(at: now).map(\.id))
        return state.waitingOccurrences(through: now.adding(.day, days)).filter { !ringingIDs.contains($0.id) && $0.alertAt > now }
    }

    /// Answered in the last week, newest first.
    var recent: [AlarmOccurrence] {
        let since = now.adding(.day, -7)
        return state.occurrences.values.filter { $0.status.isFinished && $0.scheduledFor >= since }
            .sorted { a, b in a.scheduledFor > b.scheduledFor }
    }

    var isPaused: Bool { pausedUntil.map { $0 > Date() } ?? false }

    /// When an alarm next goes off, which is where the editor starts a repeating alarm from (as on the PC).
    func nextRun(of definitionID: UUID) -> WallTime? {
        state.waitingOccurrences(through: now.adding(.year, 1)).first { $0.definitionId == definitionID }?.scheduledFor
    }

    // MARK: - The user's actions

    func complete(_ occurrence: AlarmOccurrence) { record(state.act(.complete, on: occurrence, now: now), answering: occurrence.id) }

    func dismiss(_ occurrence: AlarmOccurrence) { record(state.act(.dismiss, on: occurrence, now: now), answering: occurrence.id) }

    /// Snoozes, stopping at the deadline if the choice would go past it.
    /// - Returns: A reason if it couldn't be snoozed.
    @discardableResult
    func snooze(_ occurrence: AlarmOccurrence, for duration: SnoozeDuration) -> String? {
        guard let until = AlarmState.snoozeTarget(duration, alertAt: occurrence.alertAt, now: now,
                                                  deadline: occurrence.deadline) else {
            return "Its deadline has passed, so it can't be snoozed."
        }
        record(state.act(.snooze, on: occurrence, until: until, now: now), answering: occurrence.id)
        return nil
    }

    /// - Returns: A reason if it couldn't be moved to `date`.
    @discardableResult
    func reschedule(_ occurrence: AlarmOccurrence, to date: Date) -> String? {
        let target = WallTime(date)
        if target <= now { return "Choose a time in the future." }
        if let deadline = occurrence.deadline, target > deadline {
            return "It must be completed by \(Formatting.dayAndTime(deadline))."
        }
        record(state.act(.reschedule, on: occurrence, until: target, now: now), answering: occurrence.id)
        return nil
    }

    func save(_ definition: AlarmDefinition) { record(state.save(definition)) }

    /// Saves an alarm along with whether it nags on this phone.
    func save(_ definition: AlarmDefinition, nags: Bool) {
        if nags { noNagAlarms.remove(definition.id) } else { noNagAlarms.insert(definition.id) }
        save(definition)
    }

    func delete(_ definitionID: UUID) {
        noNagAlarms.remove(definitionID)
        record(state.delete(definitionID))
    }

    func pause(for length: TimeInterval?) {
        pausedUntil = length.map { Date().addingTimeInterval($0) } ?? .distantFuture
        replanAlerts()
    }

    func resume() {
        pausedUntil = nil
        replanAlerts()
    }

    // Notification and alarm buttons name the occurrence by alarm and time, as it may only have been worked out.
    func complete(_ target: NotificationTarget) {
        if let occurrence = occurrence(for: target) { complete(occurrence) }
    }

    func dismiss(_ target: NotificationTarget) {
        if let occurrence = occurrence(for: target) { dismiss(occurrence) }
    }

    func snooze(_ target: NotificationTarget, for duration: SnoozeDuration) {
        if let occurrence = occurrence(for: target) { snooze(occurrence, for: duration) }
    }

    /// Records a snooze chosen on an Important alarm, whose countdown AlarmKit is already running.
    func snoozeFromAlarm(_ target: NotificationTarget, for duration: SnoozeDuration) {
        guard let occurrence = occurrence(for: target),
              let until = AlarmState.snoozeTarget(duration, alertAt: occurrence.alertAt, now: now,
                                                  deadline: occurrence.deadline) else { return }
        notifications.removeDelivered(for: occurrence.id)
        record(state.act(.snooze, on: occurrence, until: until, now: now))
    }

    func focus(_ target: NotificationTarget) { focused = occurrence(for: target) }

    private func occurrence(for target: NotificationTarget) -> AlarmOccurrence? {
        if let stored = state.occurrences[target.occurrenceID] { return stored }
        let projected = state.definitions[target.definitionID]?.occurrence(at: target.scheduledFor)
        return projected?.id == target.occurrenceID ? projected : nil
    }

    private func record(_ op: SyncOp, answering occurrenceID: UUID? = nil) {
        pendingOps.append(op)
        repository.save(state, ops: pendingOps)
        if let occurrenceID {
            notifications.removeDelivered(for: occurrenceID)
            importantAlarms.stop(occurrenceID)
        }
        replanAlerts()
        scheduleSync()
    }

    // MARK: - Alerts

    /// Asks for notification permission (once) and sets up the notification buttons.
    func prepareAlerts() async {
        _ = await notifications.requestPermission()
        notifications.registerActions(for: snoozeChoices)
        replanAlerts()
    }

    func replanAlerts() {
        Task { await refreshAlerts() }
    }

    /// Re-plans notifications and Important alarms from the current state.
    func refreshAlerts() async {
        holdOutsideAlarmHours()
        let plan = AlertPlanner.plan(state, now: now, pausedUntil: pausedUntil.map { WallTime($0) }, nagEvery: nagInterval,
                                     nagging: naggingEnabled, noNagAlarms: noNagAlarms, alarmHours: alarmHours)
        await notifications.schedule(plan, state: state, alarmKitRingsImportant: importantAlarms.isAuthorized)
        await importantAlarms.schedule(plan, state: state, snooze: snoozeChoices.first)
    }

    /// Run by iOS now and then in the background: sync, then top up the alerts for the days ahead.
    func backgroundRefresh() async {
        BackgroundRefresh.schedule()
        await syncNow()
        await refreshAlerts()
    }

    var importantAlarmsAllowed: Bool { importantAlarms.isAuthorized }

    /// Asks to ring Important alarms full screen; until allowed they ring as ordinary notifications.
    func requestImportantAlarms() async -> Bool {
        let allowed = await importantAlarms.requestAuthorization()
        replanAlerts()
        return allowed
    }

    func notificationsAllowed() async -> Bool { await notifications.isAllowed() }

    // MARK: - Syncing

    private var client: SyncClient? { SyncClient.baseURL(from: pcAddress).map { SyncClient(baseURL: $0) } }

    private var deviceID: String {
        if let id = defaults.string(forKey: Keys.deviceID) { return id }
        let id = UUID().uuidString
        defaults.set(id, forKey: Keys.deviceID)
        return id
    }

    func signIn(password: String) async throws {
        guard let client else { throw SyncError.notSetUp }
        try await client.signIn(password: password)
        await syncNow()
    }

    func signOut() {
        SyncClient.signOut()
        syncStatus = .failed(SyncError.signInNeeded.errorDescription ?? "")
    }

    /// Syncs a couple of seconds after the last change, so a burst of taps goes in one request.
    /// Snoozes iPhone-only alarms that would ring outside the alarm hours until they start, as real snoozes the PC
    /// hears about. Runs whenever alerts are planned, as the phone isn't awake to do it when the alarm comes due.
    private func holdOutsideAlarmHours() {
        let ops = state.holdOutsideAlarmHours(alarmHours, now: now)
        guard !ops.isEmpty else { return }
        pendingOps.append(contentsOf: ops)
        repository.save(state, ops: pendingOps)
        scheduleSync()
    }

    private func scheduleSync() {
        syncTask?.cancel()
        syncTask = Task {
            try? await Task.sleep(for: .seconds(2))
            if !Task.isCancelled { await syncNow() }
        }
    }

    func syncNow() async {
        guard let client else {
            syncStatus = .notSetUp
            return
        }
        if syncStatus == .syncing { return }
        syncStatus = .syncing
        let sending = pendingOps
        do {
            let reply = try await client.sync(SyncRequest(deviceId: deviceID, sinceRevision: state.revision, ops: sending))
            let sent = Set(sending.map(\.opId))
            pendingOps.removeAll { sent.contains($0.opId) }
            state.merge(reply, unsent: pendingOps, now: now)
            rejected = reply.results.filter { !$0.applied }.compactMap(\.message)
            repository.save(state, ops: pendingOps)
            notifications.registerActions(for: snoozeChoices)
            replanAlerts()
            let synced = Date()
            defaults.set(synced, forKey: Keys.lastSynced)
            syncStatus = .idle(lastSynced: synced)
        } catch {
            log.notice("Sync failed: \(error.localizedDescription, privacy: .public)")
            syncStatus = .failed(error.localizedDescription)
        }
    }
}
