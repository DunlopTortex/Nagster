using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Nagster;

/// <summary>
/// Keeps windows fully on screen. Windows open centred on the screen (WindowStartupLocation="CenterScreen"),
/// but a window taller or wider than a small or highly scaled screen would put its title bar out of reach,
/// so such windows are shrunk to the screen's working area (their content scrolls) and centred again.
/// </summary>
public static class WindowPlacement
{
    /// <summary>Applies to every window the app opens, except alarm popups, which stack in a corner.</summary>
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window and not AlarmPopup) FitToScreen(window);
    }

    /// <summary>Shrinks a shown window to its monitor's working area if it's too big, and centres it.</summary>
    public static void FitToScreen(Window window)
    {
        var area = WorkArea(window);
        if (window.ActualHeight <= area.Height && window.ActualWidth <= area.Width) return;
        window.SizeToContent = SizeToContent.Manual;
        window.Height = Math.Min(window.ActualHeight, area.Height);
        window.Width = Math.Min(window.ActualWidth, area.Width);
        window.Left = area.Left + (area.Width - window.Width) / 2;
        window.Top = area.Top + (area.Height - window.Height) / 2;
    }

    /// <summary>The working area (excluding the taskbar) of the monitor the window is on, in WPF units.</summary>
    private static Rect WorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        var pixels = Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(pixels.Left / dpi.DpiScaleX, pixels.Top / dpi.DpiScaleY,
                        pixels.Width / dpi.DpiScaleX, pixels.Height / dpi.DpiScaleY);
    }
}
