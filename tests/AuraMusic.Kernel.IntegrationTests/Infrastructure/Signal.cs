namespace AuraMusic.Kernel.IntegrationTests.Infrastructure;

/// <summary>Audio test signals and how to measure them.</summary>
static class Signal
{
    const int SampleRate = AuraProtocol.SampleRate;
    const int Channels = AuraProtocol.Channels;
    const int FrameSamples = AuraProtocol.FrameSamples;

    /// <summary>One 20 ms stereo frame of a half-scale sine at <paramref name="frequency"/>, <paramref name="frame"/> frames in.</summary>
    public static short[] Tone(double frequency, int frame)
    {
        var pcm = new short[FrameSamples * Channels];
        for (int i = 0; i < FrameSamples; i++)
        {
            long n = (long)frame * FrameSamples + i;
            short sample = (short)(16_000 * Math.Sin(2 * Math.PI * frequency * n / SampleRate));
            pcm[i * Channels] = pcm[i * Channels + 1] = sample;
        }
        return pcm;
    }

    /// <summary>The power of <paramref name="frequency"/> in the left channel of <paramref name="pcm"/> (Goertzel).</summary>
    public static double PowerAt(ReadOnlySpan<float> pcm, double frequency)
    {
        double coefficient = 2 * Math.Cos(2 * Math.PI * frequency / SampleRate);
        double previous = 0, beforePrevious = 0;
        for (int i = 0; i < pcm.Length; i += Channels)
        {
            double current = pcm[i] + coefficient * previous - beforePrevious;
            beforePrevious = previous;
            previous = current;
        }
        return previous * previous + beforePrevious * beforePrevious - coefficient * previous * beforePrevious;
    }

    /// <summary>The RMS level of <paramref name="pcm"/>, where 1 is full scale.</summary>
    public static double Rms(ReadOnlySpan<float> pcm)
    {
        double sum = 0;
        foreach (float sample in pcm)
            sum += sample * sample;
        return pcm.IsEmpty ? 0 : Math.Sqrt(sum / pcm.Length);
    }
}
