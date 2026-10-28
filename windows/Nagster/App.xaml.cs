using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Nagster.Calendars;
using Nagster.Cloud;
using Nagster.Notifications;
using Nagster.Sounds;
using Nagster.Web;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Nagster;

public partial class App : Application, Web.IAlarmActions
{
    private const string PipeName = "Nagster.Activation";
    private Mutex? _mutex;
    private bool _ownsMutex;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _unpauseItem;
    private PauseEndedPopup? _pauseEnded;
    private MainWindow? _main;
    private NewAlarmWindow? _new;
    private QuickReminderWindow? _quick;
    private SettingsWindow? _settings;
    private AlarmCenterWindow? _center;
    private readonly Dictionary<Guid, AlarmPopup> _popups = [];
    private readonly SoundCoordinator _sound = new();
    private DispatcherTimer? _tick;
    private AutoSnoozeNotifier? _notifier;
    private WebServer? _web;
    private static readonly System.Net.Http.HttpClient CloudHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Optional, end-to-end encrypted sync through Supabase; off until turned on in Settings.</summary>
    public CloudSync Cloud { get; private set; } = null!;
    private CalendarImporter? _calendars;

    /// <summary>Reads connected calendars now instead of waiting for the next scheduled check.</summary>
    public Task SyncCalendarsNow() => _calendars?.SyncAllAsync() ?? Task.CompletedTask;

    /// <summary>The web interface's state for Settings, e.g. "Running on port 5972".</summary>
    public string WebStatus => _web?.Status ?? "Off";

    /// <summary>Devices signed in to the web interface, for Settings.</summary>
    public IReadOnlyList<SignedInDevice> WebDevices => _web?.Sessions.Devices() ?? [];

    public void SignOutWebDevices() => _web?.Sessions.EndAll();

    /// <summary>Starts, restarts or stops the web interface to match the current settings.</summary>
    public Task ApplyWebAccess(bool passwordChanged) =>
        _web?.ApplyAsync(Engine.State.Settings.WebAccess, passwordChanged) ?? Task.CompletedTask;
    public AlarmEngine Engine { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WindowPlacement.Register();
        _mutex = new Mutex(true, @"Local\Nagster", out var first);
        _ownsMutex = first;
        var argument = e.Args.FirstOrDefault() ?? "-calendar";
        if (!first)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(3000);
                // This process was started by the user, so it may take the foreground; pass that on to the
                // running instance, which is the one that will actually open the window.
                Foreground.AllowPipeServer(client);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(argument);
            }
            catch (Exception error) { MessageBox.Show("Could not contact the running alarm app: " + error.Message); }
            Shutdown();
            return;
        }
        try
        {
            Engine = new AlarmEngine(new AlarmStore());
            _notifier = new AutoSnoozeNotifier(Engine);
            _web = new WebServer(this);
            Cloud = new CloudSync(CloudHttp, this, action => Dispatcher.InvokeAsync(action).Task);
            // Changes here go up a moment later, so an answered alarm stops ringing on the iPhone quickly.
            Engine.Changed += Cloud.Nudge;
            Cloud.Start();
            _calendars = new CalendarImporter(Engine);
            _ = ApplyWebAccess(passwordChanged: false);
            SetStartup(Engine.State.Settings.StartWithWindows);
            CreateTray();
            RegisterLinks();
            _ = Task.Run(ListenForActivation);
            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (_, _) => ProcessDue();
            _tick.Start();
            HandleActivation(argument);
            ProcessDue();
        }
        catch (Exception error)
        {
            MessageBox.Show("Nagster could not start: " + error, "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task ListenForActivation()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server);
                var argument = await reader.ReadLineAsync();
                // BeginInvoke, not Invoke: a dialog opened here stays open, and waiting for it would stop the
                // listener accepting the next launch (which would then time out).
                if (argument is not null) _ = Dispatcher.BeginInvoke(() => HandleActivation(argument));
            }
            catch { await Task.Delay(1000); }
        }
    }

    private async void HandleActivation(string argument)
    {
        // The cloud sync email confirmation link, opened in a browser on this PC.
        if (argument.StartsWith("nagster:", StringComparison.OrdinalIgnoreCase))
        {
            var message = await Cloud.CompleteSignUpAsync(argument);
            _tray?.ShowBalloonTip(8000, "Nagster cloud sync", message, Forms.ToolTipIcon.Info);
            return;
        }
        switch (argument.ToLowerInvariant())
        {
            case "-tray": break;
            case "-new": ShowNew(); break;
            case "-quick": ShowQuick(); break;
            case "-settings": ShowSettings(); break;
            default: ShowMain(); break;
        }
    }

    private void CreateTray()
    {
        var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("New alarm", null, (_, _) => Dispatcher.Invoke(ShowNew));
        menu.Items.Add("Quick reminder", null, (_, _) => Dispatcher.Invoke(ShowQuick));
        menu.Items.Add("Open calendar", null, (_, _) => Dispatcher.Invoke(ShowMain));
        menu.Items.Add("Active alarms", null, (_, _) => Dispatcher.Invoke(ShowActive));
        menu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        var pause = new Forms.ToolStripMenuItem("Pause alarms for");
        foreach (var (label, length) in PauseChoices)
            pause.DropDownItems.Add(label, null, (_, _) => Dispatcher.Invoke(() => PauseFor(length)));
        menu.Items.Add(pause);
        _unpauseItem = new Forms.ToolStripMenuItem("Unpause", null, (_, _) => Dispatcher.Invoke(Unpause));
        menu.Items.Add(_unpauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Shutdown));
        menu.Opening += (_, _) => UpdatePauseState();
        _tray = new Forms.NotifyIcon { Icon = icon, Text = "Nagster", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMain);
        UpdatePauseState();
    }

    /// <summary>Tray "Pause alarms for" choices; null pauses until Unpause is chosen.</summary>
    private static readonly (string Label, TimeSpan? Length)[] PauseChoices =
    [
        ("30 minutes", TimeSpan.FromMinutes(30)), ("1 hour", TimeSpan.FromHours(1)), ("4 hours", TimeSpan.FromHours(4)),
        ("1 day", TimeSpan.FromDays(1)), ("Forever", null)
    ];

    /// <summary>Pauses every alarm: popups on screen close and go quiet, and nothing new goes off until the pause ends.</summary>
    public void PauseFor(TimeSpan? length)
    {
        Engine.Pause(length is TimeSpan span ? DateTime.Now.Add(span) : DateTime.MaxValue);
        foreach (var (occurrenceId, popup) in _popups)
        {
            _sound.Stop(occurrenceId);
            popup.Stop();
            popup.Close();
        }
        _popups.Clear();
        ClosePauseEnded();
        UpdatePauseState();
    }

    /// <summary>"Snooze" on the pause-ended popup: keeps alarms paused for <paramref name="length"/> more from now.</summary>
    public void ExtendPause(TimeSpan length)
    {
        Engine.Pause(DateTime.Now.Add(length));
        ClosePauseEnded();
        UpdatePauseState();
    }

    private void ShowPauseEnded(DateTime now)
    {
        if (_pauseEnded is not null) return;
        _pauseEnded = new PauseEndedPopup(Engine.State.Settings.PausedUntil ?? now, Engine.Ready(now).Count);
        _pauseEnded.PlaceBottomRight();
        _pauseEnded.Show();
        UpdatePauseState();
        // One ding, on the chosen device and at the alarm volume, but not during quiet hours.
        var options = SoundOptions.From(Engine.State.Settings);
        if (!options.IsQuietAt(now)) _ = SoundCoordinator.Preview(PauseEndedSound, options, CancellationToken.None);
    }

    private const string PauseEndedSound = "builtin:ding";

    private void ClosePauseEnded()
    {
        _pauseEnded?.Close();
        _pauseEnded = null;
    }

    /// <summary>Ends a pause; every alarm that came due meanwhile goes off now.</summary>
    public void Unpause()
    {
        Engine.Resume();
        ClosePauseEnded();
        UpdatePauseState();
        ProcessDue();
    }

    /// <summary>Shows whether alarms are paused, and until when, in the tray tooltip and the Unpause item.</summary>
    private void UpdatePauseState()
    {
        if (_tray is null || _unpauseItem is null) return;
        var now = DateTime.Now;
        var paused = Engine.IsPaused;
        var until = Engine.State.Settings.PausedUntil;
        var when = Engine.PauseExpired(now) ? "you unpause (time's up)"
            : until == DateTime.MaxValue ? "you unpause"
            : until?.Date == now.Date ? until.Value.ToString("t", System.Globalization.CultureInfo.CurrentCulture)
            : until?.ToString("ddd t", System.Globalization.CultureInfo.CurrentCulture);
        _unpauseItem.Enabled = paused;
        _unpauseItem.Text = paused ? $"Unpause (paused until {when})" : "Unpause";
        _tray.Text = paused ? $"Nagster (paused until {when})" : "Nagster";
    }

    public void ShowMain()
    {
        _main ??= new MainWindow();
        _main.Show();
        Foreground.BringToFront(_main);
    }
    public void ShowActive()
    {
        if (_center is null)
        {
            _center = new AlarmCenterWindow();
            _center.Closed += (_, _) => _center = null;
        }
        _center.Show();
        Foreground.BringToFront(_center);
    }
    public void ShowNew()
    {
        if (_new is not null) { Foreground.BringToFront(_new); return; }
        _new = new NewAlarmWindow();
        if (_main?.IsVisible == true) _new.Owner = _main;
        try { Foreground.ShowDialogInFront(_new); }
        finally { _new = null; ProcessDue(); }
    }
    /// <summary>
    /// Edits an alarm. The schedule restarts from <paramref name="occurrence"/> if it hasn't gone off yet,
    /// otherwise from the alarm's next upcoming occurrence (or an hour from now if it has none).
    /// </summary>
    public void ShowEdit(AlarmDefinition definition, AlarmOccurrence occurrence)
    {
        var upcoming = (occurrence.Status is OccurrenceStatus.Pending or OccurrenceStatus.Snoozed) && occurrence.ScheduledFor > DateTime.Now
            ? occurrence : Engine.NextPending(definition.Id);
        var firstAlert = upcoming?.ScheduledFor ?? DateTime.Now.AddHours(1);
        var editor = new NewAlarmWindow(definition, new DateTime(firstAlert.Year, firstAlert.Month, firstAlert.Day, firstAlert.Hour, firstAlert.Minute, 0));
        if (_main?.IsVisible == true) editor.Owner = _main;
        try { Foreground.ShowDialogInFront(editor); }
        finally { ProcessDue(); }
    }
    public void ShowQuick()
    {
        if (_quick is not null) { Foreground.BringToFront(_quick); return; }
        _quick = new QuickReminderWindow();
        if (_main?.IsVisible == true) _quick.Owner = _main;
        try { Foreground.ShowDialogInFront(_quick); }
        finally { _quick = null; ProcessDue(); }
    }
    public void ShowSettings()
    {
        if (_settings is not null) { Foreground.BringToFront(_settings); return; }
        _settings = new SettingsWindow();
        if (_main?.IsVisible == true) _settings.Owner = _main;
        try { Foreground.ShowDialogInFront(_settings); }
        finally { _settings = null; Reposition(); }
    }

    private void ProcessDue()
    {
        var now = DateTime.Now;
        if (Engine.IsPaused)
        {
            Engine.Ready(now); // Still schedules upcoming repeats, so nothing is skipped when the pause ends.
            // A timed pause doesn't end by itself; the user decides whether to unpause or keep it going.
            if (Engine.PauseExpired(now)) ShowPauseEnded(now);
            return;
        }
        foreach (var held in Engine.HoldOutsideAlarmHours(now))
        {
            _sound.Stop(held.Id);
            if (_popups.Remove(held.Id, out var popup)) { popup.Stop(); popup.Close(); }
        }
        var ready = Engine.Ready(now);
        var fitsOnScreen = Math.Max(1, (int)((SystemParameters.WorkArea.Height - 24) / 302));
        var maximum = Math.Min(Math.Clamp(Engine.State.Settings.MaxVisiblePopups, 1, 5), fitsOnScreen);
        foreach (var occurrence in ready.Take(Math.Max(0, maximum - _popups.Count)))
        {
            Engine.Activate(occurrence);
            var definition = Engine.Definition(occurrence);
            var popup = new AlarmPopup(occurrence, definition, Engine.State.Settings);
            _popups.Add(occurrence.Id, popup);
            popup.Show();
            _sound.Start(occurrence.Id, definition.Sound, () => SoundOptions.From(Engine.State.Settings), popup.BeginResponseTimer);
        }
        Reposition();
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        var corner = Engine.State.Settings.PopupCorner;
        var ordered = _popups.Values.OrderBy(p =>
            Engine.State.Occurrences.First(o => o.Id == p.OccurrenceId).Deadline ?? DateTime.MaxValue).ToList();
        var overflow = Engine.Ringing(DateTime.Now).Count(o => o.Status != OccurrenceStatus.Active);
        for (var i = 0; i < ordered.Count; i++)
        {
            var popup = ordered[i];
            popup.Left = corner.EndsWith("Right") ? area.Right - popup.Width - 16 : area.Left + 16;
            popup.Top = corner.StartsWith("Bottom") ? area.Bottom - (i + 1) * (popup.Height + 12) - 12 : area.Top + i * (popup.Height + 12) + 12;
            popup.SetOverflow(overflow);
        }
    }

    private void ClosePopup(AlarmOccurrence occurrence)
    {
        _sound.Stop(occurrence.Id);
        if (_popups.Remove(occurrence.Id, out var popup)) { popup.Stop(); popup.Close(); }
        ProcessDue();
    }
    public void Complete(AlarmOccurrence occurrence) { Engine.Complete(occurrence); ClosePopup(occurrence); }
    public void Dismiss(AlarmOccurrence occurrence) { Engine.Dismiss(occurrence); ClosePopup(occurrence); }
    public bool TrySnooze(AlarmOccurrence occurrence, DateTime next, bool automatic, out string message, bool replay = false)
    {
        var accepted = Engine.TrySnooze(occurrence, next, automatic, out message, replay);
        if (!accepted) return false;
        ClosePopup(occurrence);
        // An automatic snooze means nobody answered; tell the user's phone (at most once per alarm per day).
        if (automatic) _ = _notifier!.OnAutoSnoozedAsync(Engine.Definition(occurrence), occurrence, next);
        return true;
    }
    public bool TryReschedule(AlarmOccurrence occurrence, DateTime next, out string message, bool replay = false)
    {
        var accepted = Engine.TryReschedule(occurrence, next, out message, replay);
        if (accepted) ClosePopup(occurrence);
        return accepted;
    }
    public void DeleteDefinition(Guid definitionId)
    {
        foreach (var occurrence in Engine.State.Occurrences.Where(o => o.DefinitionId == definitionId).ToList())
            if (_popups.Remove(occurrence.Id, out var popup)) { _sound.Stop(occurrence.Id); popup.Stop(); popup.Close(); }
        Engine.Delete(definitionId);
        ProcessDue();
    }

    public void PreviewPopup()
    {
        var sample = new AlarmOccurrence { ScheduledFor = DateTime.Now, AlertAt = DateTime.Now };
        var popup = new AlarmPopup(sample, new AlarmDefinition { Title = "Preview alarm", Description = "This is how your alarm will look." }, Engine.State.Settings);
        popup.IsHitTestVisible = false;
        popup.Show();
        var area = SystemParameters.WorkArea;
        popup.Left = area.Right - popup.Width - 16;
        popup.Top = area.Bottom - popup.Height - 16;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { timer.Stop(); popup.Close(); };
        timer.Start();
    }

    /// <summary>
    /// Registers nagster:// links for this Windows user, so the cloud sync confirmation link opens Nagster. A second
    /// launch with the link hands it to this one, like any other argument.
    /// </summary>
    private static void RegisterLinks()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\nagster");
        key.SetValue("", "URL:Nagster");
        key.SetValue("URL Protocol", "");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{Environment.ProcessPath}\" \"%1\"");
    }

    public void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        key?.DeleteValue("DGNaggingAlarms", false); // Startup entry from before the app was renamed.
        if (enabled) key?.SetValue("Nagster", $"\"{Environment.ProcessPath}\" -tray");
        else key?.DeleteValue("Nagster", false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tick?.Stop();
        _calendars?.Stop();
        if (Cloud is { } cloud) Task.Run(cloud.StopAsync).Wait(TimeSpan.FromSeconds(3));
        // Stopped off the UI thread: a request in flight may be waiting for this (blocked) thread.
        if (_web is { } web) Task.Run(web.StopAsync).Wait(TimeSpan.FromSeconds(3));
        foreach (var occurrenceId in _popups.Keys) _sound.Stop(occurrenceId);
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
