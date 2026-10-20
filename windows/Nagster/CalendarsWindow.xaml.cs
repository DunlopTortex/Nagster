using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Nagster.Calendars;
using Nagster.Notifications;

namespace Nagster;

/// <summary>
/// Manages connected calendars, what to skip, and the Google/Microsoft app registrations. Changes apply on Save;
/// accounts signed in during a cancelled session are signed out again so no stray tokens are left behind.
/// </summary>
public partial class CalendarsWindow : Window
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly ObservableCollection<SourceRow> _sources = [];
    private readonly ObservableCollection<RuleRow> _rules = [];
    private readonly HashSet<Guid> _signedInThisSession = [];
    private readonly HashSet<Guid> _removed = [];
    private bool _saved;

    private App AlarmApp => (App)Application.Current;
    private CalendarSyncSettings Saved => AlarmApp.Engine.State.Settings.Calendars;

    public CalendarsWindow()
    {
        InitializeComponent();
        foreach (var source in Saved.Sources) _sources.Add(new SourceRow(source));
        foreach (var rule in Saved.Rules) _rules.Add(new RuleRow { Field = rule.Field, Match = rule.Match, Text = rule.Text });
        Sources.ItemsSource = _sources;
        Rules.ItemsSource = _rules;
        _sources.CollectionChanged += (_, _) => UpdateEmptyHint();
        UpdateEmptyHint();

        SkipAllDay.IsChecked = Saved.SkipAllDay;
        SkipDeclined.IsChecked = Saved.SkipDeclined;
        SkipFree.IsChecked = Saved.SkipFree;
        LookaheadDays.Text = Saved.LookaheadDays.ToString(CultureInfo.CurrentCulture);
        IntervalMinutes.Text = Saved.IntervalMinutes.ToString(CultureInfo.CurrentCulture);
        GoogleClientId.Text = Saved.GoogleClientId;
        GoogleClientSecret.Password = SecretProtector.Unprotect(Saved.ProtectedGoogleClientSecret);
        MicrosoftClientId.Text = Saved.MicrosoftClientId;
        MicrosoftTenant.Text = Saved.MicrosoftTenant == "organizations" ? "" : Saved.MicrosoftTenant;
        if (!OutlookDesktopProvider.IsInstalled)
        {
            AddOutlookButton.IsEnabled = false;
            AddOutlookButton.ToolTip = "Classic desktop Outlook isn't installed on this PC.";
        }
        Closed += (_, _) => { if (!_saved) foreach (var id in _signedInThisSession) TokenFiles.Delete(id); };
    }

    private void UpdateEmptyHint() => NoSources.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The settings as currently shown (validated), used for Save, Check and sign-in.</summary>
    private string? TryCollect(out CalendarSyncSettings settings)
    {
        settings = new CalendarSyncSettings();
        if (!int.TryParse(LookaheadDays.Text, out var lookahead) || lookahead is < 1 or > 60) return "Look ahead between 1 and 60 days.";
        if (!int.TryParse(IntervalMinutes.Text, out var interval) || interval is < 5 or > 1440) return "Check calendars every 5 to 1440 minutes.";
        settings.LookaheadDays = lookahead;
        settings.IntervalMinutes = interval;
        settings.SkipAllDay = SkipAllDay.IsChecked == true;
        settings.SkipDeclined = SkipDeclined.IsChecked == true;
        settings.SkipFree = SkipFree.IsChecked == true;
        settings.Rules = _rules.Where(r => r.Text.Trim().Length > 0).Select(r => r.ToRule()).ToList();
        settings.GoogleClientId = GoogleClientId.Text.Trim();
        settings.ProtectedGoogleClientSecret = SecretProtector.Protect(GoogleClientSecret.Password.Trim());
        settings.MicrosoftClientId = MicrosoftClientId.Text.Trim();
        settings.MicrosoftTenant = MicrosoftTenant.Text.Trim().Length > 0 ? MicrosoftTenant.Text.Trim() : "organizations";
        foreach (var row in _sources)
        {
            var previous = Saved.Sources.FirstOrDefault(s => s.Id == row.Id)?.LastResult;
            if (row.TryBuild(previous, out var source) is { } problem) return problem;
            settings.Sources.Add(source!);
        }
        return null;
    }

    private void AddIcs_Click(object sender, RoutedEventArgs e) =>
        _sources.Add(new SourceRow(new CalendarSource { Kind = CalendarSourceKind.IcsLink, Name = "Calendar" }));

    private void AddOutlook_Click(object sender, RoutedEventArgs e) =>
        _sources.Add(new SourceRow(new CalendarSource { Kind = CalendarSourceKind.OutlookDesktop, Name = "Outlook" }));

    private async void AddGoogle_Click(object sender, RoutedEventArgs e) =>
        await AddAccount(CalendarSourceKind.Google, "Google Calendar", GoogleCalendarProvider.SignInAsync);

    private async void AddMicrosoft_Click(object sender, RoutedEventArgs e) =>
        await AddAccount(CalendarSourceKind.Microsoft365, "Work calendar", MicrosoftCalendarProvider.SignInAsync);

    private async Task AddAccount(CalendarSourceKind kind, string name,
        Func<CalendarSource, CalendarSyncSettings, CancellationToken, Task<string>> signIn)
    {
        var source = new CalendarSource { Kind = kind, Name = name };
        if (await SignIn(source, signIn, AddStatus) is { } account)
        {
            source.Account = account;
            _sources.Add(new SourceRow(source));
            AddStatus.Text = $"Signed in as {account}.";
        }
    }

    /// <summary>Runs a browser sign-in for a calendar; returns the account, or null after showing why it failed.</summary>
    private async Task<string?> SignIn(CalendarSource source,
        Func<CalendarSource, CalendarSyncSettings, CancellationToken, Task<string>> signIn, TextBlock status)
    {
        if (TryCollect(out var settings) is { } problem) { status.Text = problem; return null; }
        IsEnabled = false;
        status.Text = "Finish signing in in your browser…";
        // Only new calendars are signed out again if the window is cancelled; re-signing an existing one is kept.
        if (Saved.Sources.All(s => s.Id != source.Id)) _signedInThisSession.Add(source.Id);
        try
        {
            using var timeout = new CancellationTokenSource(SignInTimeout);
            return await signIn(source, settings, timeout.Token);
        }
        catch (CalendarException ex) { status.Text = ex.Message; }
        catch (OperationCanceledException) { status.Text = "Sign-in timed out. Try again."; }
        finally
        {
            IsEnabled = true;
            Activate();
        }
        return null;
    }

    private async void SignInAgain_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not SourceRow row) return;
        // Forget the old sign-in first, otherwise Google silently reuses it.
        TokenFiles.Delete(row.Id);
        var source = new CalendarSource { Id = row.Id, Kind = row.Kind, Name = row.Name };
        var status = new TextBlock();
        var account = row.Kind == CalendarSourceKind.Google
            ? await SignIn(source, GoogleCalendarProvider.SignInAsync, status)
            : await SignIn(source, MicrosoftCalendarProvider.SignInAsync, status);
        if (account is not null) row.Account = account;
        row.Status = account is not null ? $"Signed in as {account}. Save to resume syncing." : status.Text;
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not SourceRow row) return;
        if (TryCollect(out var settings) is { } problem) { row.Status = problem; return; }
        var source = settings.Sources.First(s => s.Id == row.Id);
        row.Status = "Checking…";
        try
        {
            var now = DateTime.Now;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var events = await Task.Run(() => CalendarImporter.ProviderFor(source.Kind)
                .FetchAsync(source, settings, now, now.AddDays(settings.LookaheadDays), timeout.Token));
            row.Status = Summarise(ImportPlanner.Plan(events, source, settings, AlarmApp.Engine.ImportedEventKeys(), now), settings.LookaheadDays);
        }
        catch (CalendarException ex) { row.Status = ex.Message; }
        catch (OperationCanceledException) { row.Status = "The calendar took too long to respond."; }
    }

    /// <summary>"Found 9 events in the next 7 days: 4 would become alarms (…); 3 already imported; 2 skipped by rules."</summary>
    public static string Summarise(IReadOnlyList<PlannedEvent> plans, int days)
    {
        if (plans.Count == 0) return $"No events in the next {days} days.";
        var imports = plans.Where(p => p.Decision == ImportDecision.Import).Select(p => p.Event.Title).ToList();
        var parts = new List<string>
        {
            imports.Count == 0 ? "none would become new alarms"
                : $"{imports.Count} would become alarms ({string.Join(", ", imports.Take(3))}{(imports.Count > 3 ? ", …" : "")})"
        };
        foreach (var group in plans.Where(p => p.Decision != ImportDecision.Import).GroupBy(p => p.Decision))
        {
            parts.Add(group.Key switch
            {
                ImportDecision.AlreadyImported => $"{group.Count()} already imported",
                ImportDecision.Rule => $"{group.Count()} skipped by your rules",
                ImportDecision.AllDay => $"{group.Count()} all-day skipped",
                ImportDecision.Declined => $"{group.Count()} declined",
                ImportDecision.Free => $"{group.Count()} shown as free",
                ImportDecision.Cancelled => $"{group.Count()} cancelled",
                _ => $"{group.Count()} already started"
            });
        }
        return $"Found {plans.Count} event{(plans.Count == 1 ? "" : "s")} in the next {days} days: {string.Join("; ", parts)}.";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not SourceRow row) return;
        _sources.Remove(row);
        _removed.Add(row.Id);
    }

    private void AddRule_Click(object sender, RoutedEventArgs e) => _rules.Add(new RuleRow());

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is RuleRow rule) _rules.Remove(rule);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TryCollect(out var settings) is { } problem)
        {
            MessageDialog.Inform(this, "Check calendars", problem);
            return;
        }
        AlarmApp.Engine.State.Settings.Calendars = settings;
        AlarmApp.Engine.SaveSettings();
        foreach (var id in _removed) TokenFiles.Delete(id);
        _saved = true;
        _ = AlarmApp.SyncCalendarsNow();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
