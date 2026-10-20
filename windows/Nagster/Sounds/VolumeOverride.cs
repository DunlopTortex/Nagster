using NAudio.CoreAudioApi;

namespace Nagster.Sounds;

/// <summary>
/// Temporarily sets an output device's system volume (and unmutes it) so an alarm is heard at a chosen
/// level, then puts the volume back on dispose. If the user changes the volume or mute while the alarm
/// is sounding, their change is kept rather than overwritten.
/// </summary>
public sealed class VolumeOverride : IDisposable
{
    // Endpoint volume is a float; allow for rounding when checking whether it is still the level we set.
    private const float Tolerance = 0.005f;

    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private readonly float _previousLevel;
    private readonly bool _previousMute;
    private readonly float _appliedLevel;

    private VolumeOverride(MMDeviceEnumerator enumerator, MMDevice device, float level)
    {
        _enumerator = enumerator;
        _device = device;
        var volume = device.AudioEndpointVolume;
        _previousLevel = volume.MasterVolumeLevelScalar;
        _previousMute = volume.Mute;
        _appliedLevel = level;
        volume.MasterVolumeLevelScalar = level;
        volume.Mute = false;
    }

    /// <summary>
    /// Sets the volume of the device alarms will play on (see <see cref="AudioDevices.Resolve"/>).
    /// </summary>
    /// <returns>An override to dispose when the alarm stops, or null when no level is set or the volume can't be changed.</returns>
    public static VolumeOverride? Apply(string deviceName, int? percent)
    {
        if (percent is not int value) return null;
        var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        try
        {
            device = AudioDevices.Resolve(enumerator, deviceName);
            if (device is not null) return new VolumeOverride(enumerator, device, Math.Clamp(value, 0, 100) / 100f);
        }
        catch (Exception)
        {
            // Some virtual or remote audio devices refuse volume changes; play at the current volume instead.
        }
        device?.Dispose();
        enumerator.Dispose();
        return null;
    }

    public void Dispose()
    {
        try
        {
            var volume = _device.AudioEndpointVolume;
            var untouched = Math.Abs(volume.MasterVolumeLevelScalar - _appliedLevel) < Tolerance && !volume.Mute;
            if (untouched)
            {
                volume.MasterVolumeLevelScalar = _previousLevel;
                volume.Mute = _previousMute;
            }
        }
        catch (Exception)
        {
            // The device may have been unplugged mid-alarm; there is nothing left to restore.
        }
        finally
        {
            _device.Dispose();
            _enumerator.Dispose();
        }
    }
}
