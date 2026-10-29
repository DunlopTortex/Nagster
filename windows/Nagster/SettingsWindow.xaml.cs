using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Diagnostics;
using System.Windows.Navigation;
using Nagster.Cloud;
using Nagster.Notifications;
using Nagster.Sounds;
using Nagster.Web;

namespace Nagster;

public partial class SettingsWindow : Window
{
    private AppSettings Settings => ((App)Application.Current).Engine.State.Settings;
    public SettingsWindow()
    {
        InitializeComponent();
        ResponseSeconds.Text = Settings.ResponseSeconds.ToString();
        AutoSnooze.Text = Settings.AutoSnooze;
        SnoozeChoices.Text = Settings.SnoozeChoices;
        DefaultSound.Sound = Settings.DefaultSound;
        DefaultSound.Preview = (sound, token) => SoundCoordinator.Preview(sound, new SoundOptions("", SelectedAudioDevice, SelectedVolume), token);
        PopupOpacity.Value = Settings.PopupOpacity;
        MaxPopups.Text = Settings.MaxVisiblePopups.ToString();
        PopupCorner.SelectedIndex = Settings.PopupCorner switch { "BottomLeft" => 1, "TopRight" => 2, "TopLeft" => 3, _ => 0 };
        StartWithWindows.IsChecked = Settings.StartWithWindows;
        LoadAudioDevices();
        AlarmVolume.Value = Settings.AlarmVolumePercent ?? 80;
        SetAlarmVolume.IsChecked = Settings.AlarmVolumePercent is not null;
        SetAlarmVolume_Changed(this, new RoutedEventArgs());
        QuietEnabled.IsChecked = Settings.QuietHours.Enabled;
        QuietStart.SelectedTime = Settings.QuietHours.Start;
        QuietEnd.SelectedTime = Settings.QuietHours.End;
        QuietEnabled_Changed(this, new RoutedEventArgs());
        var quietDays = QuietDayBoxes;
        for (var i = 0; i < quietDays.Length; i++) quietDays[i].IsChecked = (Settings.QuietDays.Weekdays & (1 << i)) != 0;
        AlarmHoursEnabled.IsChecked = Settings.AlarmHours.Enabled;
        AlarmHoursStart.SelectedTime = Settings.AlarmHours.Start;
        AlarmHoursEnd.SelectedTime = Settings.AlarmHours.End;
        AlarmHoursEnabled_Changed(this, new RoutedEventArgs());
        LoadPushover();
        LoadWebAccess();
        UpdateCalendarSummary();
        CloudPassphraseHint.Text = $"At least {CloudCrypto.MinimumPassphrase} characters; a few unrelated words work well. It never leaves your devices.";
        ShowCloud();
        var cloud = ((App)Application.Current).Cloud;
        cloud.StatusChanged += ShowCloudSoon;
        Closed += (_, _) => cloud.StatusChanged -= ShowCloudSoon;
    }

    private CloudSync Cloud => ((App)Application.Current).Cloud;

    /// <summary>The quiet day chips, Sunday first to match the QuietDays bits.</summary>
    private System.Windows.Controls.CheckBox[] QuietDayBoxes => [QuietSun, QuietMon, QuietTue, QuietWed, QuietThu, QuietFri, QuietSat];

    private void ShowCloudSoon() => Dispatcher.BeginInvoke(ShowCloud);

    private void ShowCloud()
    {
        var on = Cloud.IsOn;
        CloudOffPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        CloudOnPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CloudAccountText.Text = on ? $"Syncing as {Cloud.Email}, end-to-end encrypted." : "";
        CloudStatusText.Text = "Status: " + Cloud.Status;
    }

    private void CloudOwnProject_Changed(object sender, RoutedEventArgs e) =>
        CloudOwnFields.Visibility = CloudOwnProject.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Runs a cloud sync action with its buttons disabled; what it returns, or why it failed, goes under them.</summary>
    private async Task CloudActionAsync(Func<Task<string?>> action)
    {
        CloudOffPanel.IsEnabled = CloudOnPanel.IsEnabled = false;
        CloudStatusText.Text = "Working…";
        string? message;
        try { message = await action(); }
        catch (Exception ex) when (ex is SupabaseException or CloudCryptoException or ArgumentException or System.IO.IOException)
        {
            message = ex.Message;
        }
        finally { CloudOffPanel.IsEnabled = CloudOnPanel.IsEnabled = true; }
        ShowCloud();
        if (message is not null) CloudStatusText.Text = message;
    }

    private Task TurnOnCloudAsync(bool createAccount) => CloudActionAsync(async () =>
    {
        // Nagster's own project, unless the user brings theirs.
        var own = CloudOwnProject.IsChecked == true;
        var url = own ? CloudUrl.Text : SupabaseClient.DefaultProjectUrl;
        var key = own ? CloudKey.Text : SupabaseClient.DefaultPublishableKey;
        if (SupabaseClient.NormalizeUrl(url) is null) return "Enter the project URL, e.g. https://abcd.supabase.co (HTTPS only).";
        if (key.Trim().Length == 0) return "Enter the project's publishable key.";
        if (CloudEmail.Text.Trim().Length == 0 || CloudPassword.Password.Length == 0) return "Enter your email and account password.";
        if (CloudPassphrase.Password.Length == 0) return "Enter your sync passphrase.";
        var message = await Cloud.TurnOnAsync(url, key, CloudEmail.Text, CloudPassword.Password, CloudPassphrase.Password,
            CloudPassphraseAgain.Password, createAccount);
        if (message is null) CloudPassword.Password = CloudPassphrase.Password = CloudPassphraseAgain.Password = "";
        return message;
    });

    private async void CloudOn_Click(object sender, RoutedEventArgs e) => await TurnOnCloudAsync(createAccount: false);

    private async void CloudCreate_Click(object sender, RoutedEventArgs e) => await TurnOnCloudAsync(createAccount: true);

    private async void CloudSync_Click(object sender, RoutedEventArgs e) =>
        await CloudActionAsync(async () => { await Cloud.SyncAsync(); return null; });

    private async void CloudOff_Click(object sender, RoutedEventArgs e) =>
        await CloudActionAsync(async () => { await Cloud.TurnOffAsync(deleteAccount: false); return null; });

    private async void CloudDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Confirm(this, "Delete your cloud sync account?",
                "Your account, everything in the cloud and the encryption vault are deleted for good, and cloud sync turns off on " +
                "all your devices. The alarms on this PC and on each device stay as they are.", "Delete", isDanger: true)) return;
        await CloudActionAsync(async () => { await Cloud.TurnOffAsync(deleteAccount: true); return null; });
    }

    private void UpdateCalendarSummary()
    {
        var sources = Settings.Calendars.Sources;
        CalendarSummary.Text = sources.Count == 0
            ? "No calendars connected."
            : $"{sources.Count(s => s.Enabled)} of {sources.Count} calendar{(sources.Count == 1 ? "" : "s")} importing.";
    }

    private void Calendars_Click(object sender, RoutedEventArgs e)
    {
        new CalendarsWindow { Owner = this }.ShowDialog();
        UpdateCalendarSummary();
    }

    private void LoadWebAccess()
    {
        var web = Settings.WebAccess;
        WebEnabled.IsChecked = web.Enabled;
        WebPort.Text = web.Port.ToString();
        WebPasswordLabel.Text = web.PasswordHash.Length > 0
            ? "New password (leave blank to keep the current one)"
            : $"Password (at least {Web.WebPassword.MinimumLength} characters)";
        var addresses = WebAddresses.For(web.Port);
        WebAddressText.Text = addresses.Count > 0 ? "Open " + string.Join("  or  ", addresses) : "";
        WebStatusText.Text = "Status: " + ((App)Application.Current).WebStatus;
        ShowSignedInDevices();
        WebEnabled_Changed(this, new RoutedEventArgs());
    }

    /// <summary>Lists signed-in devices, e.g. "iPhone · Safari, last used today".</summary>
    private void ShowSignedInDevices()
    {
        var devices = ((App)Application.Current).WebDevices;
        WebDevicesText.Text = devices.Count == 0
            ? "None. Devices stay signed in for 6 months after they were last used."
            : string.Join(Environment.NewLine, devices.Select(d => $"{d.Device}, last used {LastUsed(d.LastUsed)}"));
        SignOutDevicesButton.IsEnabled = devices.Count > 0;
    }

    private static string LastUsed(DateTime when) =>
        when.Date == DateTime.Today ? "today"
        : when.Date == DateTime.Today.AddDays(-1) ? "yesterday"
        : when.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    private void SignOutDevices_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Confirm(this, "Sign out all devices",
                "Every phone and browser signed in to the web interface will need the password again.", "Sign out", isDanger: true)) return;
        ((App)Application.Current).SignOutWebDevices();
        ShowSignedInDevices();
    }

    private void WebEnabled_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the fields exist.
        if (WebFields is null) return;
        WebFields.IsEnabled = WebEnabled.IsChecked == true;
        WebFields.Opacity = WebFields.IsEnabled ? 1 : 0.45;
    }

    private void LoadPushover()
    {
        var pushover = Settings.Pushover;
        PushoverEnabled.IsChecked = pushover.Enabled;
        PushoverUserKey.Password = SecretProtector.Unprotect(pushover.ProtectedUserKey);
        PushoverToken.Password = SecretProtector.Unprotect(pushover.ProtectedToken);
        PushoverDevice.Text = pushover.Device;
        PushoverPriority.SelectedIndex = pushover.Priority == 1 ? 1 : 0;
        PushoverStatus.Text = pushover.LastResult.Length > 0 ? "Last notification: " + pushover.LastResult : "";
        PushoverEnabled_Changed(this, new RoutedEventArgs());
    }

    private void PushoverEnabled_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the fields exist.
        if (PushoverFields is null) return;
        PushoverFields.IsEnabled = PushoverEnabled.IsChecked == true;
        PushoverFields.Opacity = PushoverFields.IsEnabled ? 1 : 0.45;
    }

    /// <summary>Pushover keys are letters and digits only; this catches stray spaces or a pasted label.</summary>
    private static bool LooksLikePushoverKey(string key) => key.Length > 0 && key.All(char.IsAsciiLetterOrDigit);

    private async void PushoverTest_Click(object sender, RoutedEventArgs e)
    {
        var userKey = PushoverUserKey.Password.Trim();
        var token = PushoverToken.Password.Trim();
        if (!LooksLikePushoverKey(userKey) || !LooksLikePushoverKey(token))
        { PushoverStatus.Text = "Enter your user key and an application token first."; return; }
        PushoverTestButton.IsEnabled = false;
        PushoverStatus.Text = "Sending…";
        try
        {
            var result = await PushoverClient.SendAsync(new PushoverMessage(token, userKey, "Nagster test",
                "Pushover is set up. You'll get a message like this when an alarm goes unanswered.",
                PushoverPriority.SelectedIndex, PushoverDevice.Text));
            PushoverStatus.Text = result.Success ? "Test sent. Check your phone." : "Test failed: " + result.Detail;
        }
        finally { PushoverTestButton.IsEnabled = true; }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void QuietEnabled_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the time row exists.
        if (QuietTimesRow is null) return;
        QuietTimesRow.IsEnabled = QuietEnabled.IsChecked == true;
        QuietTimesRow.Opacity = QuietTimesRow.IsEnabled ? 1 : 0.45;
    }

    private void AlarmHoursEnabled_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the time row exists.
        if (AlarmHoursRow is null) return;
        AlarmHoursRow.IsEnabled = AlarmHoursEnabled.IsChecked == true;
        AlarmHoursRow.Opacity = AlarmHoursRow.IsEnabled ? 1 : 0.45;
    }

    private int? SelectedVolume => SetAlarmVolume.IsChecked == true ? (int)AlarmVolume.Value : null;

    private void SetAlarmVolume_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the slider row exists.
        if (AlarmVolumeRow is null) return;
        AlarmVolumeRow.IsEnabled = SetAlarmVolume.IsChecked == true;
        AlarmVolumeRow.Opacity = AlarmVolumeRow.IsEnabled ? 1 : 0.45;
    }

    /// <summary>
    /// Lists connected output devices. A saved device that is currently unplugged stays in the list
    /// (marked as not connected) so saving other settings doesn't silently discard the choice.
    /// </summary>
    private void LoadAudioDevices()
    {
        var saved = Settings.AudioDeviceName;
        var names = AudioDevices.ActiveOutputNames();
        AudioDevice.Items.Add(new ComboBoxItem { Content = "System default", Tag = "" });
        foreach (var name in names) AudioDevice.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        if (!string.IsNullOrWhiteSpace(saved) && !names.Contains(saved, StringComparer.OrdinalIgnoreCase))
            AudioDevice.Items.Add(new ComboBoxItem { Content = $"{saved} (not connected)", Tag = saved });
        AudioDevice.SelectedItem = AudioDevice.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string)i.Tag, saved, StringComparison.OrdinalIgnoreCase)) ?? AudioDevice.Items[0];
    }

    private string SelectedAudioDevice => (string)((ComboBoxItem)AudioDevice.SelectedItem).Tag;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ResponseSeconds.Text, out var seconds) || seconds < 0 || seconds > 86400 ||
            !int.TryParse(MaxPopups.Text, out var max) || max < 1 || max > 5)
        { Error("Check the numeric settings. Response time can be 0 to disable automatic snooze."); return; }
        if (!SnoozeDuration.TryParse(AutoSnooze.Text, out var autoSnooze))
        { Error("Enter the automatic snooze length as a number followed by s, m, h, d, w, mo or y (e.g. 30m or 1d)."); return; }
        var choices = SnoozeDuration.TryParseList(SnoozeChoices.Text);
        if (choices is null)
        {
            Error($"Enter one or more snooze lengths separated by commas, each a number from 1 to {SnoozeDuration.MaxAmount} " +
                  "followed by s, m, h, d, w, mo or y (e.g. 10m, 1h, 1d, 1w). A number on its own means minutes.");
            return;
        }
        if (DefaultSound.Sound.Length > 0 && !SoundLibrary.IsAvailable(DefaultSound.Sound)) { Error("The default sound file can't be found. Choose another sound."); return; }
        if (QuietStart.SelectedTime is not TimeSpan quietStart || QuietEnd.SelectedTime is not TimeSpan quietEnd)
        { Error("Enter a start and end time for quiet hours."); return; }
        if (QuietEnabled.IsChecked == true && quietStart == quietEnd)
        { Error("Quiet hours need different start and end times."); return; }
        if (AlarmHoursStart.SelectedTime is not TimeSpan hoursStart || AlarmHoursEnd.SelectedTime is not TimeSpan hoursEnd)
        { Error("Enter a start and end time for alarm hours."); return; }
        if (AlarmHoursEnabled.IsChecked == true && hoursStart == hoursEnd)
        { Error("Alarm hours need different start and end times."); return; }
        var pushoverUser = PushoverUserKey.Password.Trim();
        var pushoverToken = PushoverToken.Password.Trim();
        if (PushoverEnabled.IsChecked == true && (!LooksLikePushoverKey(pushoverUser) || !LooksLikePushoverKey(pushoverToken)))
        { Error("Enter your Pushover user key and application token (letters and digits only), or turn phone notifications off."); return; }
        var webPassword = WebPassword.Password;
        var webEnabled = WebEnabled.IsChecked == true;
        if (!int.TryParse(WebPort.Text, out var webPort) || webPort is < 1024 or > 65535)
        { Error("Enter a web access port between 1024 and 65535 (the default is 5972)."); return; }
        if (webPassword.Length > 0 && webPassword.Length < Web.WebPassword.MinimumLength)
        { Error($"Use a web access password of at least {Web.WebPassword.MinimumLength} characters."); return; }
        if (webEnabled && webPassword.Length == 0 && Settings.WebAccess.PasswordHash.Length == 0)
        { Error("Set a password before turning on web access."); return; }
        Settings.ResponseSeconds = seconds;
        Settings.AutoSnooze = autoSnooze.ToString();
        Settings.SnoozeChoices = string.Join(", ", choices);
        Settings.DefaultSound = DefaultSound.Sound;
        Settings.QuietHours = new QuietHours { Enabled = QuietEnabled.IsChecked == true, Start = quietStart, End = quietEnd };
        var quietDays = new QuietDays();
        var quietDayBoxes = QuietDayBoxes;
        for (var i = 0; i < quietDayBoxes.Length; i++) if (quietDayBoxes[i].IsChecked == true) quietDays.Weekdays |= 1 << i;
        Settings.QuietDays = quietDays;
        Settings.AlarmHours = new AlarmHours { Enabled = AlarmHoursEnabled.IsChecked == true, Start = hoursStart, End = hoursEnd };
        Settings.Pushover = new PushoverSettings
        {
            Enabled = PushoverEnabled.IsChecked == true,
            ProtectedUserKey = SecretProtector.Protect(pushoverUser),
            ProtectedToken = SecretProtector.Protect(pushoverToken),
            Device = PushoverDevice.Text.Trim(),
            Priority = PushoverPriority.SelectedIndex,
            LastResult = Settings.Pushover.LastResult
        };
        Settings.WebAccess = new WebAccessSettings
        {
            Enabled = webEnabled,
            Port = webPort,
            PasswordHash = webPassword.Length > 0 ? Web.WebPassword.Hash(webPassword) : Settings.WebAccess.PasswordHash
        };
        Settings.AudioDeviceName = SelectedAudioDevice;
        Settings.AlarmVolumePercent = SelectedVolume;
        Settings.PopupOpacity = Math.Round(PopupOpacity.Value, 2);
        Settings.MaxVisiblePopups = max;
        Settings.PopupCorner = (string)((ComboBoxItem)PopupCorner.SelectedItem).Tag;
        Settings.StartWithWindows = StartWithWindows.IsChecked == true;
        ((App)Application.Current).Engine.SaveSettings();
        _ = ((App)Application.Current).ApplyWebAccess(passwordChanged: webPassword.Length > 0);
        ((App)Application.Current).SetStartup(Settings.StartWithWindows);
        DialogResult = true;
    }
    private void Preview_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).PreviewPopup();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void Error(string message) => MessageDialog.Inform(this, "Check settings", message);
}
