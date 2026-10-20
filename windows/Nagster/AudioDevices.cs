using NAudio.CoreAudioApi;

namespace Nagster;

/// <summary>
/// Looks up audio output devices by their friendly name (e.g. "Speakers (Realtek(R) Audio)").
/// Names are stored rather than device IDs because IDs can change when drivers are reinstalled
/// or devices are reconnected, while the name the user picked stays recognisable.
/// </summary>
public static class AudioDevices
{
    /// <summary>Names of the output devices that are currently connected and enabled.</summary>
    public static IReadOnlyList<string> ActiveOutputNames()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var names = new List<string>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device) names.Add(device.FriendlyName);
            }
            return names.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// Returns the active output device called <paramref name="name"/>, or the system default output
    /// when the name is blank or no such device is connected. Returns null if there is no output at all.
    /// The caller owns (and must dispose) the returned device.
    /// </summary>
    public static MMDevice? Resolve(MMDeviceEnumerator enumerator, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (string.Equals(device.FriendlyName, name.Trim(), StringComparison.OrdinalIgnoreCase)) return device;
                device.Dispose();
            }
        }
        return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : null;
    }
}
