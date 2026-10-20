using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Nagster.Controls;

/// <summary>
/// A time-of-day field that accepts typed input (e.g. "9:30 AM") and offers a drop-down with
/// hour, minute and AM/PM columns. The chosen value is exposed as <see cref="SelectedTime"/>,
/// which is null when the field is empty.
/// </summary>
public partial class TimePicker : UserControl
{
    private const string Format = "h:mm tt";

    public static readonly DependencyProperty SelectedTimeProperty = DependencyProperty.Register(
        nameof(SelectedTime), typeof(TimeSpan?), typeof(TimePicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedTimeChanged));

    private readonly string[] _periods;
    private bool _syncing;

    /// <summary>Raised when <see cref="SelectedTime"/> changes, whether typed, picked or set in code.</summary>
    public event EventHandler? SelectedTimeChanged;

    public TimePicker()
    {
        InitializeComponent();
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        _periods =
        [
            string.IsNullOrEmpty(format.AMDesignator) ? "AM" : format.AMDesignator,
            string.IsNullOrEmpty(format.PMDesignator) ? "PM" : format.PMDesignator
        ];
        HourList.ItemsSource = Enumerable.Range(0, 12).Select(h => (h == 0 ? 12 : h).ToString(CultureInfo.CurrentCulture)).ToList();
        MinuteList.ItemsSource = Enumerable.Range(0, 60).Select(m => m.ToString("00", CultureInfo.CurrentCulture)).ToList();
        PeriodList.ItemsSource = _periods;
    }

    /// <summary>The selected time of day, truncated to whole minutes, or null when nothing valid is entered.</summary>
    public TimeSpan? SelectedTime
    {
        get => (TimeSpan?)GetValue(SelectedTimeProperty);
        set => SetValue(SelectedTimeProperty, value);
    }

    private static void OnSelectedTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (TimePicker)d;
        picker.Input.Text = picker.SelectedTime is TimeSpan time ? FormatTime(time) : "";
        picker.SyncLists();
        picker.SelectedTimeChanged?.Invoke(picker, EventArgs.Empty);
    }

    private static string FormatTime(TimeSpan time) => DateTime.Today.Add(time).ToString(Format, CultureInfo.CurrentCulture);

    private static TimeSpan? ParseTime(string text) =>
        DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var parsed)
            ? new TimeSpan(parsed.Hour, parsed.Minute, 0)
            : null;

    /// <summary>Applies whatever has been typed, or restores the last valid time if it cannot be read.</summary>
    private void CommitText()
    {
        if (string.IsNullOrWhiteSpace(Input.Text)) { SelectedTime = null; return; }
        var parsed = ParseTime(Input.Text);
        if (parsed is not null) SelectedTime = parsed;
        // Re-format so "930pm" style entries and rejected text both settle on a canonical display.
        Input.Text = SelectedTime is TimeSpan time ? FormatTime(time) : "";
    }

    private void SyncLists()
    {
        _syncing = true;
        try
        {
            if (SelectedTime is TimeSpan time)
            {
                HourList.SelectedIndex = time.Hours % 12;
                MinuteList.SelectedIndex = time.Minutes;
                PeriodList.SelectedIndex = time.Hours < 12 ? 0 : 1;
            }
            else
            {
                HourList.SelectedIndex = MinuteList.SelectedIndex = PeriodList.SelectedIndex = -1;
            }
        }
        finally { _syncing = false; }
    }

    private void Part_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        // Any column left unselected borrows its value from the current time (or now, if empty).
        var baseline = SelectedTime ?? new TimeSpan(DateTime.Now.Hour, DateTime.Now.Minute, 0);
        var hour = HourList.SelectedIndex >= 0 ? HourList.SelectedIndex : baseline.Hours % 12;
        var minute = MinuteList.SelectedIndex >= 0 ? MinuteList.SelectedIndex : baseline.Minutes;
        var pm = PeriodList.SelectedIndex >= 0 ? PeriodList.SelectedIndex == 1 : baseline.Hours >= 12;
        SelectedTime = new TimeSpan(hour + (pm ? 12 : 0), minute, 0);
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenPicker();

    private void OpenPicker()
    {
        CommitText();
        Picker.IsOpen = true;
    }

    private void Picker_Opened(object? sender, EventArgs e)
    {
        // A StaysOpen=False popup closes on the button's mouse-down; block the click that would reopen it.
        OpenButton.IsHitTestVisible = false;
        Dispatcher.BeginInvoke(() =>
        {
            CentreSelection(HourList);
            CentreSelection(MinuteList);
            (HourList.SelectedIndex >= 0 ? HourList : MinuteList).Focus();
        }, DispatcherPriority.Loaded);
    }

    private void Picker_Closed(object? sender, EventArgs e) => OpenButton.IsHitTestVisible = true;

    private static void CentreSelection(ListBox list)
    {
        var index = list.SelectedIndex >= 0 ? list.SelectedIndex : 0;
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement item ||
            list.Template.FindName("Scroller", list) is not ScrollViewer scroller) return;
        var top = item.TranslatePoint(new Point(), scroller).Y + scroller.VerticalOffset;
        scroller.ScrollToVerticalOffset(top - (scroller.ViewportHeight - item.ActualHeight) / 2);
    }

    private void Now_Click(object sender, RoutedEventArgs e)
    {
        SelectedTime = new TimeSpan(DateTime.Now.Hour, DateTime.Now.Minute, 0);
        CentreSelection(HourList);
        CentreSelection(MinuteList);
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        Picker.IsOpen = false;
        Input.Focus();
    }

    private void Picker_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Handle these here so they close the drop-down instead of reaching the dialog's default/cancel buttons.
        if (e.Key is not (Key.Escape or Key.Enter)) return;
        Done_Click(sender, e);
        e.Handled = true;
    }

    private void Input_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitText();

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            // Not handled, so a dialog's default button still fires after the text is applied.
            case Key.Enter: CommitText(); break;
            case Key.F4:
            case Key.System when e.SystemKey == Key.Down:
                OpenPicker();
                e.Handled = true;
                break;
        }
    }
}
