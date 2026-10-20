using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nagster;

public partial class QuickReminderWindow : Window
{
    private const int CustomTimeIndex = 5;

    public QuickReminderWindow()
    {
        InitializeComponent();
        var later = DateTime.Now.AddHours(1);
        CustomDate.SelectedDate = later.Date;
        CustomTime.SelectedTime = new TimeSpan(later.Hour, later.Minute, 0);
        TitleBox.Focus();
    }
    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) { MessageDialog.Inform(this, "Quick reminder", "Enter a title."); return; }
        DateTime due;
        if (WhenBox.SelectedIndex == CustomTimeIndex)
        {
            if (CustomDate.SelectedDate is not DateTime date || CustomTime.SelectedTime is not TimeSpan time || date.Date.Add(time) <= DateTime.Now)
            { MessageDialog.Inform(this, "Quick reminder", "Choose a date and time in the future."); return; }
            due = date.Date.Add(time);
        }
        else
        {
            due = WhenBox.SelectedIndex switch
            {
                0 => DateTime.Now.AddMinutes(5),
                1 => DateTime.Now.AddMinutes(15),
                2 => DateTime.Now.AddMinutes(30),
                3 => DateTime.Now.AddHours(1),
                _ => DateTime.Today.AddDays(1).AddHours(9)
            };
        }
        ((App)Application.Current).Engine.Add(new AlarmDefinition { Title = TitleBox.Text, FirstDue = due });
        DialogResult = true;
    }
    private void WhenBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent, before CustomPanel exists.
        if (IsInitialized) CustomPanel.Visibility = WhenBox.SelectedIndex == CustomTimeIndex ? Visibility.Visible : Visibility.Collapsed;
    }
    private void More_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Dispatcher.BeginInvoke(() => ((App)Application.Current).ShowNew());
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
