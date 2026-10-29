using System.Text.Json.Serialization;

namespace Nagster;

public enum RepeatKind { Once, Minutes, Hours, Daily, Weekly, Monthly, Yearly }
public enum OccurrenceStatus { Pending, Active, Snoozed, Dismissed, Completed }

/// <summary>Which devices sound an alarm. Every device still lists it; the others just don't ring.</summary>
public enum AlarmDevices { All, Desktop, Phone }

public sealed class RepeatRule
{
    public RepeatKind Kind { get; set; } = RepeatKind.Once;
    public int Interval { get; set; } = 1;
    public int Weekdays { get; set; } // Sunday = bit 0.
    public int DayOfMonth { get; set; }
    public int Nth { get; set; } // 1..4 or -1 for last.
    public DayOfWeek NthWeekday { get; set; }
    public bool UseLastDayForShortMonths { get; set; } = true;
    public DateTime? Until { get; set; }
    public int? Count { get; set; }

    public DateTime? Next(DateTime anchor, DateTime after)
    {
        if (Kind == RepeatKind.Once) return null;
        var interval = Math.Max(1, Interval);
        if (Kind is RepeatKind.Minutes or RepeatKind.Hours)
        {
            var step = Kind == RepeatKind.Minutes ? TimeSpan.FromMinutes(interval) : TimeSpan.FromHours(interval);
            var count = Math.Max(1, (long)Math.Floor((after - anchor).Ticks / (double)step.Ticks) + 1);
            var next = anchor.AddTicks(step.Ticks * count);
            return Until is not null && next > Until ? null : next;
        }
        for (var day = after.Date; day < after.Date.AddYears(10); day = day.AddDays(1))
        {
            var next = day.Add(anchor.TimeOfDay);
            if (next <= after || next <= anchor || (Until is not null && next > Until)) continue;
            var days = (day - anchor.Date).Days;
            var months = (day.Year - anchor.Year) * 12 + day.Month - anchor.Month;
            var match = Kind switch
            {
                RepeatKind.Daily => days % interval == 0,
                RepeatKind.Weekly => (days / 7) % interval == 0 &&
                    ((Weekdays == 0 ? 1 << (int)anchor.DayOfWeek : Weekdays) & (1 << (int)day.DayOfWeek)) != 0,
                RepeatKind.Monthly => months >= 0 && months % interval == 0 && MatchesMonthDay(day, anchor),
                RepeatKind.Yearly => day.Year > anchor.Year && (day.Year - anchor.Year) % interval == 0 &&
                    day.Month == anchor.Month && MatchesMonthDay(day, anchor),
                _ => false
            };
            if (match) return next;
        }
        return null;
    }

    private bool MatchesMonthDay(DateTime day, DateTime anchor)
    {
        if (Nth != 0)
        {
            if (day.DayOfWeek != NthWeekday) return false;
            return Nth == -1 ? day.AddDays(7).Month != day.Month : (day.Day - 1) / 7 + 1 == Nth;
        }
        var target = DayOfMonth == 0 ? anchor.Day : DayOfMonth;
        if (UseLastDayForShortMonths) target = Math.Min(target, DateTime.DaysInMonth(day.Year, day.Month));
        return day.Day == target;
    }
}

public sealed class AlarmDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsEvent { get; set; }
    public DateTime? EventEnd { get; set; }
    public int ReminderMinutes { get; set; }
    public DateTime FirstDue { get; set; }
    public RepeatRule Repeat { get; set; } = new();
    public DateTime? NextGenerationAt { get; set; }
    public int GeneratedCount { get; set; }
    public DateTime? AbsoluteDeadline { get; set; }
    public int? DeadlineAfterMinutes { get; set; }
    /// <summary>Sound reference (see SoundLibrary); null uses the default. Saved under its original name.</summary>
    [JsonPropertyName("SoundPath")]
    public string? Sound { get; set; }
    public int? ResponseSecondsOverride { get; set; }
    /// <summary>Per-alarm automatic snooze length such as "30m" or "1d"; null uses the setting.</summary>
    public string? AutoSnoozeOverride { get; set; }

    /// <summary>Reads the minutes-only value saved by older versions; never written back.</summary>
    [JsonPropertyName("AutoSnoozeMinutesOverride"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyAutoSnoozeMinutesOverride
    {
        get => null;
        set { if (value is int minutes) AutoSnoozeOverride = $"{minutes}m"; }
    }
    public bool Enabled { get; set; } = true;

    /// <summary>The day a Pushover notice was last sent for this alarm, so only the first auto-snooze each day is pushed.</summary>
    public DateTime? PushoverNotifiedOn { get; set; }

    /// <summary>Name of the connected calendar this alarm was imported from; null for alarms made in the app.</summary>
    public string? ImportedFrom { get; set; }

    public AlarmDevices Devices { get; set; } = AlarmDevices.All;

    /// <summary>On the iPhone, ring as a full-screen alarm (AlarmKit) rather than a notification. Ignored on the PC.</summary>
    public bool Important { get; set; }

    /// <summary>Value of <see cref="AlarmState.Revision"/> when this alarm last changed; drives sync.</summary>
    public long Revision { get; set; }

    [JsonIgnore]
    public bool RingsOnDesktop => Devices != AlarmDevices.Phone;

    /// <summary>A deep copy with every setting, including the ones the web editor doesn't show.</summary>
    public AlarmDefinition Copy() =>
        System.Text.Json.JsonSerializer.Deserialize<AlarmDefinition>(System.Text.Json.JsonSerializer.Serialize(this))!;
}

public sealed class AlarmOccurrence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DefinitionId { get; set; }
    public DateTime ScheduledFor { get; set; }
    public DateTime AlertAt { get; set; }
    public DateTime? Deadline { get; set; }
    public OccurrenceStatus Status { get; set; }
    public DateTime? RespondedAt { get; set; }
    public int SnoozeCount { get; set; }
    public string LastAction { get; set; } = "";

    /// <summary>Value of <see cref="AlarmState.Revision"/> when this occurrence last changed; drives sync.</summary>
    public long Revision { get; set; }

    /// <summary>
    /// True when the last change was made by the app rather than a person (generated, shown, automatically
    /// snoozed, marked missed), so an action from the phone made before it can still take effect.
    /// </summary>
    public bool LastChangeAutomatic { get; set; }

    [JsonIgnore]
    public bool IsFinished => Status is OccurrenceStatus.Completed or OccurrenceStatus.Dismissed;

    /// <summary>
    /// Where a snooze length is counted from: the alert time if it is still to come, otherwise now, so snoozing
    /// an alarm that hasn't gone off yet pushes it later rather than pulling it earlier.
    /// </summary>
    public DateTime SnoozeStart(DateTime now) => AlertAt > now ? AlertAt : now;
}

/// <summary>Records that an alarm or occurrence was deleted, so the phone deletes its copy at the next sync.</summary>
public sealed class Tombstone
{
    public const string AlarmKind = "Alarm";
    public const string OccurrenceKind = "Occurrence";

    public Guid Id { get; set; }
    public string Kind { get; set; } = AlarmKind;
    public long Revision { get; set; }
    public DateTime DeletedAt { get; set; }
}

/// <summary>A sync action already applied, so one the phone resends after a dropped reply isn't applied twice.</summary>
public sealed class AppliedOp
{
    public Guid Id { get; set; }
    public DateTime AppliedAt { get; set; }
}

public sealed class AppSettings
{
    public int ResponseSeconds { get; set; } = 30;
    /// <summary>Automatic snooze length such as "60m" or "1d".</summary>
    public string AutoSnooze { get; set; } = "10m";

    /// <summary>Reads the minutes-only value saved by older versions; never written back.</summary>
    [JsonPropertyName("AutoSnoozeMinutes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyAutoSnoozeMinutes
    {
        get => null;
        set { if (value is int minutes) AutoSnooze = $"{minutes}m"; }
    }
    /// <summary>Default sound reference (see SoundLibrary); blank uses the Windows alert. Saved under its original name.</summary>
    [JsonPropertyName("DefaultSoundPath")]
    public string DefaultSound { get; set; } = "";

    /// <summary>Friendly name of the output device for alarm sounds; blank means the system default.</summary>
    public string AudioDeviceName { get; set; } = "";

    /// <summary>System volume (0–100) to set on the output device while an alarm sounds; null leaves it unchanged.</summary>
    public int? AlarmVolumePercent { get; set; }

    public QuietHours QuietHours { get; set; } = new();

    public QuietDays QuietDays { get; set; } = new();

    public AlarmHours AlarmHours { get; set; } = new();

    /// <summary>
    /// While set, no alarm goes off (no popups, sounds or automatic snoozes). Once this time passes the user is asked
    /// to unpause or extend the pause; DateTime.MaxValue means paused until they unpause. Saved, so a pause survives restarts.
    /// </summary>
    public DateTime? PausedUntil { get; set; }

    public PushoverSettings Pushover { get; set; } = new();

    public WebAccessSettings WebAccess { get; set; } = new();

    public Calendars.CalendarSyncSettings Calendars { get; set; } = new();
    public double PopupOpacity { get; set; } = 0.94;
    public string PopupCorner { get; set; } = "BottomRight";
    public int MaxVisiblePopups { get; set; } = 3;
    public string SnoozeChoices { get; set; } = "5m, 15m, 30m, 1h, 1d, 1w, 2w";
    public bool StartWithWindows { get; set; } = true;
}

/// <summary>
/// A daily period when alarms make no sound (they still pop up). The period may cross midnight,
/// e.g. 22:00 to 07:00; a start equal to the end means no quiet period.
/// </summary>
public sealed class QuietHours
{
    public bool Enabled { get; set; }
    public TimeSpan Start { get; set; } = new(22, 0, 0);
    public TimeSpan End { get; set; } = new(7, 0, 0);

    public bool Contains(DateTime moment)
    {
        if (!Enabled || Start == End) return false;
        var time = moment.TimeOfDay;
        return Start < End ? time >= Start && time < End : time >= Start || time < End;
    }
}

/// <summary>
/// Whole days of the week when alarms make no sound (they still pop up), e.g. Saturday and Sunday.
/// Kept on this PC only; no days chosen means none are quiet.
/// </summary>
public sealed class QuietDays
{
    public int Weekdays { get; set; } // Sunday = bit 0, as in RepeatRule.

    public bool Contains(DateTime moment) => (Weekdays & (1 << (int)moment.DayOfWeek)) != 0;
}

/// <summary>
/// The hours this PC rings alarms set to ring on the PC only. One that comes due outside them is snoozed until
/// they next start, unless its deadline comes first. Alarms for all devices ignore them, so the phone (with its own
/// hours) isn't held back. The hours may cross midnight; a start equal to the end means all day.
/// </summary>
public sealed class AlarmHours
{
    public bool Enabled { get; set; }
    public TimeSpan Start { get; set; } = new(8, 0, 0);
    public TimeSpan End { get; set; } = new(21, 0, 0);

    public bool Contains(DateTime moment)
    {
        if (!Enabled || Start == End) return true;
        var time = moment.TimeOfDay;
        return Start < End ? time >= Start && time < End : time >= Start || time < End;
    }

    /// <summary>When the hours next start, at or after <paramref name="moment"/>: later the same day or the next.</summary>
    public DateTime NextStart(DateTime moment)
    {
        var sameDay = moment.Date + Start;
        return sameDay >= moment ? sameDay : sameDay.AddDays(1);
    }
}

/// <summary>
/// Pushover (pushover.net) phone notifications for unanswered alarms. The application token and user key
/// are stored encrypted for the current Windows user (see <see cref="Notifications.SecretProtector"/>).
/// </summary>
public sealed class PushoverSettings
{
    public bool Enabled { get; set; }
    public string ProtectedToken { get; set; } = "";
    public string ProtectedUserKey { get; set; } = "";
    /// <summary>Optional Pushover device name; blank sends to all of the user's devices.</summary>
    public string Device { get; set; } = "";
    /// <summary>Pushover priority: 0 normal, 1 high (bypasses the phone's quiet hours).</summary>
    public int Priority { get; set; }
    /// <summary>Outcome of the most recent automatic send, shown in Settings, e.g. "Failed 16 Oct 09:12: user key is invalid".</summary>
    public string LastResult { get; set; } = "";
}

/// <summary>
/// The optional web interface. It listens on all network interfaces, so it is off until enabled and always
/// requires a password, stored only as a salted hash (see <see cref="Web.WebPassword"/>).
/// </summary>
public sealed class WebAccessSettings
{
    public const int DefaultPort = 5972;

    public bool Enabled { get; set; }
    public int Port { get; set; } = DefaultPort;
    public string PasswordHash { get; set; } = "";
}

public sealed class AlarmState
{
    public List<AlarmDefinition> Definitions { get; set; } = [];
    public List<AlarmOccurrence> Occurrences { get; set; } = [];
    public AppSettings Settings { get; set; } = new();

    /// <summary>Calendar events already turned into alarms, so they are never imported twice.</summary>
    public List<Calendars.ImportedEvent> ImportedEvents { get; set; } = [];

    /// <summary>Counter stamped on every changed alarm, occurrence and tombstone; the phone asks for changes since one.</summary>
    public long Revision { get; set; }

    public List<Tombstone> Deleted { get; set; } = [];

    /// <summary>Newest revision among tombstones pruned so far; a phone that last synced before it needs everything again.</summary>
    public long PrunedThrough { get; set; }

    public List<AppliedOp> AppliedOps { get; set; } = [];
}
