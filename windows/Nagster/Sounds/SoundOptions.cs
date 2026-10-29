namespace Nagster.Sounds;

/// <summary>
/// How alarm sounds are played: the fallback sound, the output device, an optional volume (0–100)
/// to set on that device while the alarm sounds (null leaves the system volume alone), and the quiet
/// hours and quiet days during which alarms make no sound.
/// </summary>
public sealed record SoundOptions(string DefaultSound, string DeviceName, int? VolumePercent, QuietHours? Quiet = null,
    QuietDays? QuietDays = null)
{
    public static SoundOptions From(AppSettings settings) =>
        new(settings.DefaultSound, settings.AudioDeviceName, settings.AlarmVolumePercent, settings.QuietHours, settings.QuietDays);

    public bool IsQuietAt(DateTime moment) => Quiet?.Contains(moment) == true || QuietDays?.Contains(moment) == true;
}
