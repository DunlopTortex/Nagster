using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Nagster;

public partial class MainWindow : Window
{
    private enum StatusFilter { All, Pending, Dismissed, Completed }

    private StatusFilter _filter = StatusFilter.Pending;
    private AlarmEngine Engine => ((App)Application.Current).Engine;

    public MainWindow()
    {
        InitializeComponent();
        Engine.Changed += Refresh;
        Closed += (_, _) => Engine.Changed -= Refresh;
        // Closing only hides the window, so put it back on Pending each time it is reopened.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) return;
            _filter = StatusFilter.Pending;
            PendingFilter.IsChecked = true;
            Refresh();
        };
        Refresh();
    }

    private bool Matches(AlarmOccurrence occurrence) => _filter switch
    {
        StatusFilter.Pending => !occurrence.IsFinished,
        StatusFilter.Dismissed => occurrence.Status == OccurrenceStatus.Dismissed,
        StatusFilter.Completed => occurrence.Status == OccurrenceStatus.Completed,
        _ => true,
    };

    public void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(Refresh); return; }
        var rows = Engine.State.Occurrences.Where(Matches).Select(o => new AlarmRow(o, Engine.Definition(o)))
            .Where(r => FilterDate.SelectedDate is null || r.Occurrence.ScheduledFor.Date == FilterDate.SelectedDate.Value.Date)
            .OrderBy(r => r.Occurrence.AlertAt).ToList();
        AlarmGrid.ItemsSource = rows;
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = rows.Count == 1 ? "1 occurrence" : $"{rows.Count} occurrences";
    }

    private void New_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowNew();
    private void Quick_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowQuick();
    private void Settings_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowSettings();
    private void All_Click(object sender, RoutedEventArgs e) { _filter = StatusFilter.All; FilterDate.SelectedDate = null; MonthCalendar.SelectedDate = null; Refresh(); }
    private void Pending_Click(object sender, RoutedEventArgs e) { _filter = StatusFilter.Pending; Refresh(); }
    private void Dismissed_Click(object sender, RoutedEventArgs e) { _filter = StatusFilter.Dismissed; Refresh(); }
    private void Completed_Click(object sender, RoutedEventArgs e) { _filter = StatusFilter.Completed; Refresh(); }
    private void FilterDate_Changed(object sender, SelectionChangedEventArgs e) => Refresh();
    private void MonthCalendar_Changed(object sender, SelectionChangedEventArgs e)
    {
        FilterDate.SelectedDate = MonthCalendar.SelectedDate;
        Refresh();
    }
    private void MonthCalendar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Calendar keeps mouse capture after a day is clicked, which swallows the next click elsewhere.
        if (Mouse.Captured is CalendarItem or CalendarDayButton) Mouse.Capture(null);
    }
    /// <summary>Right-click selects the row under the mouse first, so the menu acts on what was clicked.</summary>
    private void AlarmGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(AlarmGrid, (DependencyObject)e.OriginalSource) is DataGridRow row)
            row.IsSelected = true;
        else
            e.Handled = true; // Not on a row (e.g. headers or empty space): no menu.
    }

    private void AlarmGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(AlarmGrid, (DependencyObject)e.OriginalSource) is DataGridRow) Edit_Click(sender, e);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (AlarmGrid.SelectedItem is AlarmRow row) ((App)Application.Current).ShowEdit(row.Definition, row.Occurrence);
    }

    private void OpenActive_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowActive();
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (AlarmGrid.SelectedItem is not AlarmRow row) return;
        if (MessageDialog.Confirm(this, "Delete alarm", $"Delete '{row.Title}' and all its occurrences?", "Delete", isDanger: true))
            ((App)Application.Current).DeleteDefinition(row.Definition.Id);
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void Minimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}

public sealed class AlarmRow
{
    public AlarmRow(AlarmOccurrence occurrence, AlarmDefinition definition)
    {
        Occurrence = occurrence;
        Definition = definition;
    }
    public AlarmOccurrence Occurrence { get; }
    public AlarmDefinition Definition { get; }
    public string Title => Definition.Title;
    public string Type => Definition.IsEvent ? "Event" : "Alarm";
    public bool PhoneOnly => Definition.Devices == AlarmDevices.Phone;
    public string DueText => Compact(Occurrence.ScheduledFor);
    public string EndText => Definition.IsEvent && Definition.EventEnd is DateTime end
        ? EndOf(Occurrence.ScheduledFor, Occurrence.ScheduledFor.Add(end - Definition.FirstDue)) : "—";
    public string AlertText => Compact(Occurrence.AlertAt);
    public string DeadlineText => Occurrence.Deadline is DateTime deadline ? Compact(deadline) : "—";
    public string Status => Occurrence.Status.ToString();
    public string LastAction => Occurrence.LastAction;
    public string SnoozedBy => Occurrence.LastAction.StartsWith(AlarmEngine.AutoSnoozePrefix, StringComparison.Ordinal) ? "Automatically" : "You";
    public int SnoozeCount => Occurrence.SnoozeCount;

    /// <summary>Short date and time for list columns; the year is shown only when it isn't this year.</summary>
    private static string Compact(DateTime value) =>
        value.ToString(value.Year == DateTime.Now.Year ? "MMM d, h:mm tt" : "MMM d yyyy, h:mm tt", CultureInfo.CurrentCulture);

    /// <summary>An end time that falls on the start's day needs only the time.</summary>
    private static string EndOf(DateTime start, DateTime end) =>
        end.Date == start.Date ? end.ToString("h:mm tt", CultureInfo.CurrentCulture) : Compact(end);
}
