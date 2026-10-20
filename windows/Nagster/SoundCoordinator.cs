using System.Media;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Nagster.Sounds;
using NAudio.Wave;

namespace Nagster;

/// <summary>
/// Plays alarm sounds: each active alarm plays its sound three times, waits a minute and repeats,
/// with one alarm audible at a time. Sounds go to the output device named in Settings when it is
/// connected, otherwise to the system default. If Settings has an alarm volume, the device is set to
/// it for each burst and then restored. Bursts that fall in quiet hours are skipped; the alarm stays
/// on screen and starts sounding at its next repeat after quiet hours end.
/// </summary>
public sealed class SoundCoordinator
{
    private readonly SemaphoreSlim _playback = new(1, 1);
    private readonly Dictionary<Guid, CancellationTokenSource> _tokens = [];

    /// <summary>
    /// Starts an alarm's sound. <paramref name="options"/> is read before every burst so changes made
    /// in Settings while an alarm is ringing take effect at its next repeat.
    /// </summary>
    public void Start(Guid occurrenceId, string? selectedSound, Func<SoundOptions> options, Action firstPlayback)
    {
        Stop(occurrenceId);
        var cancellation = new CancellationTokenSource();
        _tokens[occurrenceId] = cancellation;
        _ = PlayLoop(selectedSound, options, firstPlayback, cancellation.Token);
    }

    public void Stop(Guid occurrenceId)
    {
        if (!_tokens.Remove(occurrenceId, out var token)) return;
        token.Cancel();
        token.Dispose();
    }

    private async Task PlayLoop(string? selectedSound, Func<SoundOptions> options, Action firstPlayback, CancellationToken token)
    {
        var first = true;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _playback.WaitAsync(token);
                try
                {
                    // The callback starts the popup's auto-snooze countdown, so it runs even when silenced.
                    if (first) { firstPlayback(); first = false; }
                    var current = options();
                    if (!current.IsQuietAt(DateTime.Now)) await PlayBurst(selectedSound, current, repeats: 3, token);
                }
                finally { _playback.Release(); }
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
        }
        catch (OperationCanceledException) { }
        catch { if (first) firstPlayback(); }
    }

    /// <summary>
    /// Plays a sound once as an alarm would, including the alarm volume; used for previews. Quiet hours
    /// are deliberately ignored because a preview is something the user asked to hear.
    /// </summary>
    public static Task Preview(string? selectedSound, SoundOptions options, CancellationToken token) =>
        PlayBurst(selectedSound, options, repeats: 1, token);

    /// <summary>
    /// Plays a sound <paramref name="repeats"/> times with short gaps. The volume is set once for the whole
    /// burst, so other audio isn't pumped up and down between repeats, and restored when it ends or is cancelled.
    /// </summary>
    private static async Task PlayBurst(string? selectedSound, SoundOptions options, int repeats, CancellationToken token)
    {
        using var volume = VolumeOverride.Apply(options.DeviceName, options.VolumePercent);
        for (var i = 0; i < repeats; i++)
        {
            token.ThrowIfCancellationRequested();
            await PlayOnce(selectedSound, options, token);
            if (i < repeats - 1) await Task.Delay(300, token);
        }
    }

    /// <summary>
    /// Plays a sound once on the chosen device (or the default). The alarm's own sound is used if
    /// available, then the default from Settings, then the user's Windows alert sound.
    /// </summary>
    private static async Task PlayOnce(string? selectedSound, SoundOptions options, CancellationToken token)
    {
        var sound = SoundLibrary.IsAvailable(selectedSound) ? selectedSound
            : SoundLibrary.IsAvailable(options.DefaultSound) ? options.DefaultSound
            : SystemAlertSoundPath();
        if (sound is not null && await TryPlay(sound, options.DeviceName, token)) return;
        // No usable file or output device: fall back to the Windows alert, which always uses the default device.
        SystemSounds.Exclamation.Play();
        await Task.Delay(800, token);
    }

    private static async Task<bool> TryPlay(string sound, string deviceName, CancellationToken token)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            // Resolved on every play so a device plugged in (or removed) after the alarm started is honoured.
            using var device = AudioDevices.Resolve(enumerator, deviceName);
            if (device is null) return false;
            using var reader = SoundLibrary.Open(sound);
            if (reader is null) return false;
            await using var player = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithCategory(AudioStreamCategory.Alerts)
                .Build();
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.PlaybackStopped += (_, _) => finished.TrySetResult();
            player.Init(reader);
            player.Play();
            try { await finished.Task.WaitAsync(TimeSpan.FromSeconds(30), token); }
            catch (TimeoutException) { }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return false; }
    }

    /// <summary>The file behind the user's Windows "Exclamation" sound, so it can be sent to the chosen device.</summary>
    private static string? SystemAlertSoundPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes\Apps\.Default\SystemExclamation\.Current");
        var path = key?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Environment.ExpandEnvironmentVariables(path);
        return File.Exists(path) ? path : null;
    }
}
