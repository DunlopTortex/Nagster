using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nagster;

public partial class AlarmPopup : Window
{
    private readonly AlarmOccurrence _occurrence;
    private readonly AlarmDefinition _definition;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _responseTimer;
    private DateTime _responseStarted;
    private bool _autoAttempted;
    private bool _closed;
    private bool _editing;
    private bool _silenced;
    private App App => (App)Application.Current;

    public AlarmPopup(AlarmOccurrence occurrence, AlarmDefinition definition, AppSettings settings)
    {
        InitializeComponent();
        _occurrence = occurrence;
        _definition = definition;
        _settings = settings;
        UpdateQuietBadge();
        TypeText.Text = definition.IsEvent ? "EVENT REMINDER" : "ALARM";
        TypeGlyph.Text = definition.IsEvent ? "" : "";
        ShowDetails(definition);
        DueText.Text = definition.IsEvent ? $"Starts {occurrence.ScheduledFor:f}" : $"Due {occurrence.ScheduledFor:f}";
        DeadlineText.Text = occurrence.Deadline is DateTime deadline ? $"Complete by {deadline:f}" : "";
        DeadlineRow.Visibility = occurrence.Deadline is null ? Visibility.Collapsed : Visibility.Visible;
        // Matches Brush.Panel, with the user's chosen opacity.
        Fill.Background = new SolidColorBrush(Color.FromArgb((byte)(settings.PopupOpacity * 255), 0x20, 0x21, 0x27));
        _responseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _responseTimer.Tick += ResponseTick;
    }

    public Guid OccurrenceId => _occurrence.Id;
    public void BeginResponseTimer()
    {
        if (_responseTimer.IsEnabled) return;
        _responseStarted = DateTime.Now;
        _responseTimer.Start();
        ResponseTick(this, EventArgs.Empty);
    }
    public void SetOverflow(int count)
    {
        MoreButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.Content = count == 1 ? "+1 more alarm waiting — open Active alarms" : $"+{count} more alarms waiting — open Active alarms";
    }
    public void Stop() { _closed = true; _responseTimer.Stop(); }
    private void PauseResponse() => _responseTimer.Stop();
    private void ResumeResponse()
    {
        if (_closed || _editing || _autoAttempted || _responseTimer.IsEnabled) return;
        _responseStarted = DateTime.Now;
        _responseTimer.Start();
    }
    public void ShowWarning(string text) => StatusText.Text = text;

    private void ShowDetails(AlarmDefinition definition)
    {
        TitleRun.Text = definition.Title;
        DescriptionText.Text = definition.Description;
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(definition.Description) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Shows whether this alarm is currently silenced, by the user or by quiet hours/days; refreshed each
    /// second while it counts down.
    /// </summary>
    private void UpdateQuietBadge()
    {
        var now = DateTime.Now;
        var quietDay = _settings.QuietDays.Contains(now);
        QuietBadge.Visibility = _silenced || quietDay || _settings.QuietHours.Contains(now) ? Visibility.Visible : Visibility.Collapsed;
        QuietBadgeText.Text = _silenced ? "Silenced" : quietDay ? "Quiet day" : "Quiet hours";
        QuietBadge.ToolTip = _silenced ? "You opened this alarm, so its sound was stopped"
            : "No sound during quiet hours or quiet days (see Settings)";
    }

    private void ResponseTick(object? sender, EventArgs e)
    {
        UpdateQuietBadge();
        var seconds = _definition.ResponseSecondsOverride ?? App.Engine.State.Settings.ResponseSeconds;
        if (seconds == 0) { StatusText.Text = "Waiting for your response"; return; }
        if (_autoAttempted) return;
        var remaining = seconds - (int)(DateTime.Now - _responseStarted).TotalSeconds;
        if (remaining > 0) { StatusText.Text = $"Auto snooze in {remaining}s"; return; }
        _autoAttempted = true;
        if (!App.TrySnooze(_occurrence, AutoSnoozeLength().AddTo(DateTime.Now), true, out var message))
            StatusText.Text = "Deadline prevents auto-snooze. " + message;
    }
    private SnoozeDuration AutoSnoozeLength()
    {
        // Both values are validated on save; the fallback only guards against a hand-edited database.
        var text = _definition.AutoSnoozeOverride ?? App.Engine.State.Settings.AutoSnooze;
        return SnoozeDuration.TryParse(text, out var length) ? length : new SnoozeDuration(1, SnoozeUnit.Hours);
    }
    private void Complete_Click(object sender, RoutedEventArgs e) => App.Complete(_occurrence);
    private void Dismiss_Click(object sender, RoutedEventArgs e) => App.Dismiss(_occurrence);
    private void More_Click(object sender, RoutedEventArgs e) => App.ShowActive();

    /// <summary>
    /// Stops the alarm's sound for good and opens it in the editor. The popup stays up so the user can still
    /// complete, snooze or dismiss it, and its countdown waits while the editor is open.
    /// </summary>
    private void Title_Click(object sender, RoutedEventArgs e)
    {
        App.Silence(_occurrence);
        _silenced = true;
        UpdateQuietBadge();
        _editing = true;
        PauseResponse();
        App.ShowEdit(App.Engine.Definition(_occurrence), _occurrence);
        _editing = false;
        // The alarm may have been completed or deleted elsewhere (e.g. from the phone) while the editor was open.
        if (_closed) return;
        ShowDetails(App.Engine.Definition(_occurrence));
        ResumeResponse();
    }
    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        PauseResponse();
        var menu = new ContextMenu();
        foreach (var text in App.Engine.State.Settings.SnoozeChoices.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!SnoozeDuration.TryParse(text, out var duration)) continue;
            var item = new MenuItem { Header = duration.Label };
            item.Click += (_, _) =>
            {
                if (!App.TrySnooze(_occurrence, duration.AddTo(_occurrence.SnoozeStart(DateTime.Now)), false, out var message))
                    ShowWarning(message);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = "Choose date/time…" };
        custom.Click += (_, _) => Reschedule_Click(sender, e);
        menu.Items.Add(custom);
        menu.PlacementTarget = SnoozeButton;
        menu.Closed += (_, _) => ResumeResponse();
        menu.IsOpen = true;
    }
    private void Reschedule_Click(object sender, RoutedEventArgs e)
    {
        _editing = true;
        PauseResponse();
        var dialog = new RescheduleWindow(_occurrence) { Owner = this };
        dialog.ShowDialog();
        _editing = false;
        ResumeResponse();
    }
}
