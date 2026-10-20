using System.Security.Cryptography;
using System.Text;

namespace DGNaggingAlarms.Notifications;

/// <summary>
/// Encrypts small secrets (API keys) with Windows DPAPI for the current user, so they are never stored in
/// plain text and can only be read back by the same Windows account.
/// </summary>
public static class SecretProtector
{
    // Ties the ciphertext to this app so other DPAPI consumers running as the same user can't casually reuse it.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DGNaggingAlarms.Secrets.v1");

    public static string Protect(string secret) =>
        string.IsNullOrEmpty(secret)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>Decrypts a protected value; returns an empty string if it is blank or can't be decrypted (e.g. another user's data).</summary>
    public static string Unprotect(string protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; }
    }
}
