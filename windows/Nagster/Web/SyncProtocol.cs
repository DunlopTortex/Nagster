namespace Nagster.Web;

// What the iPhone app and the PC exchange through POST /api/sync. Times are wall-clock text (see SyncTime) and
// enums travel by name. The Swift side mirrors these records in NagsterCore/Sync.

/// <summary>
/// One sync: the phone's actions since it last synced, and the newest revision it has seen. The reply carries
/// everything changed since that revision, or everything if the phone is too far behind.
/// </summary>
public sealed record SyncRequest(string? DeviceId, long SinceRevision, SyncOp[]? Ops);

/// <summary>
/// Something the user did on the phone. <see cref="Type"/> is one of the constants on <see cref="SyncOps"/>.
/// <see cref="BaseRevision"/> is the record's revision when the phone last saw it, used to spot conflicts.
/// </summary>
public sealed record SyncOp(
    Guid OpId,
    string? Type,
    long BaseRevision,
    Guid? OccurrenceId = null,
    Guid? DefinitionId = null,
    string? ScheduledFor = null,
    string? Until = null,
    SyncDefinition? Definition = null);

public static class SyncOps
{
    public const string Complete = "complete";
    public const string Dismiss = "dismiss";
    public const string Snooze = "snooze";
    public const string Reschedule = "reschedule";
    public const string UpsertAlarm = "upsertAlarm";
    public const string DeleteAlarm = "deleteAlarm";
}

public sealed record SyncRepeat(
    string Kind,
    int Interval,
    int Weekdays,
    int DayOfMonth,
    int Nth,
    int NthWeekday,
    bool UseLastDayForShortMonths,
    string? Until,
    int? Count);

public sealed record SyncDefinition(
    Guid Id,
    string Title,
    string Description,
    bool IsEvent,
    string? EventEnd,
    int ReminderMinutes,
    string FirstDue,
    SyncRepeat Repeat,
    string? AbsoluteDeadline,
    int? DeadlineAfterMinutes,
    string Devices,
    bool Important,
    bool Enabled,
    string? ImportedFrom,
    long Revision,
    // This alarm's own automatic snooze ("10m"): the PC's auto-snooze and the phone's reminder interval for it.
    // Null uses each device's setting.
    string? AutoSnoozeOverride = null);

public sealed record SyncOccurrence(
    Guid Id,
    Guid DefinitionId,
    string ScheduledFor,
    string AlertAt,
    string? Deadline,
    string Status,
    int SnoozeCount,
    string LastAction,
    long Revision);

public sealed record SyncTombstone(Guid Id, string Kind, long Revision);

/// <summary>The PC settings the phone follows: snooze buttons, automatic snooze length and response time.</summary>
public sealed record SyncSettings(string[] SnoozeChoices, string AutoSnooze, int ResponseSeconds);

/// <summary>Whether one of the phone's actions took effect, and why not if it didn't.</summary>
public sealed record SyncOpResult(Guid OpId, bool Applied, string? Message);

/// <summary>
/// The PC's reply. With <see cref="Full"/> the phone replaces everything it holds with this; otherwise it applies
/// these as changes on top of what it has.
/// </summary>
public sealed record SyncResponse(
    long Revision,
    bool Full,
    SyncDefinition[] Definitions,
    SyncOccurrence[] Occurrences,
    SyncTombstone[] Tombstones,
    SyncSettings Settings,
    SyncOpResult[] Results);
