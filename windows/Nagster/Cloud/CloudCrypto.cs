using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nagster.Cloud;

/// <summary>A wrong passphrase, or ciphertext that was tampered with or isn't Nagster's.</summary>
public sealed class CloudCryptoException(string message) : Exception(message);

/// <summary>
/// End-to-end encryption for cloud sync: Supabase only ever stores what this produces.
///
/// A random 256-bit data key encrypts every alarm, occurrence, action and answer with AES-256-GCM, each under a fresh
/// random nonce, with the account and record IDs as associated data so the server can't swap one ciphertext for
/// another. The data key is stored in the cloud only wrapped (AES-256-GCM) by a key derived from the sync passphrase
/// with PBKDF2-HMAC-SHA256 at 600,000 iterations and a random salt; the passphrase never leaves the device.
/// Plaintext is padded with spaces (which JSON ignores) to a multiple of 256 bytes, so sizes give little away.
///
/// Sealed form, base64: version byte (1) | 12-byte nonce | ciphertext | 16-byte tag. The iPhone app implements the
/// same; shared/cloud/crypto-vectors.json pins both.
/// </summary>
public static class CloudCrypto
{
    public const int Iterations = 600_000;
    public const int KeyBytes = 32;
    public const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const byte Version = 1;
    private const int PadBlock = 256;
    /// <summary>Long enough that guessing it from a stolen vault is out of reach, even at PBKDF2's speed.</summary>
    public const int MinimumPassphrase = 12;

    /// <summary>The JSON the iPhone app reads: camelCase, as the network sync sends it.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static byte[] NewDataKey() => RandomNumberGenerator.GetBytes(KeyBytes);
    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltBytes);

    /// <summary>The key that wraps the data key. The passphrase is NFC-normalized, so it types the same anywhere.</summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations = Iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)), salt, iterations,
            HashAlgorithmName.SHA256, KeyBytes);

    // What each kind of ciphertext is bound to. Never change these: everything sealed so far would stop opening.
    public static string VaultContext(string userId) => $"nagster/vault/v1/{userId.ToLowerInvariant()}";
    public static string RecordContext(string userId, Guid id) => $"nagster/record/v1/{userId.ToLowerInvariant()}/{id:D}";
    public static string OpContext(string userId, Guid id) => $"nagster/op/v1/{userId.ToLowerInvariant()}/{id:D}";
    public static string ResultContext(string userId, Guid id) => $"nagster/result/v1/{userId.ToLowerInvariant()}/{id:D}";

    public static string WrapKey(byte[] dataKey, byte[] wrappingKey, string userId, byte[]? nonce = null) =>
        Seal(dataKey, wrappingKey, VaultContext(userId), nonce);

    /// <exception cref="CloudCryptoException">The passphrase was wrong.</exception>
    public static byte[] UnwrapKey(string wrapped, byte[] wrappingKey, string userId)
    {
        try { return Open(wrapped, wrappingKey, VaultContext(userId)); }
        catch (CloudCryptoException) { throw new CloudCryptoException("That sync passphrase isn't right."); }
    }

    /// <summary>Seals JSON text, padded.</summary>
    public static string SealJson(string json, byte[] key, string context, byte[]? nonce = null) =>
        Seal(Pad(Encoding.UTF8.GetBytes(json)), key, context, nonce);

    /// <summary>Seals a value as JSON. Named apart from <see cref="SealJson(string, byte[], string, byte[])"/>, so a string is never serialized twice.</summary>
    public static string SealValue<T>(T value, byte[] key, string context) => SealJson(JsonSerializer.Serialize(value, Json), key, context);

    /// <summary>The JSON text inside, padding trimmed.</summary>
    /// <exception cref="CloudCryptoException">It wasn't sealed with this key for this context, or was altered.</exception>
    public static string OpenJson(string payload, byte[] key, string context) => Encoding.UTF8.GetString(Open(payload, key, context)).TrimEnd(' ');

    public static T OpenJson<T>(string payload, byte[] key, string context)
    {
        try { return JsonSerializer.Deserialize<T>(OpenJson(payload, key, context), Json) ?? throw new JsonException(); }
        catch (JsonException) { throw new CloudCryptoException("Something in the cloud couldn't be read."); }
    }

    private static byte[] Pad(byte[] plain)
    {
        var padded = new byte[(plain.Length + PadBlock - 1) / PadBlock * PadBlock];
        plain.CopyTo(padded, 0);
        padded.AsSpan(plain.Length).Fill((byte)' ');
        return padded;
    }

    /// <param name="nonce">Only the checks pass one, to compare with the shared vectors; otherwise it's random.</param>
    private static string Seal(byte[] plain, byte[] key, string context, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(NonceBytes);
        var sealedBytes = new byte[1 + NonceBytes + plain.Length + TagBytes];
        sealedBytes[0] = Version;
        nonce.CopyTo(sealedBytes, 1);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plain, sealedBytes.AsSpan(1 + NonceBytes, plain.Length),
            sealedBytes.AsSpan(1 + NonceBytes + plain.Length), Encoding.UTF8.GetBytes(context));
        return Convert.ToBase64String(sealedBytes);
    }

    private static byte[] Open(string sealedText, byte[] key, string context)
    {
        byte[] sealedBytes;
        try { sealedBytes = Convert.FromBase64String(sealedText); }
        catch (FormatException) { throw new CloudCryptoException("Not Nagster ciphertext."); }
        if (sealedBytes.Length < 1 + NonceBytes + TagBytes || sealedBytes[0] != Version)
            throw new CloudCryptoException("Not Nagster ciphertext, or from a newer Nagster.");
        var cipherLength = sealedBytes.Length - 1 - NonceBytes - TagBytes;
        var plain = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(sealedBytes.AsSpan(1, NonceBytes), sealedBytes.AsSpan(1 + NonceBytes, cipherLength),
                sealedBytes.AsSpan(1 + NonceBytes + cipherLength), plain, Encoding.UTF8.GetBytes(context));
        }
        catch (AuthenticationTagMismatchException) { throw new CloudCryptoException("Decryption failed: wrong key, or the data was altered."); }
        return plain;
    }
}
