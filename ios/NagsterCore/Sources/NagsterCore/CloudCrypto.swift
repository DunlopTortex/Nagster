import CommonCrypto
import CryptoKit
import Foundation

public enum CloudCryptoError: LocalizedError, Equatable {
    case wrongPassphrase
    case unreadable(String)

    public var errorDescription: String? {
        switch self {
        case .wrongPassphrase: "That sync passphrase isn't right."
        case .unreadable(let detail): detail
        }
    }
}

/// End-to-end encryption for cloud sync: Supabase only ever stores what this produces.
///
/// A random 256-bit data key encrypts every alarm, occurrence, action and answer with AES-256-GCM, each under a fresh
/// random nonce, with the account and record IDs as associated data so the server can't swap one ciphertext for
/// another. The data key is stored in the cloud only wrapped (AES-256-GCM) by a key derived from the sync passphrase
/// with PBKDF2-HMAC-SHA256 at 600,000 iterations and a random salt; the passphrase never leaves the device.
/// Plaintext is padded with spaces (which JSON ignores) to a multiple of 256 bytes, so sizes give little away.
///
/// Sealed form, base64: version byte (1) | 12-byte nonce | ciphertext | 16-byte tag. Nagster on the PC implements the
/// same (Cloud/CloudCrypto.cs); shared/cloud/crypto-vectors.json pins both.
public enum CloudCrypto {
    public static let iterations = 600_000
    public static let saltBytes = 16
    /// Long enough that guessing it from a stolen vault is out of reach, even at PBKDF2's speed.
    public static let minimumPassphrase = 12
    static let version: UInt8 = 1
    static let padBlock = 256

    public static func newDataKey() -> SymmetricKey { SymmetricKey(size: .bits256) }

    public static func newSalt() -> Data { SymmetricKey(size: .init(bitCount: saltBytes * 8)).withUnsafeBytes { Data($0) } }

    /// The key that wraps the data key. The passphrase is NFC-normalized, so it types the same anywhere.
    public static func deriveKey(passphrase: String, salt: Data, iterations: Int = iterations) -> SymmetricKey {
        let password = Data(passphrase.precomposedStringWithCanonicalMapping.utf8)
        var derived = Data(count: 32)
        let status = derived.withUnsafeMutableBytes { out in
            password.withUnsafeBytes { pass in
                salt.withUnsafeBytes { saltBytes in
                    CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2), pass.baseAddress?.assumingMemoryBound(to: CChar.self),
                                         pass.count, saltBytes.baseAddress?.assumingMemoryBound(to: UInt8.self), saltBytes.count,
                                         CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), UInt32(iterations),
                                         out.baseAddress?.assumingMemoryBound(to: UInt8.self), 32)
                }
            }
        }
        precondition(status == Int32(kCCSuccess), "PBKDF2 failed (\(status))")
        return SymmetricKey(data: derived)
    }

    // What each kind of ciphertext is bound to. Never change these: everything sealed so far would stop opening.
    public static func vaultContext(_ userId: String) -> String { "nagster/vault/v1/\(userId.lowercased())" }
    public static func recordContext(_ userId: String, _ id: UUID) -> String { "nagster/record/v1/\(userId.lowercased())/\(id.uuidString.lowercased())" }
    public static func opContext(_ userId: String, _ id: UUID) -> String { "nagster/op/v1/\(userId.lowercased())/\(id.uuidString.lowercased())" }
    public static func resultContext(_ userId: String, _ id: UUID) -> String { "nagster/result/v1/\(userId.lowercased())/\(id.uuidString.lowercased())" }
    public static func settingsContext(_ userId: String, _ scope: String) -> String { "nagster/settings/v1/\(userId.lowercased())/\(scope)" }

    public static func wrap(_ dataKey: SymmetricKey, with wrappingKey: SymmetricKey, userId: String) throws -> String {
        try seal(dataKey.withUnsafeBytes { Data($0) }, key: wrappingKey, context: vaultContext(userId))
    }

    /// - Throws: `CloudCryptoError.wrongPassphrase` when `wrappingKey` came from the wrong passphrase.
    public static func unwrap(_ wrapped: String, with wrappingKey: SymmetricKey, userId: String) throws -> SymmetricKey {
        do { return SymmetricKey(data: try open(wrapped, key: wrappingKey, context: vaultContext(userId))) } catch {
            throw CloudCryptoError.wrongPassphrase
        }
    }

    /// Seals a value as padded JSON.
    public static func seal(_ value: some Encodable, key: SymmetricKey, context: String) throws -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = .withoutEscapingSlashes
        return try seal(padded(try encoder.encode(value)), key: key, context: context)
    }

    /// - Throws: `CloudCryptoError` if it wasn't sealed with this key for this context, or was altered.
    public static func open<Value: Decodable>(_ type: Value.Type, _ payload: String, key: SymmetricKey, context: String) throws -> Value {
        let plain = try open(payload, key: key, context: context)
        do { return try JSONDecoder().decode(type, from: plain) } catch {
            throw CloudCryptoError.unreadable("Something in the cloud couldn't be read.")
        }
    }

    static func padded(_ plain: Data) -> Data {
        var padded = plain
        padded.append(contentsOf: [UInt8](repeating: UInt8(ascii: " "), count: (padBlock - plain.count % padBlock) % padBlock))
        return padded
    }

    /// - Parameter nonce: Only the tests pass one, to compare with the shared vectors; otherwise it's random.
    static func seal(_ plain: Data, key: SymmetricKey, context: String, nonce: AES.GCM.Nonce = AES.GCM.Nonce()) throws -> String {
        let box = try AES.GCM.seal(plain, using: key, nonce: nonce, authenticating: Data(context.utf8))
        var sealed = Data([version])
        sealed.append(contentsOf: box.nonce)
        sealed.append(box.ciphertext)
        sealed.append(box.tag)
        return sealed.base64EncodedString()
    }

    static func open(_ text: String, key: SymmetricKey, context: String) throws -> Data {
        guard let sealed = Data(base64Encoded: text), sealed.count >= 1 + 12 + 16, sealed.first == version else {
            throw CloudCryptoError.unreadable("Not Nagster ciphertext, or from a newer Nagster.")
        }
        do {
            let box = try AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: sealed.dropFirst().prefix(12)),
                                            ciphertext: sealed.dropFirst(13).dropLast(16), tag: sealed.suffix(16))
            return try AES.GCM.open(box, using: key, authenticating: Data(context.utf8))
        } catch {
            throw CloudCryptoError.unreadable("Decryption failed: wrong key, or the data was altered.")
        }
    }
}
