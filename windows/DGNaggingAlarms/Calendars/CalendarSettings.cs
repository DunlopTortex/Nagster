namespace DGNaggingAlarms.Calendars;

public enum CalendarSourceKind { IcsLink, Google, Microsoft365, OutlookDesktop }

public enum RuleField { Title, Location, Calendar }

public enum RuleMatch { Is, Contains, StartsWith }

/// <summary>Skips events whose field matches some text, e.g. "Title is Dance". Comparisons ignore case.</summary>
public sealed class IgnoreRule
{
    public RuleField Field { get; set; }
    public RuleMatch Match { get; set; } = RuleMatch.Contains;
    public string Text { get; set; } = "";

    public bool Matches(CalendarEvent calendarEvent)
    {
        var text = Text.Trim();
        if (text.Length == 0) return false;
        var value = (Field switch
        {
            RuleField.Location => calendarEvent.Location,
            RuleField.Calendar => calendarEvent.CalendarName,
            _ => calendarEvent.Title
        }).Trim();
        return Match switch
        {
            RuleMatch.Is => value.Equals(text, StringComparison.CurrentCultureIgnoreCase),
            RuleMatch.StartsWith => value.StartsWith(text, StringComparison.CurrentCultureIgnoreCase),
            _ => value.Contains(text, StringComparison.CurrentCultureIgnoreCase)
        };
    }
}

/// <summary>One connected calendar: a secret ICS link, a signed-in Google or Microsoft 365 account, or desktop Outlook.</summary>
public sealed class CalendarSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public CalendarSourceKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Minutes before each event that its alarm goes off.</summary>
    public int LeadMinutes { get; set; } = 10;
    /// <summary>The ICS link, encrypted: anyone with it can read the calendar.</summary>
    public string ProtectedIcsUrl { get; set; } = "";
    /// <summary>The signed-in account (e-mail or user name) for Google and Microsoft 365.</summary>
    public string Account { get; set; } = "";
    /// <summary>For accounts and Outlook: read every calendar shown, not just the main one.</summary>
    public bool AllCalendars { get; set; }
    public string LastResult { get; set; } = "";
}

/// <summary>Calendar import: connected calendars, what to skip, and the developer app registrations used to sign in.</summary>
public sealed class CalendarSyncSettings
{
    public int IntervalMinutes { get; set; } = 15;
    /// <summary>Only events starting within this many days are imported.</summary>
    public int LookaheadDays { get; set; } = 7;
    public bool SkipAllDay { get; set; } = true;
    public bool SkipDeclined { get; set; } = true;
    public bool SkipFree { get; set; }
    public List<IgnoreRule> Rules { get; set; } = [];
    public List<CalendarSource> Sources { get; set; } = [];

    public string GoogleClientId { get; set; } = "";
    public string ProtectedGoogleClientSecret { get; set; } = "";
    public string MicrosoftClientId { get; set; } = "";
    /// <summary>"organizations" accepts any work or school account; a tenant ID or domain restricts sign-in to one organisation.</summary>
    public string MicrosoftTenant { get; set; } = "organizations";
}

/// <summary>An identity of an event already imported, kept so it is never imported twice (even if its alarm is deleted).</summary>
public sealed class ImportedEvent
{
    public string Key { get; set; } = "";
    public DateTime Start { get; set; }
}
