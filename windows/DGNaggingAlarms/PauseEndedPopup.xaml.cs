using System.Globalization;
using System.Windows;

namespace DGNaggingAlarms;

/// <summary>
/// Shown in the lower-right corner when a timed pause runs out. Alarms stay paused until the user chooses
/// Unpause or Snooze (another 30 minutes of pause).
/// </summary>
public partial class PauseEndedPopup : Window
{
    public static readonly TimeSpan SnoozeLength = TimeSpan.FromMinutes(30);

    public PauseEndedPopup(DateTime endedAt, int waitingAlarms)
    {
        InitializeComponent();
        var waiting = waitingAlarms switch
        {
            0 => "No alarms are waiting.",
            1 => "1 alarm is waiting to go off.",
            _ => $"{waitingAlarms} alarms are waiting to go off."
        };
        DetailText.Text = $"Your pause ended at {endedAt.ToString("t", CultureInfo.CurrentCulture)}. {waiting}";
    }

    /// <summary>Places the popup in the lower-right corner of the working area, clear of the taskbar.</summary>
    public void PlaceBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 2;
        Top = area.Bottom - Height - 2;
    }

    private void Unpause_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).Unpause();

    private void Snooze_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ExtendPause(SnoozeLength);
}
