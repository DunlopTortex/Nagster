using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nagster.Notifications;
using Nagster.Web;

namespace Nagster.Cloud;

/// <summary>An occurrence as the cloud holds it: the network sync's form, plus whether the app (not a person) last changed it.</summary>
public sealed record CloudOccurrence(SyncOccurrence Occurrence, bool Automatic);

/// <summary>
/// Optional, end-to-end encrypted sync through Supabase, so the PC and iPhones stay in step from anywhere. Off until
/// turned on in Settings; the web interface and network sync work with or without it.
///
/// The cloud holds the shared copy (alarms, occurrences, settings), encrypted, and a queue of actions sent by the
/// iPhones. One device at a time, the applier, applies those actions and publishes the changes. This PC is the
/// applier whenever it's running: every few seconds it renews the role, takes in anything an iPhone applied while it
/// was away, applies the waiting actions with the same rules as network sync (<see cref="SyncApi.Apply"/>), and
/// publishes what changed in its own copy, with the answers. Without a PC an iPhone takes the role, so the PC's copy
/// stays the master but is never required.
///
/// The iPhones name records by their cloud seq; this PC by its own revisions. <see cref="CloudState.Known"/> maps one
/// to the other, so an action made against an old copy is still recognised as such.
/// </summary>
public sealed class CloudSync : IDisposable
{
    /// <summary>How often this PC applies waiting actions and publishes its changes.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    /// <summary>How long the applier role lasts unrenewed: an iPhone takes over this long after the PC stops.</summary>
    public const int LeaseSeconds = 60;
    /// <summary>The settings record's fixed ID.</summary>
    public static readonly Guid SettingsId = new("00000000-0000-0000-0000-000000000001");
    private const int MaxPerPublish = 1000;

    private readonly HttpClient _http;
    private readonly IAlarmActions _alarms;
    private readonly Func<Action, Task> _onEngine;
    private readonly SemaphoreSlim _running = new(1, 1);
    private System.Threading.Timer? _timer;
    private CloudSession? _session;
    private byte[]? _dataKey;
    /// <summary>An account created but not yet confirmed, kept in memory until the confirmation link comes back.</summary>
    private Func<Task<string?>>? _pendingSignUp;

    /// <param name="onEngine">Runs engine work where the app does (its UI thread), so popups and sounds follow.</param>
    public CloudSync(HttpClient http, IAlarmActions alarms, Func<Action, Task> onEngine)
    {
        _http = http;
        _alarms = alarms;
        _onEngine = onEngine;
    }

    /// <summary>Raised from a worker thread when <see cref="Status"/> changes.</summary>
    public event Action? StatusChanged;

    /// <summary>For Settings, e.g. "Last synced at 14:02" or why the last sync failed.</summary>
    public string Status { get; private set; } = "Off";

    public bool IsOn => CloudState.Load() is { IsComplete: true };

    /// <summary>The account it syncs as, or "" when off.</summary>
    public string Email => CloudState.Load()?.Email ?? "";

    public void Start()
    {
        Status = IsOn ? "On" : "Off";
        _timer = new System.Threading.Timer(_ => _ = SyncAsync(), null, TimeSpan.FromSeconds(5), Interval);
    }

    /// <summary>Syncs straight away, e.g. after a change on this PC.</summary>
    public void Nudge() => _timer?.Change(TimeSpan.FromSeconds(2), Interval);

    // ---- Turning it on and off ----

    /// <summary>
    /// Signs in (or creates the account), opens the account's vault with the passphrase, or creates the vault if this
    /// is the first device, and syncs. Creating a vault needs <paramref name="passphraseAgain"/> to match.
    /// </summary>
    /// <returns>Null when cloud sync is on; otherwise what to tell the user (e.g. to confirm their email first).</returns>
    public async Task<string?> TurnOnAsync(string projectUrl, string publishableKey, string email, string password,
        string passphrase, string passphraseAgain, bool createAccount)
    {
        var client = new SupabaseClient(_http, projectUrl, publishableKey);
        var session = createAccount ? await client.SignUpAsync(email.Trim(), password) : await client.SignInAsync(email.Trim(), password);
        if (session is null)
        {
            _pendingSignUp = () => TurnOnAsync(projectUrl, publishableKey, email, password, passphrase, passphraseAgain, createAccount: false);
            return $"Account created. Open the link Supabase emailed you to confirm it, and cloud sync turns on. {SupabaseClient.CheckSpam}";
        }
        _pendingSignUp = null;
        var vault = await client.GetVaultAsync(session);
        byte[] dataKey;
        if (vault is null)
        {
            if (passphrase.Length < CloudCrypto.MinimumPassphrase)
                throw new CloudCryptoException($"Choose a sync passphrase of at least {CloudCrypto.MinimumPassphrase} characters.");
            if (passphraseAgain != passphrase)
                throw new CloudCryptoException("This is your first device on cloud sync: type the passphrase again, the same, to confirm it.");
            dataKey = CloudCrypto.NewDataKey();
            var salt = CloudCrypto.NewSalt();
            var key = dataKey;
            var wrapped = await Task.Run(() => CloudCrypto.WrapKey(key, CloudCrypto.DeriveKey(passphrase, salt), session.UserId));
            var created = await client.CreateVaultAsync(session, Convert.ToBase64String(salt), CloudCrypto.Iterations, wrapped);
            vault = await client.GetVaultAsync(session) ?? throw new SupabaseException("The new vault couldn't be read back.");
            if (!created) dataKey = await OpenVaultAsync(vault, passphrase, session.UserId); // another device made one meanwhile
        }
        else dataKey = await OpenVaultAsync(vault, passphrase, session.UserId);

        await _running.WaitAsync();
        try
        {
            new CloudState
            {
                ProjectUrl = client.ProjectUrl, PublishableKey = publishableKey.Trim(), Email = email.Trim(),
                UserId = session.UserId, VaultId = vault.VaultId
            }.WithSecrets(session.RefreshToken, dataKey).Save();
            _session = session;
            _dataKey = dataKey;
        }
        finally { _running.Release(); }
        Log("Cloud sync turned on.");
        await SyncAsync();
        return null;
    }

    private static async Task<byte[]> OpenVaultAsync(CloudVault vault, string passphrase, string userId)
    {
        if (passphrase.Length == 0) throw new CloudCryptoException("Enter your sync passphrase: this account already has cloud sync.");
        var salt = Convert.FromBase64String(vault.KdfSalt);
        return await Task.Run(() => CloudCrypto.UnwrapKey(vault.WrappedKey, CloudCrypto.DeriveKey(passphrase, salt, vault.KdfIterations), userId));
    }

    /// <summary>
    /// Run when the email confirmation link opens Nagster (nagster://auth-callback): finishes turning on the account
    /// created here, if there is one waiting.
    /// </summary>
    /// <returns>What to tell the user.</returns>
    public async Task<string> CompleteSignUpAsync(string link)
    {
        // Supabase reports a failure (an expired link, say) in the fragment or the query.
        var problem = System.Text.RegularExpressions.Regex.Match(link, "error_description=([^&]+)");
        if (problem.Success)
            return $"The confirmation link didn't work ({Uri.UnescapeDataString(problem.Groups[1].Value.Replace('+', ' '))}). " +
                   "Press Create account again for a new one.";
        if (_pendingSignUp is not { } finish) return "Your email is confirmed. Open Settings → Cloud sync and press Turn on.";
        try { return await finish() ?? "Your email is confirmed, and cloud sync is on."; }
        catch (Exception ex) when (ex is SupabaseException or CloudCryptoException or IOException)
        {
            return "Your email is confirmed, but cloud sync couldn't turn on: " + ex.Message;
        }
    }

    /// <summary>
    /// Stops syncing and forgets the account on this PC; the alarms here are untouched. With
    /// <paramref name="deleteAccount"/>, first deletes the account and everything in the cloud.
    /// </summary>
    public async Task TurnOffAsync(bool deleteAccount)
    {
        await _running.WaitAsync();
        try
        {
            if (CloudState.Load() is { IsComplete: true } state)
            {
                var client = state.Client(_http);
                try
                {
                    var session = await SessionAsync(state, client);
                    if (deleteAccount) await client.DeleteAccountAsync(session);
                    else
                    {
                        await client.ReleaseAsync(session, state.DeviceId);
                        await client.SignOutAsync(session);
                    }
                }
                catch (SupabaseException) when (!deleteAccount) { }
            }
            Forget("Off");
            Log(deleteAccount ? "Cloud sync account deleted." : "Cloud sync turned off.");
        }
        finally { _running.Release(); }
    }

    private void Forget(string status)
    {
        CloudState.Delete();
        _session = null;
        _dataKey = null;
        SetStatus(status);
    }

    // ---- Syncing ----

    /// <summary>One round, if cloud sync is on and none is running already. Never throws: failures go to Status.</summary>
    public async Task SyncAsync()
    {
        if (!await _running.WaitAsync(0)) return;
        try
        {
            if (CloudState.Load() is not { IsComplete: true } state) return;
            await SyncOnceAsync(state);
        }
        catch (NotApplierException) { } // another PC took the role between claiming and publishing; next round
        catch (Exception ex) // runs on a timer: a failure must reach Status, never end the app
        {
            SetStatus(ex is SupabaseException or CloudCryptoException ? ex.Message : "Cloud sync failed: " + ex.Message);
            Log("Cloud sync: " + ex.Message);
        }
        finally { _running.Release(); }
    }

    /// <summary>Releases the applier role on the way out, so an iPhone needn't wait for it to run out.</summary>
    public async Task StopAsync()
    {
        _timer?.Dispose();
        _timer = null;
        if (CloudState.Load() is { IsComplete: true } state && _session is { } session)
            await state.Client(_http).ReleaseAsync(session, state.DeviceId);
    }

    private async Task SyncOnceAsync(CloudState state)
    {
        var client = state.Client(_http);
        var session = await SessionAsync(state, client);
        var key = _dataKey ??= state.DataKey() ?? throw new SupabaseException("Turn cloud sync on again in Settings.", signInNeeded: true);
        if ((await client.GetVaultAsync(session))?.VaultId != state.VaultId)
        {
            Forget("Cloud sync was turned off here: the account was deleted from another device.");
            return;
        }
        if (!await client.ClaimAsync(session, state.DeviceId, pc: true, LeaseSeconds))
        {
            SetStatus("Another PC is syncing this account; cloud sync works from one PC at a time.");
            return;
        }

        // What an iPhone applied while this PC was away.
        List<CloudRow> page;
        do
        {
            page = await client.PullRecordsAsync(session, state.Cursor);
            if (page.Count == 0) break;
            var opened = page.Select(row => (row, row.Payload is null ? null : CloudCrypto.OpenJson(row.Payload, key,
                CloudCrypto.RecordContext(state.UserId, row.Id)))).ToList();
            await _onEngine(() => Adopt(_alarms, state, opened));
            state.Cursor = page[^1].Seq;
            state.Save();
        } while (page.Count == SupabaseClient.PageSize);

        // The iPhones' actions.
        var waiting = await client.PullWaitingOpsAsync(session);
        var answers = new List<CloudAnswer>();
        if (waiting.Count > 0)
        {
            var ops = new List<SyncOp>();
            foreach (var row in waiting)
            {
                try { ops.Add(CloudCrypto.OpenJson<SyncOp>(row.Payload, key, CloudCrypto.OpContext(state.UserId, row.Id)) with { OpId = row.Id }); }
                catch (CloudCryptoException)
                {
                    answers.Add(Answer(new SyncOpResult(row.Id, false, "The PC couldn't read this action."), key, state.UserId));
                }
            }
            List<SyncOpResult> results = [];
            await _onEngine(() => results = SyncApi.Apply(_alarms, Translate(state, ops), DateTime.Now));
            answers.AddRange(results.Select(r => Answer(r, key, state.UserId)));
        }

        // This PC's own changes, the ones just applied included.
        List<Outgoing> outgoing = [];
        await _onEngine(() => outgoing = CollectOutgoing(_alarms.Engine, state, DateTime.Now));
        await PublishAsync(client, session, state, key, outgoing, answers);
        SetStatus($"Last synced at {DateTime.Now:HH:mm}");
    }

    private static CloudAnswer Answer(SyncOpResult result, byte[] key, string userId) =>
        new(result.OpId, CloudCrypto.SealValue(result, key, CloudCrypto.ResultContext(userId, result.OpId)));

    private static async Task PublishAsync(SupabaseClient client, CloudSession session, CloudState state, byte[] key,
        List<Outgoing> outgoing, List<CloudAnswer> answers)
    {
        if (outgoing.Count == 0 && answers.Count == 0) return;
        // Answers ride with the first batch, so they're published with the changes they made.
        for (var start = 0; start < Math.Max(outgoing.Count, 1); start += MaxPerPublish)
        {
            var batch = outgoing.Skip(start).Take(MaxPerPublish).ToList();
            var records = batch.Select(o => new CloudRecordChange(o.Id, o.Kind, o.Json is null,
                o.Json is null ? null : CloudCrypto.SealJson(o.Json, key, CloudCrypto.RecordContext(state.UserId, o.Id)))).ToList();
            var seqs = await client.PublishAsync(session, state.DeviceId, records, start == 0 ? answers : []);
            foreach (var o in batch)
                if (seqs.TryGetValue(o.Id, out var seq))
                    state.Known[o.Id] = new CloudKnown { Kind = o.Kind, Seq = seq, LocalRevision = o.LocalRevision, Digest = o.Digest };
            state.Cursor = Math.Max(state.Cursor, seqs.Count > 0 ? seqs.Values.Max() : 0);
            state.Save();
        }
    }

    private async Task<CloudSession> SessionAsync(CloudState state, SupabaseClient client)
    {
        if (_session is { } current && current.UserId == state.UserId && current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return current;
        var refresh = state.RefreshToken() ?? throw new SupabaseException("Sign in to cloud sync again in Settings.", signInNeeded: true);
        try { _session = await client.RefreshAsync(refresh); }
        catch (SupabaseException ex) when (ex.SignInNeeded)
        {
            Forget("Cloud sync's sign-in has ended (it expired, or the account was deleted): turn it on again in Settings.");
            throw new SupabaseException(Status, signInNeeded: true);
        }
        // Supabase rotates refresh tokens: the old one stops working soon, so keep the new one straight away.
        state.WithSecrets(_session.RefreshToken, null).Save();
        return _session;
    }

    // ---- The rules, testable without a network ----

    /// <summary>A change to publish: the record's JSON (null to delete it), and what to remember once published.</summary>
    public sealed record Outgoing(Guid Id, string Kind, string? Json, string? Digest, long LocalRevision);

    private static string Digest(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    /// <summary>
    /// Takes in records another applier (an iPhone, while this PC was away) published: alarms added, edited or
    /// deleted, and occurrences answered, snoozed or replaced. Records this PC published itself are skipped. A
    /// finished occurrence here never reopens; otherwise the cloud's state, settled by the rules already, wins.
    /// </summary>
    public static void Adopt(IAlarmActions alarms, CloudState state, IReadOnlyList<(CloudRow Row, string? Json)> rows)
    {
        var engine = alarms.Engine;
        using var batch = engine.Batch();
        foreach (var (row, json) in rows)
        {
            if (state.Known.TryGetValue(row.Id, out var known) && known.Seq >= row.Seq) continue;
            var digest = json is null ? null : Digest(json);
            long localRevision = 0;
            try
            {
                if (known?.Digest != digest || digest is null) localRevision = AdoptOne(alarms, row, json);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                // Unreadable or no longer valid here: this PC's copy stays, and republishes over it.
                digest = null;
            }
            state.Known[row.Id] = new CloudKnown
            {
                Kind = row.Kind, Seq = row.Seq, Digest = digest,
                LocalRevision = localRevision != 0 ? localRevision : known?.LocalRevision ?? 0
            };
        }
    }

    /// <returns>The record's revision on this PC after taking it in, or 0 if there's nothing of it here.</returns>
    private static long AdoptOne(IAlarmActions alarms, CloudRow row, string? json)
    {
        var engine = alarms.Engine;
        var state = engine.State;
        switch (row.Kind)
        {
            case "alarm":
            {
                var existing = state.Definitions.FirstOrDefault(d => d.Id == row.Id);
                if (json is null)
                {
                    if (existing is not null) alarms.DeleteDefinition(row.Id);
                    return 0;
                }
                var wire = JsonSerializer.Deserialize<SyncDefinition>(json, CloudCrypto.Json) ?? throw new JsonException();
                if (SyncApi.ToDefinition(wire with { Id = row.Id }, existing, out var definition) is { } problem)
                    throw new ArgumentException(problem);
                definition!.Enabled = wire.Enabled;
                if (existing is null) engine.Add(definition);
                else engine.Update(existing.Id, definition);
                return state.Definitions.First(d => d.Id == row.Id).Revision;
            }
            case "occurrence":
            {
                var local = state.Occurrences.FirstOrDefault(o => o.Id == row.Id);
                if (json is null)
                {
                    if (local is not null) engine.RemoveOccurrence(row.Id);
                    return 0;
                }
                var (wire, automatic) = JsonSerializer.Deserialize<CloudOccurrence>(json, CloudCrypto.Json) ?? throw new JsonException();
                var definition = state.Definitions.FirstOrDefault(d => d.Id == wire.DefinitionId);
                if (definition is null || !SyncTime.TryParse(wire.ScheduledFor, out var scheduledFor) ||
                    !SyncTime.TryParse(wire.AlertAt, out var alertAt) || !SyncTime.TryParseOptional(wire.Deadline, out var deadline) ||
                    !Enum.TryParse<OccurrenceStatus>(wire.Status, out var status)) throw new ArgumentException("not a valid occurrence");
                local ??= engine.AdoptProjected(row.Id, definition, scheduledFor) ?? throw new ArgumentException("not part of its alarm");
                if (local.IsFinished && status is not (OccurrenceStatus.Completed or OccurrenceStatus.Dismissed)) return local.Revision;
                if (local.Status == OccurrenceStatus.Completed) return local.Revision;
                // Through the app's own actions first, so a popup showing it here closes and its sound stops.
                if (status == OccurrenceStatus.Completed) alarms.Complete(local);
                else if (status == OccurrenceStatus.Dismissed && local.Status != OccurrenceStatus.Dismissed) alarms.Dismiss(local);
                else if (status == OccurrenceStatus.Snoozed && local.AlertAt != alertAt) alarms.TryReschedule(local, alertAt, out _, replay: true);
                engine.AdoptState(local, status, alertAt, deadline, wire.SnoozeCount, wire.LastAction, automatic);
                return local.Revision;
            }
            default:
                return 0; // settings: only a PC publishes them
        }
    }

    /// <summary>
    /// The iPhones' actions with each base revision turned from the cloud's numbering into this PC's: an action
    /// made against the latest published copy of a record gets the revision it was published at, so a change made
    /// here since still counts as one; anything older counts as made against nothing.
    /// </summary>
    public static List<SyncOp> Translate(CloudState state, IEnumerable<SyncOp> ops) =>
    [
        .. ops.Select(op =>
        {
            var id = op.Type switch
            {
                SyncOps.UpsertAlarm => op.Definition?.Id,
                SyncOps.DeleteAlarm => op.DefinitionId,
                _ => op.OccurrenceId
            };
            var mapped = id is Guid known && state.Known.TryGetValue(known, out var record) && op.BaseRevision > 0 &&
                         op.BaseRevision >= record.Seq ? record.LocalRevision : 0;
            return op with { BaseRevision = mapped };
        })
    ];

    /// <summary>
    /// Everything that differs from what this PC last published: alarms, occurrences that are waiting or were
    /// answered in the last month, and the settings the iPhones follow; and deletions for whatever has gone.
    /// </summary>
    public static List<Outgoing> CollectOutgoing(AlarmEngine engine, CloudState state, DateTime now)
    {
        var result = new List<Outgoing>();
        var present = new HashSet<Guid>();
        void Offer(Guid id, string kind, string json, long revision)
        {
            present.Add(id);
            var digest = Digest(json);
            if (state.Known.TryGetValue(id, out var known) && known.Digest == digest) return;
            result.Add(new Outgoing(id, kind, json, digest, revision));
        }

        foreach (var definition in engine.State.Definitions)
            Offer(definition.Id, "alarm", JsonSerializer.Serialize(SyncApi.ToWire(definition) with { Revision = 0 }, CloudCrypto.Json),
                definition.Revision);
        var oldest = now - SyncApi.HistoryWindow;
        foreach (var occurrence in engine.State.Occurrences.Where(o => !o.IsFinished || o.ScheduledFor >= oldest))
            Offer(occurrence.Id, "occurrence",
                JsonSerializer.Serialize(new CloudOccurrence(SyncApi.ToWire(occurrence) with { Revision = 0 }, occurrence.LastChangeAutomatic),
                    CloudCrypto.Json), occurrence.Revision);
        Offer(SettingsId, "settings", JsonSerializer.Serialize(SyncApi.Settings(engine.State.Settings), CloudCrypto.Json), 0);
        foreach (var (id, known) in state.Known)
            if (known.Digest is not null && !present.Contains(id))
                result.Add(new Outgoing(id, known.Kind, null, null, 0));
        return result;
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(AppPaths.DataFolder, "cloud-sync.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); }
        catch (IOException) { }
    }

    public void Dispose() => _timer?.Dispose();
}

/// <summary>What this PC last saw of one record in the cloud: its seq, its revision here then, and a digest (null once deleted).</summary>
public sealed class CloudKnown
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("localRevision")] public long LocalRevision { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

/// <summary>Cloud sync's account and bookkeeping, saved as cloud_sync.json. Secrets in it are DPAPI-encrypted.</summary>
public sealed class CloudState
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Overrides where the file lives; the checks set it.</summary>
    public static string? PathOverride { get; set; }
    private static string FilePath => PathOverride ?? System.IO.Path.Combine(AppPaths.DataFolder, "cloud_sync.json");

    [JsonPropertyName("projectUrl")] public string ProjectUrl { get; set; } = "";
    [JsonPropertyName("publishableKey")] public string PublishableKey { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
    [JsonPropertyName("vaultId")] public string VaultId { get; set; } = "";
    /// <summary>Names this PC as the applier.</summary>
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "pc-" + Guid.NewGuid().ToString("N");
    [JsonPropertyName("refreshToken")] public string ProtectedRefreshToken { get; set; } = "";
    /// <summary>The data key that opens everything, DPAPI-encrypted for this Windows user.</summary>
    [JsonPropertyName("dataKey")] public string ProtectedDataKey { get; set; } = "";
    /// <summary>The highest seq pulled or published so far.</summary>
    [JsonPropertyName("cursor")] public long Cursor { get; set; }
    [JsonPropertyName("known")] public Dictionary<Guid, CloudKnown> Known { get; set; } = [];

    [JsonIgnore] public bool IsComplete => UserId.Length > 0 && ProtectedRefreshToken.Length > 0 && ProtectedDataKey.Length > 0;

    public string? RefreshToken() => SecretProtector.Unprotect(ProtectedRefreshToken) is { Length: > 0 } token ? token : null;

    public byte[]? DataKey() => SecretProtector.Unprotect(ProtectedDataKey) is { Length: > 0 } key ? Convert.FromBase64String(key) : null;

    /// <summary>Stores a new refresh token and, unless null, the data key, both protected.</summary>
    public CloudState WithSecrets(string refreshToken, byte[]? dataKey)
    {
        ProtectedRefreshToken = SecretProtector.Protect(refreshToken);
        if (dataKey is not null) ProtectedDataKey = SecretProtector.Protect(Convert.ToBase64String(dataKey));
        return this;
    }

    public SupabaseClient Client(HttpClient http) => new(http, ProjectUrl, PublishableKey);

    public static CloudState? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try { return JsonSerializer.Deserialize<CloudState>(File.ReadAllText(FilePath), Json); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    public void Save()
    {
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(FilePath + ".tmp", FilePath, overwrite: true);
    }

    public static void Delete() => File.Delete(FilePath);
}
