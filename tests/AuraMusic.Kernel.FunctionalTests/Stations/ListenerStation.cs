namespace AuraMusic.Kernel.FunctionalTests.Stations;

/// <summary>
/// A listener's phone, as the Android listen service runs it (ListenService), built from the same kernel classes:
/// header, hello, one <see cref="SequenceGate"/> for every link, the listener's keep-alive every second, the watchdog
/// on a silent master, the jitter buffer, the playout, the Opus decoder and the statistics. Only the platform is
/// replaced: RFCOMM by TCP on 127.0.0.1, the beacon's UDP port by a test port, the <c>AudioTrack</c> by
/// <see cref="Speaker"/>. The tests turn the playback loop by hand, one 20 ms packet per <see cref="Play"/> turn.
/// </summary>
sealed class ListenerStation : IDisposable
{
    const int MaxBufferedFrames = 96;
    const int CatchUpFrames = AuraProtocol.SampleRate / 1000;
    static readonly TimeSpan AliveInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    readonly Hello phone;
    readonly SequenceGate gate = new();
    readonly ListenerStats stats = new(TimeProvider.System);
    readonly Channel<OpusPacket> packets = Channel.CreateBounded<OpusPacket>(
        new BoundedChannelOptions(MaxBufferedFrames) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
        static dropped => dropped.Return());
    readonly IAudioDecoder decoder;
    readonly PlayoutController playout;
    readonly DriftController drift = new(AuraProtocol.SampleRate);
    readonly LatencyTarget target;
    readonly float[] pcm = new float[AuraProtocol.FrameSamples * AuraProtocol.Channels];
    readonly CancellationTokenSource stopping = new();
    readonly Lock linksLock = new();
    readonly ConcurrentQueue<uint> accepted = new();
    readonly ConcurrentBag<ListenerLink> open = [];
    int bluetoothLinks, wifiLinks;
    string master = "AuraMusic";
    int ignoredBeacons;

    public ListenerStation(string name, bool nativeCodec = true)
    {
        phone = new Hello(Guid.NewGuid(), name);
        decoder = nativeCodec
            ? OpusCodec.CreateDecoder(AuraProtocol.SampleRate, AuraProtocol.Channels)
            : OpusCodec.CreateManagedDecoder(AuraProtocol.SampleRate, AuraProtocol.Channels);
        stats.NativeCodec = decoder.IsNative;
        playout = new PlayoutController(stats);
        target = new LatencyTarget(playout, drift);
        State = new Searching();
    }

    public Hello Phone => phone;

    /// <summary>A master silent for this long (no packet, no keep-alive) is dropped. Five seconds on the phones.</summary>
    public TimeSpan LinkTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>What the listener's screen shows.</summary>
    public AuraState State
    {
        get
        {
            lock (linksLock)
                return field;
        }
        private set;
    }

    public Speaker Speaker { get; } = new();

    public ListenerStats Stats => stats;

    /// <summary>The sequence numbers let through, in the order they were.</summary>
    public IReadOnlyCollection<uint> Accepted => accepted;

    /// <summary>Beacons heard from another broadcast than the one followed.</summary>
    public int IgnoredBeacons => Volatile.Read(ref ignoredBeacons);

    /// <summary>The latency the playback aims for.</summary>
    public int LatencyMs => target.LatencyMs;

    /// <summary>Times a much higher latency had the playback pause to refill.</summary>
    public int Refills { get; private set; }

    /// <summary>The playout's decisions, one per <see cref="Play"/> turn.</summary>
    public List<string> Steps { get; } = [];

    /// <summary>Connects to the master's "RFCOMM" socket, as the paired-phones loop does.</summary>
    public async Task<ListenerLink> ConnectBluetoothAsync(MasterStation station, CancellationToken stoppingToken)
    {
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await client.ConnectAsync(station.BluetoothEndPoint, stoppingToken);
        return Follow(client, Links.Bluetooth, station.Name);
    }

    /// <summary>Waits for beacons and follows the first one from the broadcast this phone may join, over Wi-Fi.</summary>
    public async Task<ListenerLink> FollowBeaconAsync(UdpClient beacons, CancellationToken stoppingToken)
    {
        while (true)
        {
            var received = await beacons.ReceiveAsync(stoppingToken);
            if (!LanBeacon.TryDecode(received.Buffer, out var beacon))
                continue;
            if (gate.Session is { } following && beacon.Session != following)
            {
                Interlocked.Increment(ref ignoredBeacons); // another master on the same network
                continue;
            }

            var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                connecting.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(received.RemoteEndPoint.Address, beacon.Port, connecting.Token);
            }
            return Follow(client, Links.Wifi, beacon.Name);
        }
    }

    ListenerLink Follow(TcpClient client, Links kind, string name)
    {
        var link = new ListenerLink(client, kind);
        open.Add(link);
        link.Ended = Task.Factory.StartNew(() => ReadLink(link, name), stopping.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return link;
    }

    /// <returns><see langword="false"/> when the link gave up before the sound came: refused, or another broadcast.</returns>
    bool ReadLink(ListenerLink link, string name)
    {
        var stream = link.Stream;
        var stoppingToken = stopping.Token;
        try
        {
            stream.ReadTimeout = (int)LinkTimeout.TotalMilliseconds; // until the watchdog takes over
            uint session = AuraProtocol.ReadHeader(stream);
            if (!gate.Join(session))
                return false; // the other link follows another master: do not mix two broadcasts
            AuraProtocol.WriteHello(stream, phone);
            stream.ReadTimeout = Timeout.Infinite;

            long lastHeard = Environment.TickCount64;
            using var watchdog = new Timer(_ =>
            {
                if (Environment.TickCount64 - Interlocked.Read(ref lastHeard) > LinkTimeout.TotalMilliseconds)
                {
                    link.Close();
                    return;
                }
                try
                {
                    AuraProtocol.WriteListenerAlive(stream);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // The read below notices too.
                }
            }, null, TimeSpan.FromMilliseconds(Math.Min(AliveInterval.TotalMilliseconds, LinkTimeout.TotalMilliseconds / 4)),
                TimeSpan.FromMilliseconds(Math.Min(AliveInterval.TotalMilliseconds, LinkTimeout.TotalMilliseconds / 4)));

            bool up = false;
            try
            {
                var buffer = new byte[AuraProtocol.MaxPacketSize];
                while (!stoppingToken.IsCancellationRequested)
                {
                    int length = AuraProtocol.ReadFrame(stream, buffer, out uint sequence);
                    Interlocked.Exchange(ref lastHeard, Environment.TickCount64);
                    if (!up)
                    {
                        up = true;
                        LinkUp(link.Kind, name);
                    }
                    if (length == 0)
                        continue; // keep-alive
                    stats.Received(link.Kind, length);
                    if (!gate.TryAccept(session, sequence))
                        continue; // the other link was faster
                    if (packets.Reader.Count == MaxBufferedFrames)
                        stats.Dropped();
                    var packet = OpusPacket.Rent(buffer.AsSpan(0, length));
                    if (packets.Writer.TryWrite(packet))
                        accepted.Enqueue(sequence);
                    else
                        packet.Return();
                }
            }
            catch (Exception ex) when (!up && ex is IOException or ObjectDisposedException)
            {
                return false; // refused, or gone before sending anything
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The link dropped.
            }
            finally
            {
                if (up)
                    LinkDown(link.Kind);
                else
                    ForgetBroadcastIfAlone();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            ForgetBroadcastIfAlone();
            return false;
        }
        finally
        {
            link.Close();
        }
    }

    void ForgetBroadcastIfAlone()
    {
        lock (linksLock)
        {
            if (bluetoothLinks + wifiLinks == 0)
                gate.Reset();
        }
    }

    void LinkUp(Links kind, string name)
    {
        lock (linksLock)
        {
            master = name;
            if (kind == Links.Bluetooth)
                bluetoothLinks++;
            else
                wifiLinks++;
            PublishLinks();
        }
    }

    void LinkDown(Links kind)
    {
        lock (linksLock)
        {
            if (kind == Links.Bluetooth)
                bluetoothLinks--;
            else
                wifiLinks--;
            if (bluetoothLinks + wifiLinks == 0)
                gate.Reset(); // the next master found may be another broadcast
            PublishLinks();
        }
    }

    void PublishLinks()
    {
        var links = (bluetoothLinks > 0 ? Links.Bluetooth : Links.None) | (wifiLinks > 0 ? Links.Wifi : Links.None);
        stats.Links = links;
        State = links == Links.None ? new Searching() : new Listening(master, links);
    }

    /// <summary>Packets waiting in the jitter buffer.</summary>
    public int Buffered => packets.Reader.Count;

    /// <summary>Turns of the playback loop: one packet each, or what the playout makes of its absence.</summary>
    public void Play(int turns = 1)
    {
        for (int turn = 0; turn < turns; turn++)
        {
            if (target.Retarget(PlayoutTuning.LatencyFor(stats.Links)))
            {
                Refills++;
                playout.Resynced();
                drift.Restart();
            }

            OpusPacket? packet = packets.Reader.TryRead(out var ready) ? ready : (OpusPacket?)null;
            try
            {
                switch (playout.Next(packet, packets.Reader.Count))
                {
                    case Play(var data):
                        decoder.Decode(data.Span, pcm, AuraProtocol.FrameSamples);
                        Speaker.Write(pcm);
                        Steps.Add(nameof(Play));
                        break;
                    case CatchUp(var data):
                        decoder.Decode(data.Span, pcm, AuraProtocol.FrameSamples);
                        Speaker.Write(pcm.AsSpan(0, PcmCrossfade.Shorten(pcm, AuraProtocol.Channels, CatchUpFrames, 2 * CatchUpFrames)));
                        Steps.Add(nameof(CatchUp));
                        break;
                    case Conceal:
                        decoder.Decode([], pcm, AuraProtocol.FrameSamples);
                        drift.RanDry();
                        Speaker.Write(pcm);
                        Steps.Add(nameof(Conceal));
                        break;
                    case Skip(var data):
                        decoder.Decode(data.Span, pcm, AuraProtocol.FrameSamples);
                        Steps.Add(nameof(Skip));
                        break;
                    case Rebuffer:
                        Steps.Add(nameof(Rebuffer));
                        break;
                }
            }
            finally
            {
                packet?.Return();
            }

            double bufferedMs = (packets.Reader.Count + 1) * PlayoutTuning.FrameMs + PlayoutTuning.OutputShareMs;
            drift.Update(bufferedMs);
            stats.Playing((int)bufferedMs, target.ReachableMs, drift.Correction * 1e6, 0);
        }
    }

    /// <summary>The service stops: every link closes.</summary>
    public void Dispose()
    {
        stopping.Cancel();
        foreach (var link in open)
            link.Close();
        foreach (var link in open)
            link.Ended.Wait(TimeSpan.FromSeconds(5));
        packets.Writer.TryComplete();
        while (packets.Reader.TryRead(out var left))
            left.Return();
        decoder.Dispose();
        stopping.Dispose();
    }
}

/// <summary>One of the listener's links to a master.</summary>
sealed class ListenerLink(TcpClient client, Links kind)
{
    public Links Kind => kind;

    public NetworkStream Stream { get; } = client.GetStream();

    /// <summary>
    /// Completes when the link is over: <see langword="true"/> if it carried the stream, <see langword="false"/> if it
    /// gave up first (refused over Wi-Fi, or another master's broadcast).
    /// </summary>
    public Task<bool> Ended { get; set; } = Task.FromResult(false);

    /// <summary>The phone loses this link (out of range, Wi-Fi off).</summary>
    public void Close() => client.Dispose();
}
