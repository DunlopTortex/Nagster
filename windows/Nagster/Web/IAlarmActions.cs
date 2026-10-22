namespace Nagster.Web;

/// <summary>
/// The alarm actions the web and sync endpoints take. <see cref="App"/> implements them so popups close and sounds
/// stop when an alarm is answered from another device; tests use the engine on its own.
/// </summary>
public interface IAlarmActions
{
    AlarmEngine Engine { get; }
    void Complete(AlarmOccurrence occurrence);
    void Dismiss(AlarmOccurrence occurrence);
    bool TrySnooze(AlarmOccurrence occurrence, DateTime next, bool automatic, out string message, bool replay = false);
    bool TryReschedule(AlarmOccurrence occurrence, DateTime next, out string message, bool replay = false);
    void DeleteDefinition(Guid definitionId);
}
