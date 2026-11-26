namespace AuraMusic.Kernel.IntegrationTests.Multipath;

/// <summary>
/// A listener on Bluetooth and Wi-Fi at once: two real sockets, each read on its own thread as the listen service
/// does, both feeding one <see cref="SequenceGate"/>.
/// </summary>
public sealed class DualLinkMergeTests
{
    const int Packets = 300;

    /// <summary>Reads one link until the master hangs up, letting through what the gate accepts.</summary>
    static Task<int> ReadLinkAsync(Stream stream, SequenceGate gate, ConcurrentBag<uint> accepted, CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() =>
        {
            uint session = AuraProtocol.ReadHeader(stream);
            if (!gate.Join(session))
                return 0;
            var buffer = new byte[AuraProtocol.MaxPacketSize];
            int received = 0;
            try
            {
                while (true)
                {
                    int length = AuraProtocol.ReadFrame(stream, buffer, out uint sequence);
                    if (length == 0)
                        continue; // keep-alive
                    received++;
                    if (gate.TryAccept(session, sequence))
                        accepted.Add(sequence);
                }
            }
            catch (EndOfStreamException)
            {
                return received; // the master hung up
            }
        }, stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>
    /// Sends the numbered stream on both links from one loop, as the master's encoder hands each packet to every link
    /// that carries it, with small random pauses so the two readers overtake each other.
    /// </summary>
    /// <param name="beforePacket">Called before each packet is sent, with its number.</param>
    static Task SendAsync(uint session, int seed, Action<int>? beforePacket, CancellationToken stoppingToken,
        params (LoopbackLink Link, Func<int, bool> Carries)[] links) =>
        Task.Run(() =>
        {
            var random = new Random(seed);
            foreach (var (link, _) in links)
                AuraProtocol.WriteHeader(link.Master, session);
            for (int i = 0; i < Packets; i++)
            {
                beforePacket?.Invoke(i);
                foreach (var (link, carries) in links)
                {
                    if (carries(i))
                        AuraProtocol.WriteFrame(link.Master, (uint)i, [(byte)i, (byte)(i >> 8)]);
                    else if (i % 50 == 0)
                        AuraProtocol.WriteKeepAlive(link.Master); // an idle link only hears that it is still up
                }
                if (random.Next(20) == 0)
                    Thread.Sleep(random.Next(1, 4));
            }
            foreach (var (link, _) in links)
                link.CloseMaster();
        }, stoppingToken);

    [Fact]
    public async Task SameStreamOnBothLinks_EveryPacketGoesThroughExactlyOnce()
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        using var bluetooth = await LoopbackLink.OpenAsync(stoppingToken);
        using var wifi = await LoopbackLink.OpenAsync(stoppingToken);
        var gate = new SequenceGate();
        var accepted = new ConcurrentBag<uint>();
        const uint session = 42;

        // Act
        var readers = new[] { ReadLinkAsync(bluetooth.Listener, gate, accepted, stoppingToken), ReadLinkAsync(wifi.Listener, gate, accepted, stoppingToken) };
        await SendAsync(session, 1, null, stoppingToken, (bluetooth, _ => true), (wifi, _ => true));
        var received = await Task.WhenAll(readers);

        // Assert
        received.ShouldBe([Packets, Packets]);
        accepted.Order().ShouldBe([.. Enumerable.Range(0, Packets).Select(i => (uint)i)]);
    }

    [Fact]
    public async Task BluetoothIdlingBehindWifi_ThenTakingOver_LosesNothing()
    {
        // Arrange: Wi-Fi carries the first two thirds; Bluetooth only gets keep-alives, then carries from there on
        var stoppingToken = TestContext.Current.CancellationToken;
        using var bluetooth = await LoopbackLink.OpenAsync(stoppingToken);
        using var wifi = await LoopbackLink.OpenAsync(stoppingToken);
        var gate = new SequenceGate();
        var accepted = new ConcurrentBag<uint>();
        const uint session = 7;
        const int wifiLost = Packets * 2 / 3;

        // Act
        var readers = new[] { ReadLinkAsync(bluetooth.Listener, gate, accepted, stoppingToken), ReadLinkAsync(wifi.Listener, gate, accepted, stoppingToken) };
        // The handover comes once Wi-Fi delivered what it was given: a link that falls behind by more than the overlap
        // loses those packets, as it would on the phones.
        void HandOver(int packet) => SpinWait.SpinUntil(() => packet != wifiLost - 5 || accepted.Count >= packet, TimeSpan.FromSeconds(10));
        await SendAsync(session, 3, HandOver, stoppingToken,
            (bluetooth, i => i >= wifiLost - 5), // a little overlap, as the router reacts
            (wifi, i => i < wifiLost));
        var received = await Task.WhenAll(readers);

        // Assert
        received.ShouldBe([Packets - wifiLost + 5, wifiLost]);
        accepted.Order().ShouldBe([.. Enumerable.Range(0, Packets).Select(i => (uint)i)]);
    }

    [Fact]
    public async Task SecondLinkFromAnotherMaster_GivesUp_AndIsNeverMixedIn()
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        using var ours = await LoopbackLink.OpenAsync(stoppingToken);
        using var theirs = await LoopbackLink.OpenAsync(stoppingToken);
        var gate = new SequenceGate();
        var accepted = new ConcurrentBag<uint>();
        AuraProtocol.WriteHeader(ours.Master, 1);
        var reader = ReadLinkAsync(ours.Listener, gate, accepted, stoppingToken);
        await WaitUntilAsync(() => gate.Session is not null, stoppingToken);

        // Act
        var intruder = ReadLinkAsync(theirs.Listener, gate, accepted, stoppingToken);
        AuraProtocol.WriteHeader(theirs.Master, 2);
        int fromTheirs = await intruder;
        for (int i = 0; i < 10; i++)
            AuraProtocol.WriteFrame(ours.Master, (uint)i, [1]);
        ours.CloseMaster();
        int fromOurs = await reader;

        // Assert
        fromTheirs.ShouldBe(0);
        fromOurs.ShouldBe(10);
        gate.Session.ShouldBe(1u);
        accepted.Count.ShouldBe(10);
    }

    static async Task WaitUntilAsync(Func<bool> condition, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }
}
