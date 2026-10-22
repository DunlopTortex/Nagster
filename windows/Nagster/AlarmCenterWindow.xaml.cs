using System.Windows;
using System.Windows.Input;

namespace Nagster;

public partial class AlarmCenterWindow : Window
{
    private App App => (App)Application.Current;
    public AlarmCenterWindow()
    {
        InitializeComponent();
        App.Engine.Changed += Refresh;
        // Snoozed alarms move to "Going off now" as time passes, which doesn't always change the engine.
        var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        clock.Tick += (_, _) => Refresh();
        clock.Start();
        Closed += (_, _) => { App.Engine.Changed -= Refresh; clock.Stop(); };
        Refresh();
    }
    private void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(Refresh); return; }
        var now = DateTime.Now;
        var ringing = App.Engine.Ringing(now).Select(o => new AlarmRow(o, App.Engine.Definition(o))).ToList();
        var snoozed = App.Engine.Snoozed(now).Select(o => new AlarmRow(o, App.Engine.Definition(o))).ToList();
        ActiveGrid.ItemsSource = ringing;
        SnoozedGrid.ItemsSource = snoozed;
        EmptyState.Visibility = ringing.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnoozedEmpty.Visibility = snoozed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RingingHeading.Text = ringing.Count == 0 ? "Going off now" : $"Going off now ({ringing.Count})";
        SnoozedHeading.Text = snoozed.Count == 0 ? "Snoozed" : $"Snoozed ({snoozed.Count})";
    }

    /// <summary>The buttons act on one alarm, so choosing a row in one list clears the other list's selection.</summary>
    private void Grid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var other = sender == ActiveGrid ? SnoozedGrid : ActiveGrid;
        other.SelectedItem = null;
    }

    private AlarmOccurrence? Selected =>
        ((ActiveGrid.SelectedItem ?? SnoozedGrid.SelectedItem) as AlarmRow)?.Occurrence;
    private void Complete_Click(object sender, RoutedEventArgs e) { if (Selected is { } o) App.Complete(o); }
    private void Dismiss_Click(object sender, RoutedEventArgs e) { if (Selected is { } o) App.Dismiss(o); }
    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } o) return;
        if (!App.TrySnooze(o, o.SnoozeStart(DateTime.Now).AddHours(1), false, out var message)) MessageDialog.Inform(this, "Cannot snooze", message);
    }
    private void Reschedule_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } o) new RescheduleWindow(o) { Owner = this }.ShowDialog();
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
