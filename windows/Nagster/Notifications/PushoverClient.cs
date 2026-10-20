using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Nagster.Notifications;

/// <summary>A message for the Pushover API (https://pushover.net/api).</summary>
public sealed record PushoverMessage(string Token, string UserKey, string Title, string Message, int Priority = 0, string Device = "");

/// <summary>
/// The outcome of a send. <see cref="Transient"/> marks server or network failures worth retrying;
/// rejected requests (bad keys, over quota) are not, because Pushover blocks clients that repeat them.
/// </summary>
public sealed record PushoverResult(bool Success, bool Transient, string Detail);

/// <summary>Sends messages to Pushover's message API.</summary>
public static class PushoverClient
{
    private const string MessagesUrl = "https://api.pushover.net/1/messages.json";
    private const int MaxTitleLength = 250;
    private const int MaxMessageLength = 1024;

    private static readonly HttpClient Http = CreateClient();

    public static async Task<PushoverResult> SendAsync(PushoverMessage message, CancellationToken token = default)
    {
        var fields = new Dictionary<string, string>
        {
            ["token"] = message.Token.Trim(),
            ["user"] = message.UserKey.Trim(),
            ["title"] = Truncate(message.Title, MaxTitleLength),
            ["message"] = Truncate(message.Message, MaxMessageLength),
            ["priority"] = message.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(message.Device)) fields["device"] = message.Device.Trim();

        try
        {
            using var response = await Http.PostAsync(MessagesUrl, new FormUrlEncodedContent(fields), token);
            var body = await response.Content.ReadAsStringAsync(token);
            if (response.IsSuccessStatusCode && StatusOf(body) == 1) return new PushoverResult(true, false, "Sent");
            var rejected = (int)response.StatusCode is >= 400 and < 500;
            var detail = response.StatusCode == HttpStatusCode.TooManyRequests
                ? "Pushover's monthly message limit has been reached."
                : ErrorsOf(body) ?? $"Pushover returned HTTP {(int)response.StatusCode}.";
            return new PushoverResult(false, Transient: !rejected, detail);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new PushoverResult(false, true, "Pushover didn't respond in time.");
        }
        catch (HttpRequestException ex)
        {
            return new PushoverResult(false, true, "Couldn't reach Pushover: " + ex.Message);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nagster/1.0");
        return client;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static int StatusOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("status", out var s) && s.TryGetInt32(out var v) ? v : 0;
        }
        catch (JsonException) { return 0; }
    }

    /// <summary>Joins Pushover's "errors" array into one readable sentence.</summary>
    private static string? ErrorsOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("errors", out var errors) ||
                errors.ValueKind != JsonValueKind.Array) return null;
            var text = string.Join("; ", errors.EnumerateArray().Select(e => e.GetString()).Where(e => !string.IsNullOrWhiteSpace(e)));
            return text.Length == 0 ? null : char.ToUpperInvariant(text[0]) + text[1..] + ".";
        }
        catch (JsonException) { return null; }
    }
}
