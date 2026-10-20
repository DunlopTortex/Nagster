using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nagster.Web;

/// <summary>A signed-in device as shown in Settings.</summary>
public sealed record SignedInDevice(string Device, DateTime SignedIn, DateTime LastUsed);

/// <summary>
/// Signed-in browser sessions, saved so devices stay signed in when the app restarts, plus per-address throttling
/// of failed sign-in attempts. A session lasts <see cref="SessionLifetime"/> from when it was last used. Only a
/// SHA-256 hash of each session token is stored, so the saved file can't be used to sign in.
/// </summary>
public sealed class WebSessions
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(180);
    // Extending a session rewrites the file, so it's done at most this often per device.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);
    private const int AllowedFailures = 5;
    private static readonly TimeSpan LockoutPeriod = TimeSpan.FromMinutes(5);

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredSession> _sessions; // Keyed by token hash.
    private readonly ConcurrentDictionary<IPAddress, (int Failures, DateTime LockedUntil)> _failures = new();

    /// <param name="path">The sessions file; defaults to web-sessions.json next to the alarm database.</param>
    public WebSessions(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataFolder, "web-sessions.json");
        _sessions = Load(_path);
    }

    /// <summary>Starts a session for a device and returns its token for the cookie.</summary>
    public string Create(string? userAgent)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            _sessions[Hash(token)] = new StoredSession { Device = DeviceName(userAgent), SignedIn = now, LastUsed = now };
            Save();
        }
        return token;
    }

    public bool IsValid(string? token)
    {
        if (token is null) return false;
        lock (_gate) return _sessions.TryGetValue(Hash(token), out var session) && !Expired(session, DateTime.UtcNow);
    }

    /// <summary>
    /// Records that a valid session was used, extending it.
    /// </summary>
    /// <returns>True when it was extended (at most daily), meaning the cookie should be sent again with a fresh expiry.</returns>
    public bool Touch(string token)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(Hash(token), out var session) || now - session.LastUsed < RefreshInterval) return false;
            session.LastUsed = now;
            Save();
            return true;
        }
    }

    public void End(string? token)
    {
        if (token is null) return;
        lock (_gate)
            if (_sessions.Remove(Hash(token))) Save();
    }

    /// <summary>Signs every device out, e.g. after the password changes or from Settings.</summary>
    public void EndAll()
    {
        lock (_gate)
        {
            _sessions.Clear();
            Save();
        }
    }

    /// <summary>Devices currently signed in, most recently used first.</summary>
    public IReadOnlyList<SignedInDevice> Devices()
    {
        var now = DateTime.UtcNow;
        lock (_gate)
            return _sessions.Values.Where(s => !Expired(s, now)).OrderByDescending(s => s.LastUsed)
                .Select(s => new SignedInDevice(s.Device, s.SignedIn.ToLocalTime(), s.LastUsed.ToLocalTime())).ToList();
    }

    public bool IsLockedOut(IPAddress address) =>
        _failures.TryGetValue(address, out var entry) && entry.LockedUntil > DateTime.UtcNow;

    public void RecordFailure(IPAddress address) =>
        _failures.AddOrUpdate(address, _ => (1, DateTime.MinValue), (_, entry) =>
        {
            var failures = entry.Failures + 1;
            return failures >= AllowedFailures ? (0, DateTime.UtcNow.Add(LockoutPeriod)) : (failures, entry.LockedUntil);
        });

    public void RecordSuccess(IPAddress address) => _failures.TryRemove(address, out _);

    /// <summary>A short, recognisable name for a browser, e.g. "iPhone · Safari" or "Windows · Edge".</summary>
    public static string DeviceName(string? userAgent)
    {
        var ua = userAgent ?? "";
        bool Has(string text) => ua.Contains(text, StringComparison.OrdinalIgnoreCase);
        var device = Has("iPhone") ? "iPhone" : Has("iPad") ? "iPad" : Has("Android") ? "Android" : Has("Windows") ? "Windows"
            : Has("Macintosh") || Has("Mac OS") ? "Mac" : Has("Linux") ? "Linux" : "Browser";
        // Order matters: Edge and Chrome both claim "Safari", and Edge also claims "Chrome".
        var browser = Has("Edg") ? "Edge" : Has("Firefox") || Has("FxiOS") ? "Firefox"
            : Has("Chrome") || Has("CriOS") ? "Chrome" : Has("Safari") ? "Safari" : "";
        return browser.Length > 0 ? $"{device} · {browser}" : device;
    }

    private static bool Expired(StoredSession session, DateTime now) => now - session.LastUsed > SessionLifetime;

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static Dictionary<string, StoredSession> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var stored = JsonSerializer.Deserialize<Dictionary<string, StoredSession>>(File.ReadAllText(path)) ?? [];
            var now = DateTime.UtcNow;
            return stored.Where(pair => !Expired(pair.Value, now)).ToDictionary(pair => pair.Key, pair => pair.Value);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return []; // A damaged file just means devices sign in again.
        }
    }

    /// <summary>Writes the sessions file atomically (temp file then replace), so a crash can't leave it half-written.</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_sessions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("Couldn't save web sessions: " + ex.Message);
        }
    }

    private sealed class StoredSession
    {
        public string Device { get; set; } = "";
        public DateTime SignedIn { get; set; }
        public DateTime LastUsed { get; set; }
    }
}
