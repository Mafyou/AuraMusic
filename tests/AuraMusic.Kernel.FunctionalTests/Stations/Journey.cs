namespace AuraMusic.Kernel.FunctionalTests.Stations;

/// <summary>Helpers that move a journey forward the way time would.</summary>
static class Journey
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Waits for something the other phone's threads do; fails the test if it never happens.</summary>
    public static async Task EventuallyAsync(Func<bool> condition, string what, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(Patience);
        if (SpinWait.SpinUntil(condition, TimeSpan.FromMilliseconds(50)))
            return; // the common case, a packet on its way through the loopback
        try
        {
            while (!condition())
                await Task.Delay(5, timeout.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            throw new ShouldAssertException($"Still waiting after {Patience.TotalSeconds:F0} s for: {what}");
        }
    }

    /// <summary>
    /// <paramref name="frames"/> packets of music in real-time order: the master sends one, it reaches the listener,
    /// the listener plays one. Before that, the listener's cushion is filled as its playback would wait for it.
    /// </summary>
    public static async Task StreamAsync(MasterStation master, ToneSource music, ListenerStation listener, int frames, CancellationToken stoppingToken)
    {
        int expected = listener.Accepted.Count;
        int cushion = Math.Max(0, PlayoutTuning.CushionFrames(PlayoutTuning.LatencyFor(listener.Stats.Links)) - listener.Buffered);
        for (int i = 0; i < cushion; i++)
            master.Broadcast(music.Next());
        await ArrivedAsync(listener, expected += cushion, stoppingToken);

        for (int i = 0; i < frames; i++)
        {
            master.Broadcast(music.Next());
            await ArrivedAsync(listener, ++expected, stoppingToken);
            listener.Play();
        }
    }

    /// <summary>Waits until the listener has let <paramref name="count"/> packets through since it started.</summary>
    public static Task ArrivedAsync(ListenerStation listener, int count, CancellationToken stoppingToken) =>
        EventuallyAsync(() => listener.Accepted.Count >= count, $"{count} packets at {listener.Phone.Name}", stoppingToken);

    /// <summary>A UDP socket on 127.0.0.1 standing in for the beacon port of the local network.</summary>
    public static UdpClient BeaconPort() => new(new IPEndPoint(IPAddress.Loopback, 0));

    public static int PortOf(UdpClient beacons) => ((IPEndPoint)beacons.Client.LocalEndPoint!).Port;
}
