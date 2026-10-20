using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nagster.Controls;
using Nagster.Sounds;

namespace Nagster;

public partial class NewAlarmWindow : Window
{
    // The alarm being edited, or null when creating a new one.
    private readonly AlarmDefinition? _editing;

    /// <summary>
    /// Opens the editor for an existing alarm. <paramref name="firstAlert"/> is where its schedule restarts,
    /// normally its next upcoming occurrence; saving rebuilds the not-yet-fired occurrences from it.
    /// </summary>
    public NewAlarmWindow(AlarmDefinition existing, DateTime firstAlert) : this()
    {
        _editing = existing;
        Title = "Edit alarm";
        Heading.Text = "Edit alarm";
        SaveButton.Content = "Save changes";
        Load(existing, firstAlert);
    }

    public NewAlarmWindow()
    {
        InitializeComponent();
        var now = DateTime.Now;
        var first = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddHours(1);
        DueTime.SelectedTimeChanged += Start_Changed;
        EventEndTime.SelectedTimeChanged += EventEnd_Changed;
        foreach (var (length, label) in EventDurations) EventDuration.Items.Add(new ComboBoxItem { Content = label, Tag = length });
        SetDateTime(DueDate, DueTime, first);
        // Selecting a duration fills in the end time.
        EventDuration.SelectedIndex = Array.FindIndex(EventDurations, d => d.Length == TimeSpan.FromHours(1));
        ReminderMinutes.Text = "10";
        SetDateTime(DeadlineDate, DeadlineTime, first.AddHours(2));
        DeadlineMinutes.Text = "120";
        var settings = ((App)Application.Current).Engine.State.Settings;
        SoundChoice.Sound = "";
        SoundChoice.Preview = (sound, token) => SoundCoordinator.Preview(sound, SoundOptions.From(settings), token);
        UpdateSections();
        TitleBox.Focus();
    }

    private static readonly (TimeSpan Length, string Label)[] EventDurations =
    [
        (TimeSpan.FromMinutes(15), "15 minutes"), (TimeSpan.FromMinutes(30), "30 minutes"), (TimeSpan.FromMinutes(45), "45 minutes"),
        (TimeSpan.FromHours(1), "1 hour"), (TimeSpan.FromHours(1.5), "1.5 hours"), (TimeSpan.FromHours(2), "2 hours"),
        (TimeSpan.FromHours(3), "3 hours"), (TimeSpan.FromHours(4), "4 hours"), (TimeSpan.FromHours(6), "6 hours"),
        (TimeSpan.FromHours(8), "8 hours"), (TimeSpan.FromDays(1), "1 day"), (TimeSpan.FromDays(2), "2 days"),
        (TimeSpan.FromDays(3), "3 days"), (TimeSpan.FromDays(7), "1 week")
    ];

    // Set while the window itself moves the end or duration, so those changes don't bounce back.
    private bool _linkingEventTimes;

    /// <summary>Index of the listed duration nearest to <paramref name="length"/>.</summary>
    public static int ClosestDuration(TimeSpan length) =>
        Enumerable.Range(0, EventDurations.Length).MinBy(i => (EventDurations[i].Length - length).Duration());

    private void Start_Changed(object? sender, EventArgs e) => SetEndFromDuration();

    private void EventDuration_SelectionChanged(object sender, SelectionChangedEventArgs e) => SetEndFromDuration();

    /// <summary>Start or duration changed: the end becomes start + duration.</summary>
    private void SetEndFromDuration()
    {
        if (_linkingEventTimes || EventDuration.SelectedItem is not ComboBoxItem { Tag: TimeSpan length } ||
            !TryReadDate(DueDate, DueTime, out var start)) return;
        _linkingEventTimes = true;
        try { SetDateTime(EventEndDate, EventEndTime, start + length); }
        finally { _linkingEventTimes = false; }
    }

    /// <summary>End changed by hand: keep it as entered and show the nearest duration.</summary>
    private void EventEnd_Changed(object? sender, EventArgs e)
    {
        if (_linkingEventTimes || !TryReadDate(DueDate, DueTime, out var start) ||
            !TryReadDate(EventEndDate, EventEndTime, out var end)) return;
        _linkingEventTimes = true;
        try { EventDuration.SelectedIndex = ClosestDuration(end - start); }
        finally { _linkingEventTimes = false; }
    }

    private static void SetDateTime(DatePicker datePicker, TimePicker timePicker, DateTime value)
    {
        datePicker.SelectedDate = value.Date;
        timePicker.SelectedTime = value.TimeOfDay;
    }

    private static bool TryReadDate(DatePicker datePicker, TimePicker timePicker, out DateTime result)
    {
        if (datePicker.SelectedDate is DateTime date && timePicker.SelectedTime is TimeSpan time)
        {
            result = date.Date.Add(time);
            return true;
        }
        result = default;
        return false;
    }

    private void Section_Changed(object sender, RoutedEventArgs e)
    {
        // Fires while InitializeComponent is still building the controls below the sender.
        if (IsInitialized) UpdateSections();
    }

    /// <summary>Shows only the fields that apply to the chosen type, repeat pattern and deadline mode.</summary>
    private void UpdateSections()
    {
        var isEvent = TypeEvent.IsChecked == true;
        DueLabel.Text = isEvent ? "Event starts" : "First alert";
        EventPanel.Visibility = ShowIf(isEvent);

        var kind = (RepeatKind)RepeatBox.SelectedIndex;
        IntervalPanel.Visibility = ShowIf(kind != RepeatKind.Once);
        IntervalUnit.Text = kind switch
        {
            RepeatKind.Minutes => "minutes", RepeatKind.Hours => "hours", RepeatKind.Daily => "days",
            RepeatKind.Weekly => "weeks", RepeatKind.Monthly => "months", _ => "years"
        };
        WeeklyPanel.Visibility = ShowIf(kind == RepeatKind.Weekly);
        MonthlyPanel.Visibility = ShowIf(kind == RepeatKind.Monthly);
        EndPanel.Visibility = ShowIf(kind != RepeatKind.Once);

        DeadlineAbsolutePanel.Visibility = ShowIf(DeadlineMode.SelectedIndex == 1);
        DeadlineRelativePanel.Visibility = ShowIf(DeadlineMode.SelectedIndex == 2);
    }

    private static Visibility ShowIf(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) { Error("Enter a title."); return; }
        if (!TryReadDate(DueDate, DueTime, out var due) || due <= DateTime.Now) { Error("Enter a future first alert date and time."); return; }
        if (!int.TryParse(IntervalBox.Text, out var interval) || interval < 1) { Error("Repeat interval must be a positive whole number."); return; }
        var repeat = new RepeatRule { Kind = (RepeatKind)RepeatBox.SelectedIndex, Interval = interval };
        var boxes = new[] { Sun, Mon, Tue, Wed, Thu, Fri, Sat };
        for (var i = 0; i < boxes.Length; i++) if (boxes[i].IsChecked == true) repeat.Weekdays |= 1 << i;
        if (!string.IsNullOrWhiteSpace(MonthDayBox.Text))
        {
            if (!int.TryParse(MonthDayBox.Text, out var day) || day is < 1 or > 31) { Error("Monthly day must be 1–31."); return; }
            repeat.DayOfMonth = day;
        }
        if (NthBox.SelectedIndex > 0)
        {
            repeat.Nth = NthBox.SelectedIndex == 5 ? -1 : NthBox.SelectedIndex;
            repeat.NthWeekday = (DayOfWeek)NthDayBox.SelectedIndex;
        }
        // The end-of-recurrence fields are hidden for one-off alarms, so ignore anything left in them.
        if (repeat.Kind != RepeatKind.Once && RepeatUntil.SelectedDate is DateTime until)
        {
            if (until.Date < due.Date) { Error("Recurrence end must be on or after the first alert."); return; }
            repeat.Until = until.Date.AddDays(1).AddTicks(-1);
        }
        if (repeat.Kind != RepeatKind.Once && !string.IsNullOrWhiteSpace(RepeatCount.Text))
        {
            if (!int.TryParse(RepeatCount.Text, out var count) || count < 1) { Error("Occurrence count must be a positive whole number."); return; }
            repeat.Count = count;
        }
        var definition = new AlarmDefinition
        {
            Title = TitleBox.Text, Description = DescriptionBox.Text,
            FirstDue = due, Repeat = repeat,
            IsEvent = TypeEvent.IsChecked == true,
            Sound = SoundChoice.Sound.Length == 0 ? null : SoundChoice.Sound
        };
        if (definition.IsEvent)
        {
            if (!TryReadDate(EventEndDate, EventEndTime, out var end) || end <= due)
            { Error("Event end must be after its start."); return; }
            if (!int.TryParse(ReminderMinutes.Text, out var reminder) || reminder < 0 || reminder > 10080)
            { Error("Reminder offset must be 0–10080 minutes."); return; }
            definition.EventEnd = end;
            definition.ReminderMinutes = reminder;
        }
        if (definition.Sound is not null && !SoundLibrary.IsAvailable(definition.Sound)) { Error("The selected sound file can't be found. Choose another sound."); return; }
        if (!string.IsNullOrWhiteSpace(ResponseOverride.Text))
        {
            if (!int.TryParse(ResponseOverride.Text, out var seconds) || seconds < 0 || seconds > 86400)
            { Error("Per-alarm response time must be 0–86400 seconds."); return; }
            definition.ResponseSecondsOverride = seconds;
        }
        if (!string.IsNullOrWhiteSpace(AutoSnoozeOverride.Text))
        {
            if (!SnoozeDuration.TryParse(AutoSnoozeOverride.Text, out var length))
            { Error("Per-alarm snooze length must be a number followed by s, m, h, d, w, mo or y (e.g. 30m or 1d)."); return; }
            definition.AutoSnoozeOverride = length.ToString();
        }
        if (DeadlineMode.SelectedIndex == 1)
        {
            if (repeat.Kind != RepeatKind.Once) { Error("Use a relative deadline for recurring alarms."); return; }
            if (!TryReadDate(DeadlineDate, DeadlineTime, out var deadline) || deadline < due) { Error("Deadline must be at or after the first alert."); return; }
            definition.AbsoluteDeadline = deadline;
        }
        else if (DeadlineMode.SelectedIndex == 2)
        {
            if (!int.TryParse(DeadlineMinutes.Text, out var minutes) || minutes < 1) { Error("Enter a positive deadline offset in minutes."); return; }
            definition.DeadlineAfterMinutes = minutes;
        }
        var engine = ((App)Application.Current).Engine;
        if (_editing is null) engine.Add(definition);
        else engine.Update(_editing.Id, definition);
        DialogResult = true;
    }

    /// <summary>Fills the form from an existing alarm (the reverse of Save_Click).</summary>
    private void Load(AlarmDefinition alarm, DateTime firstAlert)
    {
        TitleBox.Text = alarm.Title;
        DescriptionBox.Text = alarm.Description;
        (alarm.IsEvent ? TypeEvent : TypeAlarm).IsChecked = true;
        SetDateTime(DueDate, DueTime, firstAlert);
        if (alarm.IsEvent && alarm.EventEnd is DateTime end)
        {
            // The end keeps the same length after the (possibly later) start; the duration list follows it.
            SetDateTime(EventEndDate, EventEndTime, firstAlert + (end - alarm.FirstDue));
            ReminderMinutes.Text = alarm.ReminderMinutes.ToString(System.Globalization.CultureInfo.CurrentCulture);
        }

        var repeat = alarm.Repeat;
        RepeatBox.SelectedIndex = (int)repeat.Kind;
        IntervalBox.Text = repeat.Interval.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var boxes = new[] { Sun, Mon, Tue, Wed, Thu, Fri, Sat };
        for (var i = 0; i < boxes.Length; i++) boxes[i].IsChecked = (repeat.Weekdays & (1 << i)) != 0;
        MonthDayBox.Text = repeat.DayOfMonth > 0 ? repeat.DayOfMonth.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
        NthBox.SelectedIndex = repeat.Nth == -1 ? 5 : Math.Clamp(repeat.Nth, 0, 4);
        NthDayBox.SelectedIndex = (int)repeat.NthWeekday;
        RepeatUntil.SelectedDate = repeat.Until?.Date;
        RepeatCount.Text = repeat.Count?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "";

        if (alarm.AbsoluteDeadline is DateTime deadline)
        {
            DeadlineMode.SelectedIndex = 1;
            SetDateTime(DeadlineDate, DeadlineTime, deadline);
        }
        else if (alarm.DeadlineAfterMinutes is int minutes)
        {
            DeadlineMode.SelectedIndex = 2;
            DeadlineMinutes.Text = minutes.ToString(System.Globalization.CultureInfo.CurrentCulture);
        }
        SoundChoice.Sound = alarm.Sound ?? "";
        ResponseOverride.Text = alarm.ResponseSecondsOverride?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "";
        AutoSnoozeOverride.Text = alarm.AutoSnoozeOverride ?? "";
        UpdateSections();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void Error(string message) => MessageDialog.Inform(this, "Check alarm", message);
}
