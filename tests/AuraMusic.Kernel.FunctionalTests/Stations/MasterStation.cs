namespace AuraMusic.Kernel.FunctionalTests.Stations;

/// <summary>
/// The master's phone, as the Android broadcast service runs it (BroadcastService, LanServer, ListenerLink), built from
/// the same kernel classes. Only the platform is replaced: the RFCOMM server socket by a TCP server on 127.0.0.1, the
/// network's broadcast address by the test's own beacon port, the audio capture by <see cref="ToneSource"/>, the
/// preferences by <see cref="IPreferences"/>. The tests push the sound frame by frame instead of the capture clock,
/// so a journey runs as fast as the sockets allow.
/// </summary>
sealed class MasterStation : IDisposable
{
    public const string TrustedKey = "trustedListeners";

    /// <summary>A silent connection is not one of ours: as LanServer's hello timeout.</summary>
    static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    /// <summary>One second of sound: how often an idle link hears a keep-alive.</summary>
    const int KeepAliveEveryFrames = 50;

    readonly string name;
    readonly IPreferences preferences;
    readonly int beaconPort;
    readonly TcpListener bluetooth = new(IPAddress.Loopback, 0);
    readonly TcpListener lan = new(IPAddress.Loopback, 0);
    readonly UdpClient beacons = new(AddressFamily.InterNetwork);
    readonly IAudioEncoder encoder;
    readonly CancellationTokenSource stopping = new();
    readonly Lock linksLock = new();
    readonly Lock trustLock = new();
    readonly byte[] packet = new byte[AuraProtocol.MaxPacketSize];
    ImmutableArray<MasterLink> links = [];
    TrustedDevices trusted;
    uint sequence;
    int refused;

    /// <param name="beaconPort">Where the beacon goes on 127.0.0.1, instead of every subnet's broadcast address.</param>
    public MasterStation(string name, IPreferences preferences, int beaconPort, bool nativeCodec = true)
    {
        this.name = name;
        this.preferences = preferences;
        this.beaconPort = beaconPort;
        trusted = TrustedDevices.Parse(preferences.Get(TrustedKey));
        encoder = nativeCodec
            ? OpusCodec.CreateEncoder(AuraProtocol.SampleRate, AuraProtocol.Channels, AuraProtocol.Bitrate)
            : OpusCodec.CreateManagedEncoder(AuraProtocol.SampleRate, AuraProtocol.Channels, AuraProtocol.Bitrate);
        bluetooth.Start();
        lan.Start();
        _ = Task.Run(() => AcceptAsync(bluetooth, Links.Bluetooth, stopping.Token));
        _ = Task.Run(() => AcceptAsync(lan, Links.Wifi, stopping.Token));
        State = new Advertising();
    }

    public string Name => name;

    public uint Session { get; } = (uint)Random.Shared.NextInt64(uint.MaxValue + 1L);

    /// <summary>Where a paired phone "connects over Bluetooth".</summary>
    public IPEndPoint BluetoothEndPoint => (IPEndPoint)bluetooth.LocalEndpoint;

    /// <summary>
    /// A Wi-Fi link is healthy once it has kept up for this many packets: the stand-in for "three seconds with a short
    /// queue", which a link written to synchronously always has.
    /// </summary>
    public int HealthyAfterFrames { get; init; } = 50;

    /// <summary>What the master's screen shows.</summary>
    public AuraState State
    {
        get
        {
            lock (linksLock)
                return field;
        }
        private set;
    }

    /// <summary>The links up, a phone on both Bluetooth and Wi-Fi having two.</summary>
    public ImmutableArray<MasterLink> Connected
    {
        get
        {
            lock (linksLock)
                return links;
        }
    }

    /// <summary>The route of the last packet broadcast.</summary>
    public RoutePlan LastPlan { get; private set; }

    public int Bitrate => encoder.Bitrate;

    /// <summary>Phones turned away over Wi-Fi because they never connected over Bluetooth.</summary>
    public int Refused => Volatile.Read(ref refused);

    /// <summary>Packets broadcast so far.</summary>
    public int Sent => (int)sequence;

    /// <summary>The beacon, sent once (the service sends it every second).</summary>
    public async Task AnnounceAsync(CancellationToken stoppingToken)
    {
        var beacon = new LanBeacon(Session, ((IPEndPoint)lan.LocalEndpoint).Port, name).Encode();
        await beacons.SendAsync(beacon, new IPEndPoint(IPAddress.Loopback, beaconPort), stoppingToken);
    }

    /// <summary>20 ms of captured sound: encoded once, sent on every link the router picks.</summary>
    public void Broadcast(ReadOnlySpan<short> pcm)
    {
        var targets = Connected;
        var statuses = new LinkStatus[targets.Length];
        var carries = new bool[targets.Length];
        for (int i = 0; i < targets.Length; i++)
            statuses[i] = new LinkStatus(targets[i].Hello.Id, targets[i].Kind, targets[i].IsHealthyWifi(HealthyAfterFrames));
        LastPlan = LinkRouter.Plan(statuses, carries);
        if (encoder.Bitrate != LastPlan.Bitrate)
            encoder.Bitrate = LastPlan.Bitrate;

        int length = encoder.Encode(pcm, AuraProtocol.FrameSamples, packet);
        uint frameSequence = sequence++;
        for (int i = 0; i < targets.Length; i++)
        {
            if (carries[i])
                Write(targets[i], link => link.Send(frameSequence, packet.AsSpan(0, length)));
            else if (frameSequence % KeepAliveEveryFrames == 0)
                Write(targets[i], link => link.KeepAlive()); // a Bluetooth link idling behind its phone's Wi-Fi
        }
    }

    /// <summary>The music is paused: no packet, only the keep-alive the links send every second.</summary>
    public void KeepAlive()
    {
        foreach (var link in Connected)
            Write(link, link => link.KeepAlive());
    }

    void Write(MasterLink link, Action<MasterLink> write)
    {
        try
        {
            write(link);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Drop(link); // the listener went: the write fails and the link is closed
        }
    }

    async Task AcceptAsync(TcpListener server, Links kind, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await server.AcceptTcpClientAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            client.NoDelay = true;
            new Thread(() => Serve(client, kind)) { IsBackground = true, Name = $"Master to {kind}" }.Start();
        }
    }

    void Serve(TcpClient client, Links kind)
    {
        MasterLink? link = null;
        try
        {
            var stream = client.GetStream();
            stream.ReadTimeout = (int)HelloTimeout.TotalMilliseconds;
            AuraProtocol.WriteHeader(stream, Session);
            var hello = AuraProtocol.ReadHello(stream);
            if (!Admit(kind, hello))
            {
                Interlocked.Increment(ref refused);
                return;
            }

            link = new MasterLink(client, kind, hello);
            lock (linksLock)
            {
                links = links.Add(link);
                PublishListeners();
            }

            // From now on the listener's keep-alives tell whether it is still there.
            stream.ReadTimeout = Timeout.Infinite;
            while (AuraProtocol.ReadListenerMessage(stream))
                link.Heard();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            // Gone, or not one of ours.
        }
        finally
        {
            if (link is not null)
                Drop(link);
            client.Dispose();
        }
    }

    bool Admit(Links kind, Hello hello)
    {
        lock (trustLock)
        {
            if (kind == Links.Wifi)
                return trusted.Contains(hello.Id); // anyone can reach the port: only paired phones get the stream

            var updated = trusted.With(hello.Id);
            if (!ReferenceEquals(updated, trusted))
            {
                trusted = updated;
                preferences.Set(TrustedKey, updated.Serialize());
            }
            return true;
        }
    }

    void Drop(MasterLink link)
    {
        lock (linksLock)
        {
            if (!links.Contains(link))
                return;
            links = links.Remove(link);
            PublishListeners();
        }
        link.Dispose();
    }

    void PublishListeners()
    {
        ImmutableArray<string> names = [.. links.GroupBy(link => link.Hello.Id).Select(phone => phone.First().Hello.Name)];
        State = names.IsEmpty ? new Advertising() : new Streaming(names);
    }

    public void Dispose()
    {
        stopping.Cancel();
        bluetooth.Stop();
        lan.Stop();
        beacons.Dispose();
        ImmutableArray<MasterLink> left;
        lock (linksLock)
            (left, links) = (links, []);
        foreach (var link in left)
            link.Dispose();
        encoder.Dispose();
        stopping.Dispose();
    }
}

/// <summary>One link of the master to a listener's phone.</summary>
sealed class MasterLink(TcpClient client, Links kind, Hello hello) : IDisposable
{
    readonly NetworkStream stream = client.GetStream();
    int framesSent;
    long lastHeard;

    public Links Kind => kind;

    public Hello Hello => hello;

    public int FramesSent => Volatile.Read(ref framesSent);

    /// <summary>When the listener's keep-alive last came in (<see cref="Environment.TickCount64"/>), 0 before the first.</summary>
    public long LastHeard => Interlocked.Read(ref lastHeard);

    public bool IsHealthyWifi(int afterFrames) => kind == Links.Wifi && FramesSent >= afterFrames;

    public void Send(uint sequence, ReadOnlySpan<byte> packet)
    {
        AuraProtocol.WriteFrame(stream, sequence, packet);
        Interlocked.Increment(ref framesSent);
    }

    public void KeepAlive() => AuraProtocol.WriteKeepAlive(stream);

    public void Heard() => Interlocked.Exchange(ref lastHeard, Environment.TickCount64);

    public void Dispose() => client.Dispose();
}
