using System.Windows;
using System.Windows.Input;

namespace DGNaggingAlarms;

/// <summary>
/// A themed replacement for <see cref="System.Windows.MessageBox"/>. Use <see cref="Inform"/> for a
/// single-button notice and <see cref="Confirm"/> when the user must choose to proceed or cancel.
/// </summary>
public partial class MessageDialog : Window
{
    private MessageDialog(string title, string message, string primaryText, bool showCancel, bool isDanger)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        if (!showCancel)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            PrimaryButton.IsCancel = true;
        }
        if (isDanger) PrimaryButton.Style = (Style)FindResource("Button.Danger");
        Loaded += (_, _) => PrimaryButton.Focus();
    }

    /// <summary>Shows a notice with a single OK button.</summary>
    public static void Inform(Window? owner, string title, string message) =>
        Show(owner, new MessageDialog(title, message, "OK", showCancel: false, isDanger: false));

    /// <summary>Asks the user to confirm an action.</summary>
    /// <returns>True when the user chose the primary action.</returns>
    public static bool Confirm(Window? owner, string title, string message, string primaryText, bool isDanger = false) =>
        Show(owner, new MessageDialog(title, message, primaryText, showCancel: true, isDanger));

    private static bool Show(Window? owner, MessageDialog dialog)
    {
        // The owner keeps the dialog above it; the dialog itself opens in the centre of the screen.
        if (owner?.IsVisible == true) dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
