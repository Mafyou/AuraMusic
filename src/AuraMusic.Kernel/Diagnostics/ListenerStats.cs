namespace AuraMusic.Kernel.Diagnostics;

/// <summary>
/// What a listener receives on each link and what its playout does, turned once a second into a
/// <see cref="ListenerReport"/>. Thread-safe: the links' threads, the playback thread and the reporting timer all
/// use it.
/// </summary>
public sealed class ListenerStats(TimeProvider timeProvider) : IPlayoutMetrics
{
    readonly Lock counters = new();
    long windowStart = timeProvider.GetTimestamp();
    int bluetooth, wifi, bytes; // since the last report
    int caughtUp, concealed, skipped, rebuffers, dropped; // since the listening started
    int bufferedMs, targetMs;
    double driftPpm;
    // What makes the sound break: the output running dry, and the playback thread being kept from its work.
    int underruns;
    long? lastPlayed;
    TimeSpan longestGap;

    public bool NativeCodec { get; set; }

    /// <summary>The links currently up.</summary>
    public Links Links { get; set; }

    /// <summary>Times the audio output ran dry, as last reported by <see cref="Playing"/>.</summary>
    public int Underruns
    {
        get
        {
            lock (counters)
                return underruns;
        }
    }

    public void CaughtUp() => Count(ref caughtUp);

    public void Concealed() => Count(ref concealed);

    public void Skipped() => Count(ref skipped);

    public void Rebuffered() => Count(ref rebuffers);

    /// <summary>A packet was let through while the jitter buffer was full: the oldest one was dropped.</summary>
    public void Dropped() => Count(ref dropped);

    void Count(ref int counter)
    {
        lock (counters)
            counter++;
    }

    /// <summary>From the playback thread, once per packet played.</summary>
    public void Playing(int buffered, int target, double drift, int outputUnderruns)
    {
        long now = timeProvider.GetTimestamp();
        lock (counters)
        {
            (bufferedMs, targetMs, driftPpm, underruns) = (buffered, target, drift, outputUnderruns);
            if (lastPlayed is { } last && timeProvider.GetElapsedTime(last, now) is var gap && gap > longestGap)
                longestGap = gap;
            lastPlayed = now;
        }
    }

    /// <summary>A packet of <paramref name="packetBytes"/> came in over <paramref name="link"/>, whether used or not.</summary>
    public void Received(Links link, int packetBytes)
    {
        lock (counters)
        {
            if (link == Links.Bluetooth)
                bluetooth++;
            else
                wifi++;
            bytes += packetBytes + AuraProtocol.FramePrefixSize;
        }
    }

    /// <summary>
    /// The figures since the previous report, and starts a new window. Called on a timer rather than on reception:
    /// when nothing arrives (music paused, link lost) the screen must show 0 frames/s, not the last good figures.
    /// </summary>
    public ListenerReport TakeReport()
    {
        lock (counters)
        {
            // Read under the lock: two overlapping calls would otherwise close their windows in the opposite order
            // to their timestamps, and the late one would move the window's start back.
            long now = timeProvider.GetTimestamp();
            double elapsedMs = Math.Max(1, timeProvider.GetElapsedTime(windowStart, now).TotalMilliseconds);
            // 50 frames/s on a link means it keeps up with real time on its own.
            var report = new ListenerReport(NativeCodec, Links, bluetooth * 1000.0 / elapsedMs, wifi * 1000.0 / elapsedMs,
                (int)(bytes * 8.0 / elapsedMs), bufferedMs, targetMs, driftPpm, caughtUp, concealed, skipped, rebuffers, dropped);
            windowStart = now;
            bluetooth = wifi = bytes = 0;
            return report;
        }
    }

    /// <summary>The longest time between two packets played since the previous call.</summary>
    public TimeSpan TakeLongestGap()
    {
        lock (counters)
        {
            var gap = longestGap;
            longestGap = TimeSpan.Zero;
            return gap;
        }
    }
}
