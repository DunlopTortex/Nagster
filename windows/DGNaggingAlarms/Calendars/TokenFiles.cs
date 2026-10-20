using DGNaggingAlarms.Notifications;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace DGNaggingAlarms.Calendars;

/// <summary>Where sign-in tokens for connected calendar accounts are kept: one encrypted file per calendar.</summary>
public static class TokenFiles
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DGNaggingAlarms", "Tokens");

    /// <summary>Signs a calendar out on this PC by deleting its stored tokens.</summary>
    public static void Delete(Guid sourceId)
    {
        foreach (var file in Directory.Exists(Folder) ? Directory.GetFiles(Folder, $"*{sourceId:N}*") : [])
            File.Delete(file);
    }
}

/// <summary>
/// Google's token store, encrypted with Windows DPAPI for the current user instead of Google's default
/// plain-text JSON files. Each Google calendar uses its own key, so accounts never share tokens.
/// </summary>
public sealed class EncryptedGoogleTokenStore : IDataStore
{
    private static string PathFor(string key) => Path.Combine(TokenFiles.Folder, $"google-{Sanitise(key)}.bin");

    private static string Sanitise(string key) => string.Concat(key.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));

    public Task StoreAsync<T>(string key, T value)
    {
        Directory.CreateDirectory(TokenFiles.Folder);
        File.WriteAllText(PathFor(key), SecretProtector.Protect(NewtonsoftJsonSerializer.Instance.Serialize(value)));
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return Task.FromResult(default(T)!);
        var json = SecretProtector.Unprotect(File.ReadAllText(path));
        return Task.FromResult(json.Length == 0 ? default! : NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
    }

    public Task DeleteAsync<T>(string key)
    {
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    public Task ClearAsync() => Task.CompletedTask; // Tokens are removed per calendar via TokenFiles.Delete.
}
