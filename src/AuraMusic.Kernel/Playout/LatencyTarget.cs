namespace AuraMusic.Kernel.Playout;

/// <summary>
/// The latency the playback aims for, and what follows from it: the cushion of the jitter buffer and the level the
/// drift controller steers to. Moved by the sync slider, by Wi-Fi coming or going, and by <see cref="AdaptiveLatency"/>.
/// </summary>
/// <param name="outputMs">What the audio output really holds: a phone may refuse a buffer as small as asked.</param>
public sealed class LatencyTarget(PlayoutController playout, DriftController drift, int outputMs = PlayoutTuning.OutputShareMs)
{
    /// <summary>A latency raised by this much at once (Wi-Fi lost, the slider pushed up) is reached by pausing to refill.</summary>
    public const int RefillAboveMs = 100;

    /// <summary>The latency wished for, in milliseconds; 0 until the first <see cref="Retarget"/>.</summary>
    public int LatencyMs { get; private set; }

    /// <summary>The latency actually aimed for (see <see cref="PlayoutTuning.ReachableLatencyMs"/>).</summary>
    public int ReachableMs { get; private set; }

    /// <summary>Aims for <paramref name="wantedMs"/>, if that is new.</summary>
    /// <returns>
    /// <see langword="true"/> when much more must now be kept in hand than before: playing slower would take minutes
    /// to get there, with the sound at the mercy of the first stall, so the caller should refill instead.
    /// </returns>
    public bool Retarget(int wantedMs)
    {
        if (wantedMs == LatencyMs)
            return false;
        bool farAbove = wantedMs - LatencyMs >= RefillAboveMs;
        LatencyMs = wantedMs;
        ReachableMs = PlayoutTuning.ReachableLatencyMs(wantedMs, outputMs);
        playout.SetCushion(PlayoutTuning.CushionFrames(wantedMs, outputMs));
        drift.TargetMs = ReachableMs;
        return farAbove;
    }
}
