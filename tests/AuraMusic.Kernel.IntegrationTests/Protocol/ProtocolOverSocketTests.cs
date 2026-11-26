namespace AuraMusic.Kernel.IntegrationTests.Protocol;

/// <summary>The wire format between a master and a listener, both ends on a real socket.</summary>
public sealed class ProtocolOverSocketTests
{
    [Fact]
    public async Task Handshake_GivesTheListenerTheSession_AndTheMasterThePhone()
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        using var link = await LoopbackLink.OpenAsync(stoppingToken);
        var phone = new Hello(Guid.NewGuid(), "Téléphone de Zoé");

        // Act
        var listening = Task.Run(() =>
        {
            uint session = AuraProtocol.ReadHeader(link.Listener);
            AuraProtocol.WriteHello(link.Listener, phone);
            return session;
        }, stoppingToken);
        AuraProtocol.WriteHeader(link.Master, 0xA0CA_5701);
        var hello = AuraProtocol.ReadHello(link.Master);

        // Assert
        (await listening).ShouldBe(0xA0CA_5701u);
        hello.ShouldBe(phone);
    }

    [Fact]
    public async Task BatchedWrite_IsReadBackFrameByFrame()
    {
        // Arrange: the master's link sends whatever has queued up as a single write
        var stoppingToken = TestContext.Current.CancellationToken;
        using var link = await LoopbackLink.OpenAsync(stoppingToken);
        byte[][] packets = [.. Enumerable.Range(1, 10).Select(size => Enumerable.Repeat((byte)size, size * 37).ToArray())];
        var batch = new MemoryStream();
        for (int i = 0; i < packets.Length; i++)
            AuraProtocol.WriteFrame(batch, (uint)(100 + i), packets[i]);

        // Act
        link.Master.Write(batch.GetBuffer(), 0, (int)batch.Length);
        var buffer = new byte[AuraProtocol.MaxPacketSize];
        var received = new List<(uint Sequence, byte[] Packet)>();
        for (int i = 0; i < packets.Length; i++)
        {
            int length = AuraProtocol.ReadFrame(link.Listener, buffer, out uint sequence);
            received.Add((sequence, buffer[..length]));
        }

        // Assert
        received.Select(frame => frame.Sequence).ShouldBe([.. Enumerable.Range(100, 10).Select(i => (uint)i)]);
        for (int i = 0; i < packets.Length; i++)
            received[i].Packet.ShouldBe(packets[i]);
    }

    [Fact]
    public async Task LongStream_WrittenWhileRead_ArrivesIntactAndInOrder()
    {
        // Arrange: ten seconds of packets of every size the encoder may produce
        var stoppingToken = TestContext.Current.CancellationToken;
        using var link = await LoopbackLink.OpenAsync(stoppingToken);
        var random = new Random(11);
        byte[][] packets = [.. Enumerable.Range(0, 500).Select(_ =>
        {
            var packet = new byte[random.Next(1, AuraProtocol.MaxPacketSize + 1)];
            random.NextBytes(packet);
            return packet;
        })];

        // Act
        var sending = Task.Run(() =>
        {
            for (int i = 0; i < packets.Length; i++)
                AuraProtocol.WriteFrame(link.Master, (uint)i, packets[i]);
        }, stoppingToken);
        var buffer = new byte[AuraProtocol.MaxPacketSize];
        int intact = 0;
        for (int i = 0; i < packets.Length; i++)
        {
            int length = AuraProtocol.ReadFrame(link.Listener, buffer, out uint sequence);
            if (sequence == i && buffer.AsSpan(0, length).SequenceEqual(packets[i]))
                intact++;
        }
        await sending;

        // Assert
        intact.ShouldBe(packets.Length);
    }

    [Fact]
    public async Task KeepAlive_BetweenPackets_ReadsAsAnEmptyFrame()
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        using var link = await LoopbackLink.OpenAsync(stoppingToken);
        var buffer = new byte[AuraProtocol.MaxPacketSize];

        // Act
        AuraProtocol.WriteFrame(link.Master, 1, [1, 2, 3]);
        AuraProtocol.WriteKeepAlive(link.Master);
        AuraProtocol.WriteFrame(link.Master, 2, [4]);
        int first = AuraProtocol.ReadFrame(link.Listener, buffer, out uint firstSequence);
        int keepAlive = AuraProtocol.ReadFrame(link.Listener, buffer, out _);
        int second = AuraProtocol.ReadFrame(link.Listener, buffer, out uint secondSequence);

        // Assert
        (first, firstSequence).ShouldBe((3, 1u));
        keepAlive.ShouldBe(0);
        (second, secondSequence).ShouldBe((1, 2u));
    }

    [Fact]
    public async Task ListenerAlive_IsHeardUntilTheListenerHangsUp()
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        using var link = await LoopbackLink.OpenAsync(stoppingToken);

        // Act
        for (int i = 0; i < 3; i++)
            AuraProtocol.WriteListenerAlive(link.Listener);
        link.CloseListener();
        var heard = new List<bool>();
        for (int i = 0; i < 4; i++)
            heard.Add(AuraProtocol.ReadListenerMessage(link.Master));

        // Assert
        heard.ShouldBe([true, true, true, false]);
    }

    [Fact]
    public async Task MasterHangingUpInTheMiddleOfAFrame_FailsTheRead()
    {
        // Arrange: the length says 100 bytes, only 10 come before the link drops
        var stoppingToken = TestContext.Current.CancellationToken;
        using var link = await LoopbackLink.OpenAsync(stoppingToken);
        var prefix = new byte[AuraProtocol.FramePrefixSize + 10];
        prefix[0] = 100;

        // Act
        link.Master.Write(prefix);
        link.CloseMaster();

        // Assert
        Should.Throw<EndOfStreamException>(() => AuraProtocol.ReadFrame(link.Listener, new byte[AuraProtocol.MaxPacketSize], out _));
    }

    [Fact]
    public async Task SomethingElseOnThePort_IsNotTakenForAMaster()
    {
        // Arrange: another service answers on the address a stale beacon pointed to
        var stoppingToken = TestContext.Current.CancellationToken;
        using var link = await LoopbackLink.OpenAsync(stoppingToken);

        // Act
        link.Master.Write("HTTP/1.1 400 Bad Request\r\n\r\n"u8);

        // Assert
        Should.Throw<InvalidDataException>(() => AuraProtocol.ReadHeader(link.Listener));
    }

    [Fact]
    public async Task HelloWithAnOversizedName_IsRefused()
    {
        // Arrange: a peer that does not truncate its name as AuraMusic does
        var stoppingToken = TestContext.Current.CancellationToken;
        using var link = await LoopbackLink.OpenAsync(stoppingToken);
        var hello = new byte[17];
        hello[16] = AuraProtocol.MaxNameBytes + 1;

        // Act
        link.Listener.Write(hello);

        // Assert
        Should.Throw<InvalidDataException>(() => AuraProtocol.ReadHello(link.Master));
    }
}
