namespace AuraMusic.Kernel.IntegrationTests.Multipath;

/// <summary>
/// How a listener finds a master on the local network: a UDP beacon carrying the TCP port, then the stream over TCP.
/// Sent to 127.0.0.1 on a port of the test's own rather than broadcast on <see cref="LanBeacon.UdpPort"/>, so runs never
/// meet a real master or one another.
/// </summary>
public sealed class BeaconDiscoveryTests
{
    static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    static UdpClient BeaconReceiver() => new(new IPEndPoint(IPAddress.Loopback, 0));

    static async Task<UdpReceiveResult> ReceiveAsync(UdpClient receiver, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(ReceiveTimeout);
        return await receiver.ReceiveAsync(timeout.Token);
    }

    [Fact]
    public async Task Beacon_SentOverUdp_IsDecodedAsSent()
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        using var receiver = BeaconReceiver();
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var beacon = new LanBeacon(0xBEEF_0042, 50_123, "Salon de Noël");

        // Act
        await sender.SendAsync(beacon.Encode(), (IPEndPoint)receiver.Client.LocalEndPoint!, stoppingToken);
        var datagram = await ReceiveAsync(receiver, stoppingToken);

        // Assert
        LanBeacon.TryDecode(datagram.Buffer, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(beacon);
        IPAddress.IsLoopback(datagram.RemoteEndPoint.Address).ShouldBeTrue();
    }

    [Fact]
    public async Task OtherTrafficOnThePort_IsSkipped_UntilABeaconComes()
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        using var receiver = BeaconReceiver();
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var target = (IPEndPoint)receiver.Client.LocalEndPoint!;
        var beacon = new LanBeacon(7, 40_000, "Cuisine");

        // Act
        await sender.SendAsync("M-SEARCH * HTTP/1.1"u8.ToArray(), target, stoppingToken);
        byte[] futureVersion = [.. "AURB"u8, 9, 0, 0, 0, 0, 0, 0, 0];
        await sender.SendAsync(futureVersion, target, stoppingToken);
        await sender.SendAsync(beacon.Encode(), target, stoppingToken);
        var skipped = 0;
        LanBeacon? found = null;
        while (found is null)
        {
            var datagram = await ReceiveAsync(receiver, stoppingToken);
            if (LanBeacon.TryDecode(datagram.Buffer, out var decoded))
                found = decoded;
            else
                skipped++;
        }

        // Assert
        skipped.ShouldBe(2);
        found.ShouldBe(beacon);
    }

    [Fact]
    public async Task Beacon_LeadsTheListenerToTheMastersStream()
    {
        // Arrange: a master's TCP server and its beacon
        var stoppingToken = TestContext.Current.CancellationToken;
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        const uint session = 0x5E55_1011;
        var beacon = new LanBeacon(session, ((IPEndPoint)server.LocalEndpoint).Port, "Salon");
        var serving = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync(stoppingToken);
            var stream = client.GetStream();
            AuraProtocol.WriteHeader(stream, session);
            return AuraProtocol.ReadHello(stream);
        }, stoppingToken);
        using var receiver = BeaconReceiver();
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var phone = new Hello(Guid.NewGuid(), "Pixel");

        // Act: the listener hears the beacon, connects to where it came from, and joins
        await sender.SendAsync(beacon.Encode(), (IPEndPoint)receiver.Client.LocalEndPoint!, stoppingToken);
        var datagram = await ReceiveAsync(receiver, stoppingToken);
        LanBeacon.TryDecode(datagram.Buffer, out var heard).ShouldBeTrue();
        using var listener = new TcpClient(AddressFamily.InterNetwork);
        await listener.ConnectAsync(datagram.RemoteEndPoint.Address, heard.Port, stoppingToken);
        var stream = listener.GetStream();
        stream.ReadTimeout = (int)ReceiveTimeout.TotalMilliseconds;
        uint joined = AuraProtocol.ReadHeader(stream);
        AuraProtocol.WriteHello(stream, phone);

        // Assert
        joined.ShouldBe(heard.Session);
        (await serving).ShouldBe(phone);
    }
}
