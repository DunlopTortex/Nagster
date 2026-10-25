import Foundation
import Security

/// Keeps small secrets in the Keychain. Most (the PC sign-in session, cloud sync's session and data key) stay on this
/// device only, readable after first unlock so background syncs can use them. A `synchronizable` item travels to the
/// user's other Apple devices through iCloud Keychain, which Apple end-to-end encrypts; it stays here if iCloud
/// Keychain is off.
enum Keychain {
    private static let service = "com.dgutilities.nagster"

    static func read(_ account: String, synchronizable: Bool = false) -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
            kSecAttrAccount as String: account, kSecAttrSynchronizable as String: synchronizable,
            kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne
        ]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func write(_ value: String, for account: String, synchronizable: Bool = false) {
        delete(account, synchronizable: synchronizable)
        let item: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account,
            kSecAttrSynchronizable as String: synchronizable,
            // A synchronizable item can't be ThisDeviceOnly: it's meant to leave the device, inside iCloud Keychain.
            kSecAttrAccessible as String: synchronizable ? kSecAttrAccessibleAfterFirstUnlock : kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
            kSecValueData as String: Data(value.utf8)
        ]
        SecItemAdd(item as CFDictionary, nil)
    }

    static func delete(_ account: String, synchronizable: Bool = false) {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                    kSecAttrAccount as String: account, kSecAttrSynchronizable as String: synchronizable]
        SecItemDelete(query as CFDictionary)
    }
}
