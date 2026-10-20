using System.Windows;
using System.Windows.Input;

namespace Nagster;

public partial class RescheduleWindow : Window
{
    private readonly AlarmOccurrence _occurrence;
    public RescheduleWindow(AlarmOccurrence occurrence)
    {
        InitializeComponent();
        _occurrence = occurrence;
        NewDate.SelectedDate = occurrence.AlertAt.Date;
        NewTime.SelectedTime = new TimeSpan(occurrence.AlertAt.Hour, occurrence.AlertAt.Minute, 0);
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (NewDate.SelectedDate is not DateTime date || NewTime.SelectedTime is not TimeSpan time)
        { MessageDialog.Inform(this, "Reschedule", "Enter a date and time."); return; }
        if (!((App)Application.Current).TryReschedule(_occurrence, date.Date.Add(time), out var message))
        { MessageDialog.Inform(this, "Cannot reschedule", message); return; }
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
