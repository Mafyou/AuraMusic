namespace AuraMusic.Mobile.Casting;

/// <summary>An encoded 20 ms packet and its place in the stream.</summary>
readonly record struct EncodedFrame(uint Sequence, byte[] Packet);

/// <summary>
/// One connected listener, over Bluetooth or Wi-Fi, with its own small queue and its own threads so a slow link
/// never stalls the capture or the other listeners. A phone connected both ways has two of these, with the same <see cref="Id"/>.
/// </summary>
sealed class ListenerLink(Stream input, Stream output, Links kind, Action close) : IDisposable
{
    const int QueueCapacity = 50;
    const int MaxFramesPerWrite = 10;
    // A Wi-Fi link whose queue stays this short delivers in real time.
    const int HealthyQueueDepth = 2;
    static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(3);
    static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(1);
    // A listener that sent its keep-alive (AuraProtocol.WriteListenerAlive) and then stopped has gone:
    // Bluetooth would otherwise only notice at its supervision timeout, some twenty seconds later.
    const int SilentListenerMs = 6_000;

    // Bluetooth gets three packets per write rather than one every 20 ms: fewer, fuller radio packets take less
    // airtime, which the other listeners share. 40 ms more delay is nothing next to the Bluetooth cushion.
    const int BluetoothFramesPerWrite = 3;
    const int BluetoothGatherMs = (BluetoothFramesPerWrite - 1) * PlayoutTuning.FrameMs;

    // A second of audio, to lose nothing over a radio stall (the listener gets back in step by itself);
    // beyond that the oldest frames are dropped rather than drift behind.
    readonly Channel<EncodedFrame> queue = Channel.CreateBounded<EncodedFrame>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    Hello? hello;
    long readyAt;
    int dropped;
    int sentTotal, sentReported;
    long lastHeard; // 0 until the listener first sends its keep-alive: older versions never do

    public Links Kind => kind;

    /// <summary>The listener's phone: the same over Bluetooth and Wi-Fi.</summary>
    public Guid Id => hello?.Id ?? Guid.Empty;

    /// <summary>As shown on the master's screen.</summary>
    public string Name => hello?.Name ?? "?";

    /// <summary>
    /// A Wi-Fi link that has been keeping up for a few seconds: the same phone's Bluetooth link can then idle
    /// (it only gets keep-alives) and takes over again the moment this one falls behind.
    /// </summary>
    public bool IsHealthyWifi => kind == Links.Wifi
        && queue.Reader.Count <= HealthyQueueDepth
        && Environment.TickCount64 - readyAt >= HealthyAfter.TotalMilliseconds;

    /// <summary>Sends the header and waits for the listener to say who it is. Blocking.</summary>
    public Hello Handshake(uint session)
    {
        AuraProtocol.WriteHeader(output, session);
        hello = AuraProtocol.ReadHello(input);
        readyAt = Environment.TickCount64;
        return hello;
    }

    /// <summary>For the diagnostics screen: what this link sent since the previous call.</summary>
    public LinkReport Report(double seconds, bool carrying)
    {
        int total = Volatile.Read(ref sentTotal);
        int sent = total - Interlocked.Exchange(ref sentReported, total);
        return new LinkReport(Name, kind, sent / seconds, queue.Reader.Count, carrying);
    }

    public void Enqueue(EncodedFrame frame)
    {
        if (queue.Reader.Count == QueueCapacity)
            Interlocked.Increment(ref dropped);
        queue.Writer.TryWrite(frame);
    }

    /// <summary>Sends the stream until the link drops. Blocking: runs on the link's own thread.</summary>
    public void Run(CancellationToken stoppingToken)
    {
        // The hello is in: from now on silence is judged on the listener's keep-alives, not on a read timeout.
        if (input.CanTimeout)
            input.ReadTimeout = Timeout.Infinite;
        new Thread(Listen) { IsBackground = true, Name = $"AuraMusic from {kind}" }.Start();

        // Send whatever has queued up as a single write: batches grow by themselves
        // when the link falls behind, which costs far less than one write per frame.
        var batch = new MemoryStream();
        long windowStart = Environment.TickCount64;
        int sent = 0, writes = 0;
        var writing = TimeSpan.Zero;
        Task<bool>? waiting = null;
        while (!WentSilent())
        {
            // No sound to send (music paused, or this link idles behind a healthy Wi-Fi one): a keep-alive every
            // second tells the listener the link is quiet, not dead, and lets a dead one fail on write.
            waiting ??= queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
            if (Task.WaitAny([waiting], KeepAliveInterval) < 0)
            {
                AuraProtocol.WriteKeepAlive(output);
                continue;
            }
            if (!waiting.IsCompletedSuccessfully || !waiting.Result)
                return; // stopping, or this link was closed
            waiting = null;

            if (kind == Links.Bluetooth && queue.Reader.Count < BluetoothFramesPerWrite)
                Thread.Sleep(BluetoothGatherMs);

            batch.SetLength(0);
            for (int count = 0; count < MaxFramesPerWrite && queue.Reader.TryRead(out var frame); count++)
            {
                AuraProtocol.WriteFrame(batch, frame.Sequence, frame.Packet);
                sent++;
                Interlocked.Increment(ref sentTotal);
            }
            long writeStart = Stopwatch.GetTimestamp();
            output.Write(batch.GetBuffer(), 0, (int)batch.Length);
            output.Flush();
            writing += Stopwatch.GetElapsedTime(writeStart);
            writes++;

            long elapsed = Environment.TickCount64 - windowStart;
            if (elapsed >= 5_000)
            {
                // Below 50 frames/s the link cannot keep up with real time (or it idles behind Wi-Fi).
                Log.Info(AuraLog.Tag, $"tx {Name} {kind}: {sent * 1000.0 / elapsed:F1} frames/s, {(double)sent / writes:F1} frames/write, "
                    + $"{writing.TotalMilliseconds / writes:F1} ms/write, queued {queue.Reader.Count}, dropped {Interlocked.Exchange(ref dropped, 0)}");
                windowStart = Environment.TickCount64;
                sent = writes = 0;
                writing = TimeSpan.Zero;
            }
        }
    }

    /// <summary>Drains what the listener sends: its keep-alives, which also keep its side of Bluetooth awake.</summary>
    void Listen()
    {
        try
        {
            while (AuraProtocol.ReadListenerMessage(input))
                Interlocked.Exchange(ref lastHeard, Environment.TickCount64);
        }
        catch (Exception)
        {
            // Closed under this thread, or the link broke: either way it is over.
        }
        close(); // the listener hung up: free the sending side too
    }

    bool WentSilent()
    {
        long heard = Interlocked.Read(ref lastHeard);
        return heard != 0 && Environment.TickCount64 - heard > SilentListenerMs;
    }

    public void Dispose()
    {
        queue.Writer.TryComplete();
        close();
    }
}
