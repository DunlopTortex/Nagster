namespace Nagster.Sounds;

/// <summary>
/// Synthesises the app's built-in alarm sounds as 16-bit mono PCM. Because the sounds are generated
/// from code rather than shipped as recordings, they carry no third-party licence terms.
/// Each sound is roughly two to three seconds long; the player repeats it three times per cycle.
/// </summary>
public static class ToneSynth
{
    public const int SampleRate = 44100;

    /// <summary>Builds the PCM data for a built-in sound, or returns null for an unknown ID.</summary>
    public static byte[]? Render(string id) => id switch
    {
        "beep" => ToPcm(BeepBeep()),
        "chime" => ToPcm(Chime()),
        "siren" => ToPcm(Siren()),
        "pulse" => ToPcm(Pulse()),
        "rise" => ToPcm(GentleRise()),
        "ding" => ToPcm(Ding()),
        _ => null
    };

    /// <summary>Classic digital alarm clock: two bursts of four quick square-ish beeps.</summary>
    private static float[] BeepBeep()
    {
        var buffer = Silence(2.0);
        for (var burst = 0; burst < 2; burst++)
            for (var beep = 0; beep < 4; beep++)
                AddTone(buffer, start: burst * 1.0 + beep * 0.16, length: 0.09, frequency: 2000, volume: 0.5, Waveform.SoftSquare);
        return buffer;
    }

    /// <summary>Three descending bell strikes (E6, C6, G5) with a long natural decay.</summary>
    private static float[] Chime()
    {
        var buffer = Silence(2.8);
        double[] notes = [1318.5, 1046.5, 784.0];
        for (var i = 0; i < notes.Length; i++)
            AddBell(buffer, start: i * 0.45, frequency: notes[i], volume: 0.5, decay: 1.6);
        return buffer;
    }

    /// <summary>Three rising sweeps, like a soft emergency siren.</summary>
    private static float[] Siren()
    {
        var buffer = Silence(2.7);
        for (var i = 0; i < 3; i++)
            AddSweep(buffer, start: i * 0.9, length: 0.8, from: 600, to: 1400, volume: 0.45);
        return buffer;
    }

    /// <summary>Rapid urgent pulses at eight per second.</summary>
    private static float[] Pulse()
    {
        var buffer = Silence(2.0);
        for (var i = 0; i < 12; i++)
            AddTone(buffer, start: i * 0.125, length: 0.06, frequency: 880, volume: 0.5, Waveform.SoftSquare);
        return buffer;
    }

    /// <summary>A gentle marimba-like arpeggio (C5, E5, G5, C6) for less urgent reminders.</summary>
    private static float[] GentleRise()
    {
        var buffer = Silence(2.4);
        double[] notes = [523.3, 659.3, 784.0, 1046.5];
        for (var i = 0; i < notes.Length; i++)
            AddMallet(buffer, start: i * 0.22, frequency: notes[i], volume: 0.55);
        AddMallet(buffer, start: 1.2, frequency: 1046.5, volume: 0.4);
        return buffer;
    }

    /// <summary>A single soft bell strike (A5), used to announce that a pause has ended.</summary>
    private static float[] Ding()
    {
        var buffer = Silence(1.8);
        AddBell(buffer, start: 0, frequency: 880, volume: 0.5, decay: 1.2);
        return buffer;
    }

    private enum Waveform { Sine, SoftSquare }

    private static float[] Silence(double seconds) => new float[(int)(seconds * SampleRate)];

    private static void AddTone(float[] buffer, double start, double length, double frequency, double volume, Waveform shape)
    {
        var first = (int)(start * SampleRate);
        var count = (int)(length * SampleRate);
        for (var n = 0; n < count && first + n < buffer.Length; n++)
        {
            var t = (double)n / SampleRate;
            var phase = 2 * Math.PI * frequency * t;
            // Odd harmonics give a square-wave edge without the harshness of a true square.
            var sample = shape == Waveform.SoftSquare
                ? Math.Sin(phase) + Math.Sin(3 * phase) / 3 + Math.Sin(5 * phase) / 5
                : Math.Sin(phase);
            buffer[first + n] += (float)(sample * volume * Envelope(n, count, attack: 0.005, release: 0.01));
        }
    }

    private static void AddSweep(float[] buffer, double start, double length, double from, double to, double volume)
    {
        var first = (int)(start * SampleRate);
        var count = (int)(length * SampleRate);
        var phase = 0.0;
        for (var n = 0; n < count && first + n < buffer.Length; n++)
        {
            var frequency = from + (to - from) * n / count;
            phase += 2 * Math.PI * frequency / SampleRate;
            // Triangle wave: brighter than a sine but still smooth.
            var triangle = 2 / Math.PI * Math.Asin(Math.Sin(phase));
            buffer[first + n] += (float)(triangle * volume * Envelope(n, count, attack: 0.02, release: 0.05));
        }
    }

    /// <summary>Struck bell: inharmonic partials with exponential decay.</summary>
    private static void AddBell(float[] buffer, double start, double frequency, double volume, double decay)
    {
        (double Ratio, double Level)[] partials = [(1, 1), (2.76, 0.45), (5.4, 0.25), (8.93, 0.1)];
        AddStruck(buffer, start, frequency, volume, decay, partials);
    }

    /// <summary>Mallet on wood: short decay, mostly fundamental with a bright fourth harmonic.</summary>
    private static void AddMallet(float[] buffer, double start, double frequency, double volume)
    {
        (double Ratio, double Level)[] partials = [(1, 1), (4, 0.3), (10, 0.05)];
        AddStruck(buffer, start, frequency, volume, decay: 0.9, partials);
    }

    private static void AddStruck(float[] buffer, double start, double frequency, double volume, double decay,
        (double Ratio, double Level)[] partials)
    {
        var first = (int)(start * SampleRate);
        var count = (int)(decay * 2.5 * SampleRate);
        for (var n = 0; n < count && first + n < buffer.Length; n++)
        {
            var t = (double)n / SampleRate;
            var sample = 0.0;
            // Higher partials die away faster, which is what makes a strike sound natural.
            foreach (var (ratio, level) in partials)
                sample += level * Math.Sin(2 * Math.PI * frequency * ratio * t) * Math.Exp(-t * ratio / decay);
            buffer[first + n] += (float)(sample * volume * Envelope(n, count, attack: 0.002, release: 0.05));
        }
    }

    /// <summary>Linear fade in and out so tones start and stop without clicks.</summary>
    private static double Envelope(int n, int count, double attack, double release)
    {
        var a = attack * SampleRate;
        var r = release * SampleRate;
        if (n < a) return n / a;
        if (count - n < r) return (count - n) / r;
        return 1;
    }

    /// <summary>Normalises to a comfortable peak level and converts to 16-bit little-endian PCM.</summary>
    private static byte[] ToPcm(float[] samples)
    {
        var peak = samples.Max(Math.Abs);
        var gain = peak > 0 ? 0.7 / peak : 0;
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Clamp(samples[i] * gain * short.MaxValue, short.MinValue, short.MaxValue);
            bytes[2 * i] = (byte)value;
            bytes[2 * i + 1] = (byte)(value >> 8);
        }
        return bytes;
    }
}
