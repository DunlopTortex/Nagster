using System.Runtime.InteropServices;

namespace Nagster.Web;

/// <summary>What's on the PC's clipboard, as far as the web page is concerned.</summary>
/// <param name="Text">The clipboard text, or null if there isn't any.</param>
/// <param name="HasOtherContent">True when the clipboard holds something other than text, such as an image or files.</param>
public sealed record ClipboardContent(string? Text, bool HasOtherContent);

/// <summary>Reads and writes the PC's clipboard as text. Called on the UI thread, which the clipboard requires.</summary>
public interface ITextClipboard
{
    ClipboardContent Read();

    void Write(string text);
}

/// <summary>
/// The Windows clipboard. Another program can hold the clipboard open for a moment (Windows then refuses
/// access with CLIPBRD_E_CANT_OPEN), so each operation is retried briefly before giving up.
/// </summary>
public sealed class SystemTextClipboard : ITextClipboard
{
    private const int Attempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(40);

    public ClipboardContent Read() => Retry(() =>
    {
        if (System.Windows.Clipboard.ContainsText()) return new ClipboardContent(System.Windows.Clipboard.GetText(), false);
        var data = System.Windows.Clipboard.GetDataObject();
        return new ClipboardContent(null, data is not null && data.GetFormats().Length > 0);
    });

    public void Write(string text) => Retry(() =>
    {
        // Phones send "\n"; many Windows programs expect "\r\n" between lines.
        var windowsText = text.ReplaceLineEndings("\r\n");
        // copy: true keeps the text on the clipboard even after this app exits.
        System.Windows.Clipboard.SetDataObject(windowsText, copy: true);
        return true;
    });

    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return action(); }
            catch (COMException) when (attempt < Attempts) { Thread.Sleep(RetryDelay); }
        }
    }
}
