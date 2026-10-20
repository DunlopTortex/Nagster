using System.Globalization;

namespace Nagster.Notifications;

/// <summary>
/// Sends a Pushover notification the first time each alarm is automatically snoozed on a given day,
/// i.e. when it went unanswered. Later auto-snoozes of the same alarm that day stay quiet so the phone
/// isn't nagged every repeat. Transient failures are retried; the outcome is recorded in Settings.
/// </summary>
public sealed class AutoSnoozeNotifier(AlarmEngine engine)
{
    // Pushover asks clients to wait at least 5 seconds before retrying a failed request.
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    public async Task OnAutoSnoozedAsync(AlarmDefinition definition, AlarmOccurrence occurrence, DateTime snoozedUntil)
    {
        var settings = engine.State.Settings.Pushover;
        if (!settings.Enabled) return;
        var token = SecretProtector.Unprotect(settings.ProtectedToken);
        var userKey = SecretProtector.Unprotect(settings.ProtectedUserKey);
        if (token.Length == 0 || userKey.Length == 0) return;
        // Claimed before sending so a rejected request isn't repeated on every auto-snooze (Pushover blocks that).
        if (!engine.TryClaimDailyNotice(definition, DateTime.Now)) return;

        var message = new PushoverMessage(token, userKey, Title(definition), Body(definition, occurrence, snoozedUntil),
            settings.Priority, settings.Device);
        var result = await PushoverClient.SendAsync(message);
        for (var attempt = 0; !result.Success && result.Transient && attempt < RetryDelays.Length; attempt++)
        {
            await Task.Delay(RetryDelays[attempt]);
            result = await PushoverClient.SendAsync(message);
        }
        settings.LastResult = $"{(result.Success ? "Sent" : "Failed")} {DateTime.Now.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)}" +
                              (result.Success ? $" for \"{definition.Title}\"" : $": {result.Detail}");
        engine.SaveSettings();
    }

    public static string Title(AlarmDefinition definition) => $"Unanswered: {definition.Title}";

    public static string Body(AlarmDefinition definition, AlarmOccurrence occurrence, DateTime snoozedUntil)
    {
        var kind = definition.IsEvent ? "event reminder" : "alarm";
        var lines = new List<string>
        {
            $"Your {kind} \"{definition.Title}\" (due {occurrence.ScheduledFor.ToString("t", CultureInfo.CurrentCulture)}) " +
            $"wasn't answered, so it was snoozed until {snoozedUntil.ToString("t", CultureInfo.CurrentCulture)}."
        };
        if (occurrence.Deadline is DateTime deadline) lines.Add($"Must be completed by {deadline.ToString("g", CultureInfo.CurrentCulture)}.");
        if (!string.IsNullOrWhiteSpace(definition.Description)) lines.Add(definition.Description.Trim());
        return string.Join("\n", lines);
    }
}
