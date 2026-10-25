using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nagster.Cloud;

/// <summary>Supabase said no, or couldn't be reached. <see cref="SignInNeeded"/> means the session is gone.</summary>
public sealed class SupabaseException(string message, bool signInNeeded = false) : Exception(message)
{
    public bool SignInNeeded { get; } = signInNeeded;
}

/// <summary>Another device holds the applier role now, so this one's changes weren't published.</summary>
public sealed class NotApplierException() : Exception("Another device is applying changes right now.");

/// <summary>A signed-in Supabase session. Supabase rotates the refresh token on every use, so keep the newest.</summary>
public sealed record CloudSession(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string UserId);

/// <summary>The account's vault: the wrapped data key and how to derive the key that unwraps it.</summary>
public sealed record CloudVault(
    [property: JsonPropertyName("vault_id")] string VaultId,
    [property: JsonPropertyName("kdf_salt")] string KdfSalt,
    [property: JsonPropertyName("kdf_iterations")] int KdfIterations,
    [property: JsonPropertyName("wrapped_key")] string WrappedKey);

/// <summary>One record of the shared copy: an alarm, an occurrence or the PC's settings, sealed; no payload once deleted.</summary>
public sealed record CloudRow(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("payload")] string? Payload);

/// <summary>An action a device sent, sealed, waiting for the applier.</summary>
public sealed record CloudOpRow(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("result")] string? Result = null);

/// <summary>A record for <c>nagster_publish</c>.</summary>
public sealed record CloudRecordChange(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("payload"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Payload);

/// <summary>An answer for <c>nagster_publish</c>: a sealed <see cref="Web.SyncOpResult"/>.</summary>
public sealed record CloudAnswer(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("result")] string Result);

/// <summary>
/// The Supabase calls cloud sync needs, over its REST APIs: Auth (GoTrue) for the account, and PostgREST for the
/// tables and functions in shared/cloud/schema.sql. Every data call carries the user's access token, so row-level
/// security limits it to that account's rows. Only ciphertext is ever sent.
/// </summary>
public sealed class SupabaseClient(HttpClient http, string projectUrl, string publishableKey)
{
    public const int PageSize = 500;

    /// <summary>
    /// Nagster's own project, used unless the user brings theirs. The publishable key is meant to ship in apps: it only
    /// identifies the project, and row-level security keeps every account to its own (encrypted) rows.
    /// </summary>
    public const string DefaultProjectUrl = "https://zjuvclvuadtiovwvzvzp.supabase.co";
    public const string DefaultPublishableKey = "sb_publishable_awjhLXnsE7Cy3h89KIeaUw_0dLrr-6A";
    /// <summary>
    /// Where the email confirmation link sends the browser: back into Nagster (on this PC, or the iPhone app), which
    /// finishes turning sync on. It must be in the project's Authentication → URL Configuration → Redirect URLs.
    /// </summary>
    public const string ConfirmedUrl = "nagster://auth-callback";
    /// <summary>Said wherever the confirmation email is mentioned: it often lands in spam.</summary>
    public const string CheckSpam = "If it isn't in your inbox, check your spam or junk folder.";

    public string ProjectUrl { get; } = NormalizeUrl(projectUrl) ?? throw new ArgumentException("Not a Supabase project URL.");

    /// <summary>
    /// "https://abc.supabase.co" from what was typed, or null. HTTPS only, except for a project on this PC (the
    /// Supabase CLI's local stack), so tokens and ciphertext are never sent in the clear.
    /// </summary>
    public static string? NormalizeUrl(string text)
    {
        var trimmed = text.Trim().TrimEnd('/');
        if (trimmed.Length == 0) return null;
        if (!trimmed.Contains("://")) trimmed = "https://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.AbsolutePath.Trim('/').Length > 0 || uri.Query.Length > 0) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    // ---- Auth ----

    /// <summary>Creates the account. Null when the project wants the email confirmed before the first sign-in.</summary>
    public async Task<CloudSession?> SignUpAsync(string email, string password, CancellationToken cancel = default)
    {
        var reply = await SendAsync(HttpMethod.Post, $"auth/v1/signup?redirect_to={Uri.EscapeDataString(ConfirmedUrl)}", null,
            new JsonObject { ["email"] = email, ["password"] = password }, cancel);
        return reply?["access_token"] is null ? null : ReadSession(reply);
    }

    public async Task<CloudSession> SignInAsync(string email, string password, CancellationToken cancel = default)
    {
        try
        {
            return ReadSession(await SendAsync(HttpMethod.Post, "auth/v1/token?grant_type=password", null,
                new JsonObject { ["email"] = email, ["password"] = password }, cancel));
        }
        catch (SupabaseException ex) when (ex.Message.Contains("not confirmed", StringComparison.OrdinalIgnoreCase))
        {
            throw new SupabaseException($"Confirm your email first, with the link Supabase sent you. {CheckSpam}");
        }
    }

    public async Task<CloudSession> RefreshAsync(string refreshToken, CancellationToken cancel = default)
    {
        try
        {
            return ReadSession(await SendAsync(HttpMethod.Post, "auth/v1/token?grant_type=refresh_token", null,
                new JsonObject { ["refresh_token"] = refreshToken }, cancel));
        }
        catch (SupabaseException ex) when (!ex.SignInNeeded && !ex.Message.StartsWith(Unreachable, StringComparison.Ordinal))
        {
            // A refresh token that's revoked, used up or expired: only signing in again helps.
            throw new SupabaseException("Sign in to cloud sync again in Settings.", signInNeeded: true);
        }
    }

    /// <summary>Ends the session on the server too. Best effort: the device forgets it either way.</summary>
    public async Task SignOutAsync(CloudSession session)
    {
        try { await SendAsync(HttpMethod.Post, "auth/v1/logout", session, null, CancellationToken.None); }
        catch (SupabaseException) { }
    }

    /// <summary>Deletes the account: its login, its vault and everything in the cloud. Each device keeps its own alarms.</summary>
    public Task DeleteAccountAsync(CloudSession session, CancellationToken cancel = default) =>
        SendAsync(HttpMethod.Post, "rest/v1/rpc/nagster_delete_account", session, new JsonObject(), cancel);

    // ---- The vault ----

    public async Task<CloudVault?> GetVaultAsync(CloudSession session, CancellationToken cancel = default)
    {
        var rows = await SendAsync(HttpMethod.Get, "rest/v1/nagster_vaults?select=vault_id,kdf_salt,kdf_iterations,wrapped_key", session, null, cancel);
        return rows is JsonArray { Count: > 0 } list ? list[0].Deserialize<CloudVault>() : null;
    }

    /// <summary>Stores a new vault. False if the account already has one (another device got there first).</summary>
    public async Task<bool> CreateVaultAsync(CloudSession session, string kdfSalt, int iterations, string wrappedKey, CancellationToken cancel = default)
    {
        try
        {
            await SendAsync(HttpMethod.Post, "rest/v1/nagster_vaults", session,
                new JsonObject { ["kdf_salt"] = kdfSalt, ["kdf_iterations"] = iterations, ["wrapped_key"] = wrappedKey }, cancel);
            return true;
        }
        catch (SupabaseException ex) when (ex.Message.StartsWith(Conflict, StringComparison.Ordinal)) { return false; }
    }

    // ---- The shared copy and the applier role ----

    /// <summary>Takes or renews the applier role. False if another device holds it (a PC, for an iPhone).</summary>
    public async Task<bool> ClaimAsync(CloudSession session, string device, bool pc, int seconds, CancellationToken cancel = default) =>
        (await SendAsync(HttpMethod.Post, "rest/v1/rpc/nagster_claim", session,
            new JsonObject { ["device"] = device, ["pc"] = pc, ["seconds"] = seconds }, cancel))?.GetValue<bool>() == true;

    public async Task ReleaseAsync(CloudSession session, string device)
    {
        try { await SendAsync(HttpMethod.Post, "rest/v1/rpc/nagster_release", session, new JsonObject { ["device"] = device }, CancellationToken.None); }
        catch (SupabaseException) { } // it runs out by itself
    }

    /// <summary>Records changed after <paramref name="sinceSeq"/>, oldest change first, a page at a time.</summary>
    public async Task<List<CloudRow>> PullRecordsAsync(CloudSession session, long sinceSeq, CancellationToken cancel = default) =>
        (await SendAsync(HttpMethod.Get,
            $"rest/v1/nagster_records?select=id,kind,seq,deleted,payload&seq=gt.{sinceSeq.ToString(CultureInfo.InvariantCulture)}&order=seq.asc&limit={PageSize}",
            session, null, cancel))?.Deserialize<List<CloudRow>>() ?? [];

    /// <summary>Actions no applier has answered yet, oldest first.</summary>
    public async Task<List<CloudOpRow>> PullWaitingOpsAsync(CloudSession session, CancellationToken cancel = default) =>
        (await SendAsync(HttpMethod.Get, $"rest/v1/nagster_ops?select=id,device_id,seq,payload&result=is.null&order=seq.asc&limit={PageSize}",
            session, null, cancel))?.Deserialize<List<CloudOpRow>>() ?? [];

    /// <summary>Publishes the applier's changes and answers, and returns each record's new seq.</summary>
    /// <exception cref="NotApplierException">The role was lost (a PC took over, or it ran out).</exception>
    public async Task<Dictionary<Guid, long>> PublishAsync(CloudSession session, string device, IReadOnlyList<CloudRecordChange> records,
        IReadOnlyList<CloudAnswer> answers, CancellationToken cancel = default)
    {
        try
        {
            var reply = await SendAsync(HttpMethod.Post, "rest/v1/rpc/nagster_publish", session, new JsonObject
            {
                ["device"] = device, ["records"] = JsonSerializer.SerializeToNode(records), ["results"] = JsonSerializer.SerializeToNode(answers)
            }, cancel);
            return reply is JsonArray list
                ? list.ToDictionary(n => Guid.Parse(n!["id"]!.GetValue<string>()), n => n!["seq"]!.GetValue<long>())
                : throw new SupabaseException("Supabase's reply couldn't be read.");
        }
        catch (SupabaseException ex) when (ex.Message.Contains("not the applier", StringComparison.Ordinal)) { throw new NotApplierException(); }
    }

    // ---- Plumbing ----

    private const string Unreachable = "Couldn't reach Supabase.";
    private const string Conflict = "Already exists";

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, CloudSession? session, JsonObject? body, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(method, $"{ProjectUrl}/{path}");
        request.Headers.Add("apikey", publishableKey);
        if (session is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        string text;
        try
        {
            response = await http.SendAsync(request, cancel);
            text = await response.Content.ReadAsStringAsync(cancel);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            throw new SupabaseException($"{Unreachable} {ex.Message}");
        }
        using (response)
        {
            JsonNode? json = null;
            try { json = text.Length > 0 ? JsonNode.Parse(text) : null; }
            catch (JsonException) { }
            if (response.IsSuccessStatusCode) return json;
            var message = Problem(json) ?? $"Supabase replied {(int)response.StatusCode}.";
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized when session is not null => new SupabaseException("Sign in to cloud sync again in Settings.", signInNeeded: true),
                HttpStatusCode.Conflict => new SupabaseException($"{Conflict}: {message}"),
                HttpStatusCode.NotFound when path.StartsWith("rest/", StringComparison.Ordinal) =>
                    new SupabaseException("The Nagster tables aren't in that Supabase project yet: run shared/cloud/schema.sql in its SQL editor."),
                _ => new SupabaseException(message)
            };
        }
    }

    /// <summary>The human part of an Auth or PostgREST error reply.</summary>
    private static string? Problem(JsonNode? json)
    {
        if (json is not JsonObject o) return null;
        foreach (var key in new[] { "msg", "error_description", "message", "error" })
            if (o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) return s;
        return null;
    }

    private static CloudSession ReadSession(JsonNode? reply)
    {
        var access = reply?["access_token"]?.GetValue<string>();
        var refresh = reply?["refresh_token"]?.GetValue<string>();
        var user = reply?["user"]?["id"]?.GetValue<string>();
        if (access is null || refresh is null || user is null) throw new SupabaseException("Supabase didn't start a session.");
        var expiresIn = reply?["expires_in"] is JsonValue e && e.TryGetValue<int>(out var seconds) ? seconds : 3600;
        return new CloudSession(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn), user.ToLowerInvariant());
    }
}
