using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Nagster.Web;

/// <summary>
/// One command for <see cref="LocalApi"/>. <see cref="Command"/> is state, create, snooze, complete, dismiss or
/// cancel; the other fields are what that command needs (the create fields match <see cref="NewAlarmRequest"/>).
/// </summary>
public sealed record LocalRequest(string? Command, string? Id = null, string? Duration = null, string? Scope = null,
    string? Title = null, string? Description = null, string? When = null, string? Repeat = null, int Interval = 1,
    int? DeadlineMinutes = null, string? Devices = null);

/// <summary>The reply: <see cref="Ok"/> with the alarms afterwards, or an <see cref="Error"/> to show the user.</summary>
public sealed record LocalResponse(bool Ok, string? Error = null, WebState? State = null);

/// <summary>
/// A local API for other apps on this PC (e.g. the Vellina voice assistant) on the named pipe "Nagster.Api": one JSON
/// <see cref="LocalRequest"/> line in, one <see cref="LocalResponse"/> line out, per connection. Only processes running
/// as the same Windows user can connect, so unlike the web interface it needs no password and isn't on the network.
/// Commands run on the UI thread through the same rules as the web interface.
/// </summary>
public static class LocalApi
{
    public const string PipeName = "Nagster.Api";
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Answers connections until the app exits.</summary>
    public static async Task ServeAsync(IAlarmActions alarms, Dispatcher ui)
    {
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                // A client that connects and never writes mustn't hold the pipe for everyone else.
                using var timeout = new CancellationTokenSource(ReadTimeout);
                using var reader = new StreamReader(server, leaveOpen: true);
                var line = await reader.ReadLineAsync(timeout.Token);
                if (line is null) continue;
                var reply = await ui.InvokeAsync(() => HandleLine(alarms, line, DateTime.Now));
                await using var writer = new StreamWriter(server, leaveOpen: true);
                await writer.WriteLineAsync(reply);
                await writer.FlushAsync();
                server.WaitForPipeDrain();
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                await Task.Delay(200);
            }
        }
    }

    /// <summary>Parses one request line and returns the reply line. Must run on the UI thread.</summary>
    public static string HandleLine(IAlarmActions alarms, string line, DateTime now)
    {
        LocalResponse response;
        try
        {
            response = JsonSerializer.Deserialize<LocalRequest>(line, Json) is { } request
                ? Handle(alarms, request, now)
                : new LocalResponse(false, "Send a JSON command.");
        }
        catch (JsonException)
        {
            response = new LocalResponse(false, "Send a JSON command.");
        }
        return JsonSerializer.Serialize(response, Json);
    }

    public static LocalResponse Handle(IAlarmActions alarms, LocalRequest request, DateTime now)
    {
        var engine = alarms.Engine;
        var problem = request.Command?.ToLowerInvariant() switch
        {
            "state" => null,
            "create" => Create(engine, request),
            "snooze" => Ringing(request, occurrence => WebApi.Snooze(alarms, occurrence, request.Duration)),
            "complete" => Ringing(request, occurrence => { alarms.Complete(occurrence); return null; }),
            "dismiss" => Ringing(request, occurrence => { alarms.Dismiss(occurrence); return null; }),
            "cancel" => Planned(request, occurrence => WebApi.Cancel(alarms, occurrence, request.Scope)),
            _ => "Unknown command. Use state, create, snooze, complete, dismiss or cancel.",
        };
        return problem is null ? new LocalResponse(true, State: WebApi.State(engine)) : new LocalResponse(false, problem);

        string? Ringing(LocalRequest r, Func<AlarmOccurrence, string?> action) =>
            !Guid.TryParse(r.Id, out var id) ? "Give the alarm's id."
            : WebApi.FindRinging(engine, id, now) is { } occurrence ? action(occurrence) : WebApi.NotRinging;

        string? Planned(LocalRequest r, Func<AlarmOccurrence, string?> action) =>
            !Guid.TryParse(r.Id, out var id) ? "Give the alarm's id."
            : WebApi.FindPlanned(engine, id, now) is { } occurrence ? action(occurrence) : WebApi.NotPlanned;
    }

    private static string? Create(AlarmEngine engine, LocalRequest request)
    {
        var alarm = new NewAlarmRequest(request.Title, request.Description, request.When, request.Repeat ?? nameof(RepeatKind.Once),
            request.Interval, request.DeadlineMinutes, request.Devices);
        if (WebApi.Validate(alarm, out var definition) is { } problem) return problem;
        engine.Add(definition!);
        return null;
    }
}
