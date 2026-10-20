using System.ComponentModel;
using System.Runtime.CompilerServices;
using DGNaggingAlarms.Notifications;

namespace DGNaggingAlarms.Calendars;

/// <summary>A labelled choice for a ComboBox bound to an enum value.</summary>
public sealed record Choice<T>(T Value, string Label);

/// <summary>Editable copy of a <see cref="CalendarSource"/> shown as a card in the Calendars window.</summary>
public sealed class SourceRow : INotifyPropertyChanged
{
    private string _name;
    private bool _enabled;
    private string _leadMinutes;
    private string _icsUrl;
    private bool _allCalendars;
    private string _account;
    private string _status;

    public SourceRow(CalendarSource source)
    {
        Id = source.Id;
        Kind = source.Kind;
        _name = source.Name;
        _enabled = source.Enabled;
        _leadMinutes = source.LeadMinutes.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _icsUrl = SecretProtector.Unprotect(source.ProtectedIcsUrl);
        _allCalendars = source.AllCalendars;
        _account = source.Account;
        _status = source.LastResult;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }
    public CalendarSourceKind Kind { get; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string LeadMinutes { get => _leadMinutes; set => Set(ref _leadMinutes, value); }
    public string IcsUrl { get => _icsUrl; set => Set(ref _icsUrl, value); }
    public bool AllCalendars { get => _allCalendars; set => Set(ref _allCalendars, value); }
    public string Account { get => _account; set { Set(ref _account, value); OnChanged(nameof(Subtitle)); } }
    public string Status { get => _status; set => Set(ref _status, value); }

    public bool IsIcs => Kind == CalendarSourceKind.IcsLink;
    public bool SupportsAllCalendars => Kind != CalendarSourceKind.IcsLink;
    public bool UsesSignIn => Kind is CalendarSourceKind.Google or CalendarSourceKind.Microsoft365;

    public string Glyph => Kind switch
    {
        CalendarSourceKind.IcsLink => "",        // Link
        CalendarSourceKind.OutlookDesktop => "", // Desktop
        _ => ""                                  // Contact (signed-in account)
    };

    public string Subtitle => Kind switch
    {
        CalendarSourceKind.IcsLink => "Calendar link (ICS)",
        CalendarSourceKind.Google => "Google Calendar · " + Account,
        CalendarSourceKind.Microsoft365 => "Microsoft 365 · " + Account,
        _ => "Outlook on this PC"
    };

    /// <summary>Validates the card and builds the settings it represents.</summary>
    /// <returns>A problem to show the user, or null with <paramref name="source"/> set.</returns>
    public string? TryBuild(string? previousResult, out CalendarSource? source)
    {
        source = null;
        var name = Name.Trim();
        if (name.Length == 0) return "Give every calendar a name.";
        if (!int.TryParse(LeadMinutes, out var lead) || lead is < 0 or > 10080)
            return $"\"{name}\": the alarm lead time must be 0 to 10080 minutes.";
        if (IsIcs && IcsCalendarProvider.NormaliseUrl(IcsUrl) is null)
            return $"\"{name}\": paste the calendar's secret link (starting https:// or webcal://).";
        source = new CalendarSource
        {
            Id = Id, Kind = Kind, Name = name, Enabled = Enabled, LeadMinutes = lead, AllCalendars = AllCalendars,
            ProtectedIcsUrl = IsIcs ? SecretProtector.Protect(IcsUrl.Trim()) : "", Account = Account,
            LastResult = previousResult ?? ""
        };
        return null;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Editable ignore rule row.</summary>
public sealed class RuleRow
{
    public static IReadOnlyList<Choice<RuleField>> Fields { get; } =
        [new(RuleField.Title, "Title"), new(RuleField.Location, "Location"), new(RuleField.Calendar, "Calendar name")];

    public static IReadOnlyList<Choice<RuleMatch>> Matches { get; } =
        [new(RuleMatch.Is, "is"), new(RuleMatch.Contains, "contains"), new(RuleMatch.StartsWith, "starts with")];

    public RuleField Field { get; set; } = RuleField.Title;
    public RuleMatch Match { get; set; } = RuleMatch.Is;
    public string Text { get; set; } = "";

    public IgnoreRule ToRule() => new() { Field = Field, Match = Match, Text = Text.Trim() };
}
