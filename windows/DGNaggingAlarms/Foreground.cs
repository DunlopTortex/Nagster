using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32.SafeHandles;

namespace DGNaggingAlarms;

/// <summary>
/// Brings the app's windows to the front when they're opened from another launch (e.g. "DGNaggingAlarms.exe -new").
/// Windows only lets the process the user just started take the foreground, but it's the already-running tray
/// instance that opens the window. So the launching process passes its foreground right on before handing over,
/// and the running instance then activates its window.
/// </summary>
public static class Foreground
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    /// <summary>Lets the process at the other end of a connected pipe bring its windows to the front.</summary>
    public static void AllowPipeServer(NamedPipeClientStream pipe)
    {
        if (ServerProcessId(pipe) is int processId) AllowSetForegroundWindow(processId);
    }

    /// <summary>The ID of the process that owns the server end of a connected pipe, if Windows can tell.</summary>
    public static int? ServerProcessId(NamedPipeClientStream pipe) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId) ? (int)processId : null;

    /// <summary>Restores, raises and activates a window that's already showing.</summary>
    public static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        // Briefly making it topmost lifts it above other windows even if Windows declines to activate it.
        if (!window.Topmost)
        {
            window.Topmost = true;
            window.Topmost = false;
        }
        window.Focus();
    }

    /// <summary>Shows a modal window and brings it to the front once it has appeared.</summary>
    public static bool? ShowDialogInFront(Window dialog)
    {
        dialog.ContentRendered += (_, _) => BringToFront(dialog);
        return dialog.ShowDialog();
    }
}
