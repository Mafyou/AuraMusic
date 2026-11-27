namespace AuraMusic.Kernel.IntegrationTests.Codec;

/// <summary>The real encoder, the wire format over a real socket and the real decoder, as master and listener chain them.</summary>
public sealed class OpusOverSocketTests
{
    const int Frames = 50; // a second of sound
    const double ToneHz = 1_000;
    const double OtherHz = 3_100;

    static IAudioEncoder Encoder(bool native, int bitrate = AuraProtocol.Bitrate) => native
        ? OpusCodec.CreateEncoder(AuraProtocol.SampleRate, AuraProtocol.Channels, bitrate)
        : OpusCodec.CreateManagedEncoder(AuraProtocol.SampleRate, AuraProtocol.Channels, bitrate);

    static IAudioDecoder Decoder(bool native) => native
        ? OpusCodec.CreateDecoder(AuraProtocol.SampleRate, AuraProtocol.Channels)
        : OpusCodec.CreateManagedDecoder(AuraProtocol.SampleRate, AuraProtocol.Channels);

    /// <summary>Encodes <see cref="Frames"/> frames of the tone and sends them; <paramref name="beforeFrame"/> may change the encoder.</summary>
    static Task SendToneAsync(IAudioEncoder encoder, Stream stream, Action<IAudioEncoder, int>? beforeFrame, CancellationToken stoppingToken) =>
        Task.Run(() =>
        {
            var packet = new byte[AuraProtocol.MaxPacketSize];
            for (int frame = 0; frame < Frames; frame++)
            {
                beforeFrame?.Invoke(encoder, frame);
                int length = encoder.Encode(Signal.Tone(ToneHz, frame), AuraProtocol.FrameSamples, packet);
                AuraProtocol.WriteFrame(stream, (uint)frame, packet.AsSpan(0, length));
            }
        }, stoppingToken);

    /// <summary>Reads and decodes every frame; the ones in <paramref name="lost"/> are dropped as a radio would.</summary>
    static float[] ReceiveTone(IAudioDecoder decoder, Stream stream, ISet<int>? lost = null)
    {
        var buffer = new byte[AuraProtocol.MaxPacketSize];
        var pcm = new float[AuraProtocol.FrameSamples * AuraProtocol.Channels];
        var heard = new List<float>();
        for (int frame = 0; frame < Frames; frame++)
        {
            int length = AuraProtocol.ReadFrame(stream, buffer, out _);
            decoder.Decode(lost?.Contains(frame) == true ? [] : buffer.AsSpan(0, length), pcm, AuraProtocol.FrameSamples);
            if (frame >= Frames / 2) // once the codec has settled
                heard.AddRange(pcm);
        }
        return [.. heard];
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Tone_SentOverTheLink_IsHeardAtItsPitch(bool nativeMaster, bool nativeListener)
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        using var link = await LoopbackLink.OpenAsync(stoppingToken);
        using var encoder = Encoder(nativeMaster);
        using var decoder = Decoder(nativeListener);

        // Act
        var sending = SendToneAsync(encoder, link.Master, null, stoppingToken);
        var heard = ReceiveTone(decoder, link.Listener);
        await sending;

        // Assert
        Signal.Rms(heard).ShouldBe(16_000 / 32_768.0 * Math.Sqrt(0.5), tolerance: 0.05);
        Signal.PowerAt(heard, ToneHz).ShouldBeGreaterThan(Signal.PowerAt(heard, OtherHz) * 1_000);
    }

    [Fact]
    public async Task BitrateRaisedMidStream_KeepsTheSoundGoing()
    {
        // Arrange: every listener moved to Wi-Fi, the master goes from 96 to 160 kbps between two packets
        var stoppingToken = TestContext.Current.CancellationToken;
        using var link = await LoopbackLink.OpenAsync(stoppingToken);
        using var encoder = Encoder(native: true);
        using var decoder = Decoder(native: true);

        // Act
        var sending = SendToneAsync(encoder, link.Master, (encoder, frame) =>
        {
            if (frame == Frames / 3)
                encoder.Bitrate = AuraProtocol.WifiBitrate;
        }, stoppingToken);
        var heard = ReceiveTone(decoder, link.Listener);
        await sending;

        // Assert
        encoder.Bitrate.ShouldBe(AuraProtocol.WifiBitrate);
        Signal.PowerAt(heard, ToneHz).ShouldBeGreaterThan(Signal.PowerAt(heard, OtherHz) * 1_000);
    }

    [Fact]
    public async Task PacketsLostOnTheWay_AreConcealed_NotHeardAsSilence()
    {
        // Arrange: a few packets go missing, as when a link stalls for 60 ms
        var stoppingToken = TestContext.Current.CancellationToken;
        using var link = await LoopbackLink.OpenAsync(stoppingToken);
        using var encoder = Encoder(native: true);
        using var decoder = Decoder(native: true);
        HashSet<int> lost = [30, 31, 32];

        // Act
        var sending = SendToneAsync(encoder, link.Master, null, stoppingToken);
        var heard = ReceiveTone(decoder, link.Listener, lost);
        await sending;

        // Assert: the frames that stood in for the lost ones still carry sound at the tone's pitch
        int frameLength = AuraProtocol.FrameSamples * AuraProtocol.Channels;
        var concealed = heard.AsSpan((30 - Frames / 2) * frameLength, 3 * frameLength);
        Signal.Rms(concealed).ShouldBeGreaterThan(0.01);
        Signal.PowerAt(heard, ToneHz).ShouldBeGreaterThan(Signal.PowerAt(heard, OtherHz) * 100);
    }
}
