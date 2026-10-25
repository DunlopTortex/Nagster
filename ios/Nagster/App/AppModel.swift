import CryptoKit
import Foundation
import NagsterCore
import Observation
import OSLog

/// The app's state and everything the user can do. Each action changes the phone's copy straight away, is queued
/// for the PC, saved, and the phone's alerts are re-planned; a sync follows shortly after. With cloud sync on, the
/// queue goes to the cloud instead, end-to-end encrypted, where a running PC (or, without one, this iPhone) applies it.
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
    /// The cloud account and bookkeeping; nil while cloud sync is off.
    private(set) var cloudBook: CloudBook?
    /// Something about cloud sync to tell the user wherever they are, e.g. after the confirmation link opened the app.
    var cloudNotice: String?

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
            settingsChanged()
        }
    }

    /// Alarms whose reminders are turned off on this phone (not synced: the PC keeps nagging).
    private(set) var noNagAlarms: Set<UUID> {
        didSet {
            defaults.set(noNagAlarms.map(\.uuidString), forKey: Keys.noNagAlarms)
            settingsChanged()
        }
    }

    /// The hours this phone rings iPhone-only alarms; ones that fall outside are snoozed to the start.
    var alarmHours: AlarmHours {
        didSet {
            defaults.set(try? JSONEncoder().encode(alarmHours), forKey: Keys.alarmHours)
            replanAlerts()
            settingsChanged()
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
            settingsChanged()
        }
    }

    /// This phone's own snooze choices, or nil to use the PC's (or the defaults, without a PC). Not synced:
    /// the PC keeps its own list.
    var ownSnoozeChoices: [SnoozeDuration]? {
        didSet {
            defaults.set(ownSnoozeChoices?.map(\.description), forKey: Keys.snoozeChoices)
            notifications.registerActions(for: snoozeChoices)
            replanAlerts()
            settingsChanged()
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
    @ObservationIgnored private let cloudStore: CloudStore
    @ObservationIgnored private var cloudSession: CloudSession?
    @ObservationIgnored private var cloudKey: SymmetricKey?
    /// An account created but not yet confirmed: what's needed to finish when the confirmation link opens the app.
    /// Kept in memory only; if the app was closed meanwhile, the user taps Turn on instead.
    @ObservationIgnored private var pendingSignUp: SignUp?
    /// Set once joining with an account carried in from another device has failed, so it's tried once per launch.
    @ObservationIgnored private var carriedJoinFailed = false
    /// Syncs again shortly while actions wait for a PC to apply them; how many have run without them all answered.
    @ObservationIgnored private var followUpTask: Task<Void, Never>?
    @ObservationIgnored private var followUps = 0

    private struct SignUp {
        var projectURL: String
        var publishableKey: String
        var email: String
        var password: String
        var passphrase: String
        var passphraseAgain: String
        var carry: Bool
    }

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
         importantAlarms: ImportantAlarmScheduler = ImportantAlarmScheduler(), defaults: UserDefaults = .standard,
         cloudStore: CloudStore = .standard) {
        self.repository = repository
        self.cloudStore = cloudStore
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
        cloudBook = cloudStore.load()
        syncStatus = client == nil && cloudBook == nil ? .notSetUp : .idle(lastSynced: defaults.object(forKey: Keys.lastSynced) as? Date)
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

    /// Syncs through the cloud when cloud sync is on, otherwise with the PC over the network.
    func syncNow() async {
        if cloudBook == nil, !carriedJoinFailed, syncStatus != .syncing, let carried = cloudStore.carried {
            await joinCarried(carried)
        }
        if cloudBook != nil {
            await syncCloud()
            return
        }
        guard let client else {
            syncStatus = .notSetUp
            return
        }
        if syncStatus == .syncing { return }
        syncStatus = .syncing
        let sending = pendingOps
        do {
            let reply = try await client.sync(SyncRequest(deviceId: deviceID, sinceRevision: state.revision, ops: sending))
            take(reply)
        } catch {
            log.notice("Sync failed: \(error.localizedDescription, privacy: .public)")
            syncStatus = .failed(error.localizedDescription)
        }
    }

    /// Takes in a reply, from the PC or assembled from the cloud: the actions it answered leave the queue, and the
    /// rest are applied again on top, so they aren't lost from view.
    private func take(_ reply: SyncResponse) {
        let answered = Set(reply.results.map(\.opId))
        pendingOps.removeAll { answered.contains($0.opId) }
        state.merge(reply, unsent: pendingOps, now: now)
        if !reply.results.isEmpty { rejected = reply.results.filter { !$0.applied }.compactMap(\.message) }
        repository.save(state, ops: pendingOps)
        notifications.registerActions(for: snoozeChoices)
        replanAlerts()
        let synced = Date()
        defaults.set(synced, forKey: Keys.lastSynced)
        syncStatus = .idle(lastSynced: synced)
    }

    // MARK: - Cloud sync

    var isCloudOn: Bool { cloudBook != nil }

    /// A setting the account's iPhones share changed here: send it.
    func settingsChanged() {
        if isCloudOn { scheduleSync() }
    }

    /// Signs in (or creates the account), opens the account's vault with the passphrase, or creates the vault if this
    /// is the first device, and syncs. Creating a vault needs `passphraseAgain` to match, as a typo there would lock
    /// every later device out. With `carry`, the account also goes into iCloud Keychain, so the user's other Apple
    /// devices join by themselves.
    /// - Returns: Nil when cloud sync is on; otherwise what to tell the user (e.g. to confirm their email first).
    func turnOnCloud(projectURL text: String, publishableKey: String, email: String, password: String, passphrase: String,
                     passphraseAgain: String, carry: Bool, createAccount: Bool) async throws -> String? {
        guard let url = CloudClient.projectURL(from: text) else {
            throw CloudError.server("Enter the project URL, e.g. https://abcd.supabase.co (HTTPS only).")
        }
        let signUp = SignUp(projectURL: url.absoluteString, publishableKey: publishableKey.trimmingCharacters(in: .whitespaces),
                            email: email.trimmingCharacters(in: .whitespaces), password: password, passphrase: passphrase,
                            passphraseAgain: passphraseAgain, carry: carry)
        let client = CloudClient(projectURL: url, publishableKey: signUp.publishableKey)
        let signedIn = createAccount ? try await client.signUp(email: signUp.email, password: password)
            : try await client.signIn(email: signUp.email, password: password)
        guard let session = signedIn else {
            pendingSignUp = signUp
            return "Account created. Open the link Supabase emailed you to confirm it, and cloud sync turns on. " + CloudClient.checkSpam
        }
        try await finishTurningOn(signUp, client: client, session: session)
        return nil
    }

    /// Run when a nagster:// link opens the app: the email confirmation link brings the browser back here.
    func handleLink(_ url: URL) async {
        guard url.scheme == "nagster", url.host() == "auth-callback" else { return }
        // Supabase puts a failure (an expired link, say) in the fragment or the query.
        let parts = (url.fragment(percentEncoded: false) ?? "") + "&" + (url.query(percentEncoded: false) ?? "")
        let fields = Dictionary(parts.split(separator: "&").compactMap { pair -> (String, String)? in
            let kv = pair.split(separator: "=", maxSplits: 1)
            return kv.count == 2 ? (String(kv[0]), String(kv[1]).replacingOccurrences(of: "+", with: " ")) : nil
        }, uniquingKeysWith: { first, _ in first })
        if let problem = fields["error_description"] {
            cloudNotice = "The confirmation link didn't work (\(problem)). Tap Create account again for a new one."
            return
        }
        guard let signUp = pendingSignUp, let projectURL = URL(string: signUp.projectURL) else {
            cloudNotice = "Your email is confirmed. Open Settings → Cloud sync and tap Turn on."
            return
        }
        let client = CloudClient(projectURL: projectURL, publishableKey: signUp.publishableKey)
        do {
            try await finishTurningOn(signUp, client: client, session: try await client.signIn(email: signUp.email, password: signUp.password))
            cloudNotice = "Your email is confirmed, and cloud sync is on."
        } catch {
            cloudNotice = "Your email is confirmed, but cloud sync couldn't turn on: \(error.localizedDescription)"
        }
    }

    private func finishTurningOn(_ signUp: SignUp, client: CloudClient, session: CloudSession) async throws {
        let passphrase = signUp.passphrase
        let userId = session.userId
        let dataKey: SymmetricKey
        let vault: CloudVault
        if let existing = try await client.vault(session) {
            vault = existing
            dataKey = try await Self.open(existing, passphrase: passphrase, userId: userId)
        } else {
            guard passphrase.count >= CloudCrypto.minimumPassphrase else {
                throw CloudCryptoError.unreadable("Choose a sync passphrase of at least \(CloudCrypto.minimumPassphrase) characters.")
            }
            guard signUp.passphraseAgain == passphrase else {
                throw CloudCryptoError.unreadable("This is your first device on cloud sync: type the passphrase again, the same, to confirm it.")
            }
            let newKey = CloudCrypto.newDataKey()
            let newKeyBytes = newKey.withUnsafeBytes { Data($0) }
            let salt = CloudCrypto.newSalt()
            // PBKDF2 takes a moment: off the main thread, passing plain bytes across.
            let wrapped = try await Task.detached {
                try CloudCrypto.wrap(SymmetricKey(data: newKeyBytes), with: CloudCrypto.deriveKey(passphrase: passphrase, salt: salt),
                                     userId: userId)
            }.value
            let created = try await client.createVault(session, salt: salt.base64EncodedString(), iterations: CloudCrypto.iterations,
                                                       wrappedKey: wrapped)
            guard let made = try await client.vault(session) else { throw CloudError.server("The new vault couldn't be read back.") }
            vault = made
            // Another device made one meanwhile: use that.
            dataKey = created ? newKey : try await Self.open(made, passphrase: passphrase, userId: userId)
        }
        start(CloudBook(projectURL: signUp.projectURL, publishableKey: signUp.publishableKey, email: signUp.email, userId: userId,
                        vaultId: vault.vaultId), session: session, dataKey: dataKey)
        if signUp.carry {
            cloudStore.setCarried(CarriedAccount(projectURL: signUp.projectURL, publishableKey: signUp.publishableKey, email: signUp.email,
                                                 password: signUp.password, userId: userId, vaultId: vault.vaultId,
                                                 dataKey: dataKey.withUnsafeBytes { Data($0) }.base64EncodedString()))
        }
        pendingSignUp = nil
        await syncNow()
    }

    private static func open(_ vault: CloudVault, passphrase: String, userId: String) async throws -> SymmetricKey {
        guard !passphrase.isEmpty else {
            throw CloudCryptoError.unreadable("Enter your sync passphrase: this account already has cloud sync.")
        }
        guard let salt = Data(base64Encoded: vault.kdfSalt) else { throw CloudCryptoError.unreadable("The cloud vault couldn't be read.") }
        let wrapped = vault.wrappedKey
        let iterations = vault.kdfIterations
        let keyBytes = try await Task.detached {
            try CloudCrypto.unwrap(wrapped, with: CloudCrypto.deriveKey(passphrase: passphrase, salt: salt, iterations: iterations),
                                   userId: userId).withUnsafeBytes { Data($0) }
        }.value
        return SymmetricKey(data: keyBytes)
    }

    private func start(_ book: CloudBook, session: CloudSession, dataKey: SymmetricKey) {
        cloudStore.setRefreshToken(session.refreshToken)
        cloudStore.setDataKey(dataKey)
        cloudStore.save(book)
        cloudBook = book
        cloudSession = session
        cloudKey = dataKey
        syncStatus = .idle(lastSynced: nil)
    }

    /// Joins cloud sync with the account another of the user's Apple devices left in iCloud Keychain: signs in with a
    /// session of its own, checks the vault is still the one the key belongs to, and syncs everything.
    private func joinCarried(_ carried: CarriedAccount) async {
        guard let projectURL = URL(string: carried.projectURL), let keyBytes = Data(base64Encoded: carried.dataKey) else { return }
        let client = CloudClient(projectURL: projectURL, publishableKey: carried.publishableKey)
        do {
            let session = try await client.signIn(email: carried.email, password: carried.password)
            guard session.userId == carried.userId, try await client.vault(session)?.vaultId == carried.vaultId else {
                cloudStore.delete() // the account was deleted or started afresh since: nothing here can open it
                return
            }
            start(CloudBook(projectURL: carried.projectURL, publishableKey: carried.publishableKey, email: carried.email,
                            userId: carried.userId, vaultId: carried.vaultId), session: session, dataKey: SymmetricKey(data: keyBytes))
            log.notice("Joined cloud sync with the account from another device.")
        } catch {
            carriedJoinFailed = true
            syncStatus = .failed("Couldn't set up cloud sync from your other device: \(error.localizedDescription) Turn it on in Settings instead.")
        }
    }

    /// Stops cloud sync and forgets the account on this iPhone; the alarms here are untouched. With `deleteAccount`,
    /// first deletes the account itself and everything in the cloud; other devices stop when they next sync.
    func turnOffCloud(deleteAccount: Bool) async throws {
        if let book = cloudBook, let client = cloudClient(book) {
            if deleteAccount {
                try await client.deleteAccount(try await cloudSession(book, client))
            } else if let session = try? await cloudSession(book, client) {
                await client.signOut(session)
            }
        }
        forgetCloud()
    }

    /// - Parameter keepCarried: Leaves the account in iCloud Keychain, so this device joins again with it (after its
    ///   session merely expired, say).
    private func forgetCloud(keepCarried: Bool = false) {
        cloudStore.delete(keepCarried: keepCarried)
        cloudBook = nil
        cloudSession = nil
        cloudKey = nil
        followUpTask?.cancel()
        syncStatus = client == nil ? .notSetUp : .idle(lastSynced: nil)
    }

    private func cloudClient(_ book: CloudBook) -> CloudClient? {
        URL(string: book.projectURL).map { CloudClient(projectURL: $0, publishableKey: book.publishableKey) }
    }

    private func cloudSession(_ book: CloudBook, _ client: CloudClient) async throws -> CloudSession {
        if let session = cloudSession, session.userId == book.userId, session.expiresAt > Date(timeIntervalSinceNow: 120) {
            return session
        }
        guard let token = cloudStore.refreshToken else { throw CloudError.signInNeeded }
        let session = try await client.refresh(token)
        // Supabase rotates refresh tokens: the old one stops working soon, so keep the new one straight away.
        cloudStore.setRefreshToken(session.refreshToken)
        cloudSession = session
        return session
    }

    /// One cloud round (see `CloudRound`), then the iPhones' shared settings.
    private func syncCloud() async {
        guard var book = cloudBook, let client = cloudClient(book), syncStatus != .syncing else { return }
        syncStatus = .syncing
        // Alarms made on this iPhone before it joined, which the shared copy doesn't have yet.
        let before = book.joining ? state.definitions : [:]
        do {
            let session = try await cloudSession(book, client)
            guard let key = cloudKey ?? cloudStore.dataKey else { throw CloudError.signInNeeded }
            cloudKey = key
            guard try await client.vault(session)?.vaultId == book.vaultId else {
                forgetCloud()
                syncStatus = .failed("Cloud sync was turned off here: the account was deleted from another device.")
                return
            }
            let reply = try await CloudRound(client: client, session: session, key: key).run(pendingOps, book: &book)
            try await syncSettings(client, session, key: key, book: &book)
            guard cloudBook?.userId == book.userId else { return } // turned off meanwhile
            cloudBook = book
            cloudStore.save(book)
            take(reply)
            let missing = before.values.filter { state.definitions[$0.id] == nil }
            for definition in missing { pendingOps.append(state.save(definition)) }
            if !missing.isEmpty { repository.save(state, ops: pendingOps) }
            followUp()
        } catch CloudError.signInNeeded {
            forgetCloud(keepCarried: true)
            syncStatus = .failed(CloudError.signInNeeded.errorDescription ?? "")
        } catch {
            log.notice("Cloud sync failed: \(error.localizedDescription, privacy: .public)")
            syncStatus = .failed(error.localizedDescription)
        }
    }

    /// While actions wait for a running PC to apply them (it does every few seconds), syncs again to collect the
    /// answers: soon at first, then less often, giving up after about five minutes; any later sync collects them.
    private func followUp() {
        followUpTask?.cancel()
        guard !pendingOps.isEmpty, followUps < 8 else {
            followUps = 0
            return
        }
        let delay = min(5 << min(followUps, 3), 60) // 5, 10, 20, 40, then every minute
        followUps += 1
        followUpTask = Task {
            try? await Task.sleep(for: .seconds(delay))
            if !Task.isCancelled { await syncNow() }
        }
    }

    /// This iPhone's settings that every iPhone on the account shares.
    private var sharedSettings: IPhoneSettings {
        IPhoneSettings(nagInterval: nagInterval.description, naggingEnabled: naggingEnabled, noNagAlarms: Array(noNagAlarms),
                       alarmHours: alarmHours, snoozeChoices: ownSnoozeChoices?.map(\.description) ?? [],
                       appearance: defaults.string(forKey: Appearance.storageKey) ?? Appearance.auto.rawValue)
    }

    /// Takes the shared settings from the cloud if another iPhone changed them (or this one just joined), or sends
    /// this one's if they changed here.
    private func syncSettings(_ client: CloudClient, _ session: CloudSession, key: SymmetricKey, book: inout CloudBook) async throws {
        let scope = IPhoneSettings.scope
        let context = CloudCrypto.settingsContext(book.userId, scope)
        let local = sharedSettings
        let remote = try await client.settings(session, scope: scope).map { row in
            (seq: row.seq, settings: try CloudCrypto.open(IPhoneSettings.self, row.payload, key: key, context: context))
        }
        switch SettingsSync.step(local: local, remote: remote, seenSeq: book.settingsSeq, seenDigest: book.settingsDigest) {
        case .none:
            return
        case .apply(let settings, let seq):
            if let interval = settings.nagInterval.flatMap(SnoozeDuration.init), interval != nagInterval { nagInterval = interval }
            if let nagging = settings.naggingEnabled, nagging != naggingEnabled { naggingEnabled = nagging }
            if let alarms = settings.noNagAlarms.map(Set.init), alarms != noNagAlarms { noNagAlarms = alarms }
            if let hours = settings.alarmHours, hours != alarmHours { alarmHours = hours }
            if let choices = settings.snoozeChoices {
                let own = choices.compactMap(SnoozeDuration.init)
                let wanted = own.isEmpty ? nil : own
                if wanted != ownSnoozeChoices { ownSnoozeChoices = wanted }
            }
            if let appearance = settings.appearance, Appearance(rawValue: appearance) != nil {
                defaults.set(appearance, forKey: Appearance.storageKey)
            }
            book.settingsSeq = seq
            book.settingsDigest = sharedSettings.digest
        case .push:
            book.settingsSeq = try await client.saveSettings(session, scope: scope,
                                                             payload: try CloudCrypto.seal(local, key: key, context: context))
            book.settingsDigest = local.digest
        }
    }
}
