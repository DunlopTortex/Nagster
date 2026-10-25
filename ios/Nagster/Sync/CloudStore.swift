import CryptoKit
import NagsterCore
import Foundation
import OSLog

/// What a new iPhone or iPad needs to join cloud sync by itself, carried in iCloud Keychain: the account to sign in
/// with (its own session, as Supabase rotates them), and the data key, so the passphrase needn't be typed again.
struct CarriedAccount: Codable, Equatable {
    var projectURL: String
    var publishableKey: String
    var email: String
    var password: String
    var userId: String
    var vaultId: String
    var dataKey: String
}

/// Where cloud sync keeps its account and bookkeeping on this iPhone. The book (account details, and what was last
/// seen of the shared copy) is a file protected by iOS data protection; the session and the data key that opens
/// everything are in the Keychain, on this device only and never in backups. If the user chose to, a
/// `CarriedAccount` also goes into iCloud Keychain, for their other Apple devices.
struct CloudStore {
    var fileURL: URL
    /// Keeps the tests' Keychain items apart from the app's.
    var keychainPrefix = "cloud"

    static var standard: CloudStore {
        let folder = (try? FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true))
            ?? FileManager.default.temporaryDirectory
        return CloudStore(fileURL: folder.appending(component: "cloud_sync.json"))
    }

    private var refreshAccount: String { "\(keychainPrefix)-refresh-token" }
    private var keyAccount: String { "\(keychainPrefix)-data-key" }
    private var carriedAccount: String { "\(keychainPrefix)-account" }
    private let log = Logger(subsystem: "com.dgutilities.nagster", category: "cloud")

    /// The book, if cloud sync is on: written, and with its secrets in the Keychain.
    func load() -> CloudBook? {
        guard let data = try? Data(contentsOf: fileURL) else { return nil }
        do {
            let book = try JSONDecoder().decode(CloudBook.self, from: data)
            return refreshToken != nil && dataKey != nil ? book : nil
        } catch {
            log.error("Cloud sync's book couldn't be read: \(error.localizedDescription, privacy: .public)")
            return nil
        }
    }

    func save(_ book: CloudBook) {
        do {
            // Readable after first unlock, so background refresh can sync.
            try JSONEncoder().encode(book).write(to: fileURL, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
        } catch {
            log.error("Cloud sync's book couldn't be saved: \(error.localizedDescription, privacy: .public)")
        }
    }

    /// Forgets the account on this iPhone, and unless `keepCarried`, on the user's other Apple devices too, so none
    /// joins by itself again. The alarms are untouched, and devices already syncing carry on.
    func delete(keepCarried: Bool = false) {
        try? FileManager.default.removeItem(at: fileURL)
        Keychain.delete(refreshAccount)
        Keychain.delete(keyAccount)
        if !keepCarried { Keychain.delete(carriedAccount, synchronizable: true) }
    }

    /// The account carried in from another Apple device, if any.
    var carried: CarriedAccount? {
        Keychain.read(carriedAccount, synchronizable: true).flatMap { try? JSONDecoder().decode(CarriedAccount.self, from: Data($0.utf8)) }
    }

    func setCarried(_ account: CarriedAccount) {
        guard let data = try? JSONEncoder().encode(account), let text = String(data: data, encoding: .utf8) else { return }
        Keychain.write(text, for: carriedAccount, synchronizable: true)
    }

    var refreshToken: String? { Keychain.read(refreshAccount) }

    func setRefreshToken(_ token: String) { Keychain.write(token, for: refreshAccount) }

    var dataKey: SymmetricKey? {
        Keychain.read(keyAccount).flatMap { Data(base64Encoded: $0) }.map { SymmetricKey(data: $0) }
    }

    func setDataKey(_ key: SymmetricKey) {
        Keychain.write(key.withUnsafeBytes { Data($0) }.base64EncodedString(), for: keyAccount)
    }
}
