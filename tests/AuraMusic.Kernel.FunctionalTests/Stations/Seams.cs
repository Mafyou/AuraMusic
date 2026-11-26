namespace AuraMusic.Kernel.FunctionalTests.Stations;

/// <summary>Where the app keeps small settings (MAUI <c>Preferences</c> on the phones).</summary>
interface IPreferences
{
    string? Get(string key);

    void Set(string key, string value);
}

/// <summary>Preferences kept in memory: one instance per phone, kept across a restart of its services.</summary>
sealed class InMemoryPreferences : IPreferences
{
    readonly ConcurrentDictionary<string, string> values = new();

    public string? Get(string key) => values.TryGetValue(key, out var value) ? value : null;

    public void Set(string key, string value) => values[key] = value;
}

/// <summary>
/// What the master captures from the music app (<c>AudioPlaybackCapture</c> on the phone): a pure tone, so that what
/// the listener plays can be checked for it.
/// </summary>
sealed class ToneSource(double frequency)
{
    int frame;

    public double Frequency => frequency;

    /// <summary>The next 20 ms of interleaved 16-bit stereo PCM.</summary>
    public short[] Next()
    {
        var pcm = new short[AuraProtocol.FrameSamples * AuraProtocol.Channels];
        for (int i = 0; i < AuraProtocol.FrameSamples; i++)
        {
            long n = (long)frame * AuraProtocol.FrameSamples + i;
            short sample = (short)(16_000 * Math.Sin(2 * Math.PI * frequency * n / AuraProtocol.SampleRate));
            pcm[i * AuraProtocol.Channels] = pcm[i * AuraProtocol.Channels + 1] = sample;
        }
        frame++;
        return pcm;
    }
}

/// <summary>What the listener's phone sends to its speaker (<c>AudioTrack</c> on the phone).</summary>
sealed class Speaker
{
    readonly List<float> played = [];

    public int Samples => played.Count;

    public void Write(ReadOnlySpan<float> pcm) => played.AddRange(pcm);

    /// <summary>The last <paramref name="frames"/> 20 ms frames that were played.</summary>
    public float[] Last(int frames)
    {
        int samples = Math.Min(played.Count, frames * AuraProtocol.FrameSamples * AuraProtocol.Channels);
        return [.. played.Skip(played.Count - samples)];
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="pcm"/> is clearly a tone at <paramref name="frequency"/>: loud enough,
    /// and far stronger there than at a frequency it does not contain.
    /// </summary>
    public static bool IsTone(ReadOnlySpan<float> pcm, double frequency)
    {
        double rms = 0;
        foreach (float sample in pcm)
            rms += sample * sample;
        rms = pcm.IsEmpty ? 0 : Math.Sqrt(rms / pcm.Length);
        return rms > 0.1 && PowerAt(pcm, frequency) > 100 * PowerAt(pcm, frequency * 2.7);
    }

    /// <summary>The power of <paramref name="frequency"/> in the left channel (Goertzel).</summary>
    static double PowerAt(ReadOnlySpan<float> pcm, double frequency)
    {
        double coefficient = 2 * Math.Cos(2 * Math.PI * frequency / AuraProtocol.SampleRate);
        double previous = 0, beforePrevious = 0;
        for (int i = 0; i < pcm.Length; i += AuraProtocol.Channels)
        {
            double current = pcm[i] + coefficient * previous - beforePrevious;
            beforePrevious = previous;
            previous = current;
        }
        return previous * previous + beforePrevious * beforePrevious - coefficient * previous * beforePrevious;
    }
}
