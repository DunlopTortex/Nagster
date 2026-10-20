using System.Windows;
using System.Windows.Controls;
using DGNaggingAlarms.Sounds;

namespace DGNaggingAlarms.Controls;

/// <summary>
/// Chooses an alarm sound from the built-in sounds, the Windows alarm sounds on this PC, or a file the
/// user browses to. <see cref="Sound"/> holds the saved reference (see <see cref="SoundLibrary"/>), with
/// an empty string meaning "default". The host supplies <see cref="Preview"/> so previews play the same
/// way (and on the same device) as a real alarm.
/// </summary>
public partial class SoundPicker : UserControl
{
    private const string PlayGlyph = "";
    private const string StopGlyph = "";

    private bool _populated;
    private CancellationTokenSource? _preview;

    public SoundPicker() => InitializeComponent();

    /// <summary>Text for the "no specific sound" option, e.g. "Windows alert" or "Default from Settings".</summary>
    public string DefaultLabel { get; set; } = "Default";

    /// <summary>Plays a sound reference once; cancelled when the user presses stop.</summary>
    public Func<string, CancellationToken, Task>? Preview { get; set; }

    /// <summary>The selected sound reference; empty for the default.</summary>
    public string Sound
    {
        get
        {
            EnsurePopulated();
            return (Choices.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        }
        set
        {
            EnsurePopulated();
            var reference = value?.Trim() ?? "";
            var match = Options.FirstOrDefault(i => string.Equals((string)i.Tag, reference, StringComparison.OrdinalIgnoreCase));
            // A sound saved earlier may be a file, or a Windows sound missing from this PC; show it rather than lose it.
            Choices.SelectedItem = match ?? AddCustom(reference);
        }
    }

    private IEnumerable<ComboBoxItem> Options => Choices.Items.OfType<ComboBoxItem>().Where(i => i.Tag is string);

    private void EnsurePopulated()
    {
        if (_populated) return;
        _populated = true;
        Choices.Items.Add(Option("", DefaultLabel));
        AddGroup("Built-in", SoundLibrary.BuiltIn);
        AddGroup("Windows", SoundLibrary.WindowsSounds());
        Choices.SelectedIndex = 0;
    }

    private void AddGroup(string title, IReadOnlyList<SoundChoice> choices)
    {
        if (choices.Count == 0) return;
        Choices.Items.Add(Header(title));
        foreach (var choice in choices) Choices.Items.Add(Option(choice.Reference, choice.Name));
    }

    private ComboBoxItem? AddCustom(string reference)
    {
        if (reference.Length == 0 || SoundLibrary.Describe(reference) is not { } choice) return null;
        if (!Choices.Items.OfType<ComboBoxItem>().Any(i => i.Tag is null && (string)i.Content == "Your files"))
            Choices.Items.Add(Header("Your files"));
        var name = SoundLibrary.IsAvailable(reference) ? choice.Name : $"{choice.Name} (missing)";
        var item = Option(reference, name);
        item.ToolTip = choice.Source == SoundSource.File ? reference : null;
        Choices.Items.Add(item);
        return item;
    }

    private static ComboBoxItem Option(string reference, string name) => new() { Content = name, Tag = reference };

    private ComboBoxItem Header(string title) =>
        new() { Content = title, IsEnabled = false, Focusable = false, Style = (Style)FindResource("ComboHeader") };

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Audio files|*.wav;*.mp3|All files|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Sound = dialog.FileName;
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is not null) { _preview.Cancel(); return; }
        if (Preview is null) return;
        _preview = new CancellationTokenSource();
        PreviewButton.Content = StopGlyph;
        PreviewButton.ToolTip = "Stop";
        try { await Preview(Sound, _preview.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            _preview.Dispose();
            _preview = null;
            PreviewButton.Content = PlayGlyph;
            PreviewButton.ToolTip = "Preview this sound";
        }
    }

    private void Picker_Unloaded(object sender, RoutedEventArgs e) => _preview?.Cancel();
}
