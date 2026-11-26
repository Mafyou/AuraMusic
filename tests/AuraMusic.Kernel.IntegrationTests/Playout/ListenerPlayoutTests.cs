namespace AuraMusic.Kernel.IntegrationTests.Playout;

/// <summary>
/// The listener's playback chain with its real parts wired as the listen service wires them: the playout and its
/// latency target and drift controller, the real Opus decoder, and <see cref="ListenerStats"/> counting what happens,
/// down to the lines of the diagnostics screen.
/// </summary>
public sealed class ListenerPlayoutTests : IDisposable
{
    const int FrameLength = AuraProtocol.FrameSamples * AuraProtocol.Channels;

    readonly ListenerStats stats = new(TimeProvider.System);
    readonly PlayoutController playout;
    readonly DriftController drift = new(AuraProtocol.SampleRate);
    readonly LatencyTarget target;
    readonly IAudioEncoder encoder = OpusCodec.CreateEncoder(AuraProtocol.SampleRate, AuraProtocol.Channels, AuraProtocol.Bitrate);
    readonly IAudioDecoder decoder = OpusCodec.CreateDecoder(AuraProtocol.SampleRate, AuraProtocol.Channels);
    readonly float[] pcm = new float[FrameLength];
    int encoded;

    public ListenerPlayoutTests()
    {
        playout = new PlayoutController(stats);
        target = new LatencyTarget(playout, drift);
    }

    OpusPacket NextPacket()
    {
        var packet = new byte[AuraProtocol.MaxPacketSize];
        int length = encoder.Encode(Signal.Tone(440, encoded++), AuraProtocol.FrameSamples, packet);
        return new OpusPacket(packet, length);
    }

    /// <summary>One turn of the playback loop.</summary>
    /// <returns>How many interleaved samples went to the speaker.</returns>
    int Turn(OpusPacket? packet, int backlog)
    {
        switch (playout.Next(packet, backlog))
        {
            case Play(var data):
                decoder.Decode(data.Span, pcm, AuraProtocol.FrameSamples);
                return FrameLength;
            case CatchUp(var data):
                decoder.Decode(data.Span, pcm, AuraProtocol.FrameSamples);
                return PcmCrossfade.Shorten(pcm, AuraProtocol.Channels, AuraProtocol.SampleRate / 1000, 2 * AuraProtocol.SampleRate / 1000);
            case Conceal:
                decoder.Decode([], pcm, AuraProtocol.FrameSamples);
                drift.RanDry();
                return FrameLength;
            case Skip(var data):
                decoder.Decode(data.Span, pcm, AuraProtocol.FrameSamples);
                return 0;
            default:
                return 0; // rebuffer: silent until the cushion is back
        }
    }

    [Fact]
    public void BumpyReception_IsCountedAndShownOnTheDiagnosticsScreen()
    {
        // Arrange: Wi-Fi at the default latency
        stats.Links = Links.Wifi;
        stats.NativeCodec = decoder.IsNative;
        target.Retarget(PlayoutTuning.LatencyFor(Links.Wifi));
        int cushion = playout.PrebufferFrames;

        // Act: steady play, a short gap, a stall, a burst, then a flood
        var played = new List<int>();
        for (int i = 0; i < 20; i++)
        {
            stats.Received(Links.Wifi, 200);
            played.Add(Turn(NextPacket(), cushion));
        }
        for (int i = 0; i < 3; i++)
            played.Add(Turn(null, 0)); // concealed
        played.Add(Turn(NextPacket(), cushion));
        for (int i = 0; i < 6; i++)
            played.Add(Turn(null, 0)); // four concealed, a rebuffer, one more concealed
        played.Add(Turn(NextPacket(), playout.CatchUpAboveFrames + 5)); // the stalled packets pour in: caught up
        played.Add(Turn(NextPacket(), playout.MaxBacklogFrames + 1)); // far behind: skipped
        stats.Playing(180, target.ReachableMs, drift.Correction * 1e6, 0);
        var report = stats.TakeReport();
        var lines = DiagnosticsText.Lines(report).ToDictionary(line => line.Label, line => line.Value);

        // Assert
        (report.CaughtUp, report.Concealed, report.Skipped, report.Rebuffers).ShouldBe((1, 8, 1, 1));
        report.WifiFramesPerSecond.ShouldBeGreaterThan(0);
        report.BluetoothFramesPerSecond.ShouldBe(0);
        played[^2].ShouldBe(FrameLength - AuraProtocol.SampleRate / 1000 * AuraProtocol.Channels); // 1 ms shorter
        played[^1].ShouldBe(0);
        lines["links"].ShouldBe("Wi-Fi");
        lines["codec"].ShouldBe("libopus");
        lines["buffer"].ShouldBe($"180 / {PlayoutTuning.DefaultLatencyMs} ms");
        lines["concealed"].ShouldBe("8");
        lines["rebuffers"].ShouldBe("1");
    }

    [Fact]
    public void WifiLost_RaisesTheLatency_AndAsksForARefill()
    {
        // Arrange: listening over Wi-Fi and Bluetooth
        bool firstRefill = target.Retarget(PlayoutTuning.LatencyFor(Links.Wifi | Links.Bluetooth));
        int wifiCushion = playout.PrebufferFrames;

        // Act: Wi-Fi goes, Bluetooth alone must ride out its stalls
        bool refill = target.Retarget(PlayoutTuning.LatencyFor(Links.Bluetooth));

        // Assert
        firstRefill.ShouldBeTrue();
        refill.ShouldBeTrue();
        target.LatencyMs.ShouldBe(PlayoutTuning.BluetoothMinLatencyMs);
        playout.PrebufferFrames.ShouldBe(PlayoutTuning.CushionFrames(PlayoutTuning.BluetoothMinLatencyMs));
        playout.PrebufferFrames.ShouldBeGreaterThan(wifiCushion);
        playout.CatchUpAboveFrames.ShouldBe(playout.PrebufferFrames + PlayoutTuning.CatchUpMarginFrames);
        drift.TargetMs.ShouldBe(PlayoutTuning.BluetoothMinLatencyMs);
    }

    [Fact]
    public void StallsLearntByAdaptiveLatency_AreKeptInHandNextTime()
    {
        // Arrange
        var adaptive = new AdaptiveLatency();
        target.Retarget(PlayoutTuning.LatencyFor(Links.Wifi));

        // Act: playback stayed dry for 150 ms, the target is raised by what that took
        adaptive.Stalled(TimeSpan.FromMilliseconds(150));
        target.Retarget(PlayoutTuning.LatencyFor(Links.Wifi) + adaptive.ExtraMs);

        // Assert
        adaptive.ExtraMs.ShouldBe(200); // 150 ms + the 40 ms margin, in whole packets
        target.LatencyMs.ShouldBe(PlayoutTuning.DefaultLatencyMs + 200);
        playout.PrebufferFrames.ShouldBe(PlayoutTuning.CushionFrames(PlayoutTuning.DefaultLatencyMs + 200));
    }

    [Fact]
    public void BufferAboveItsTarget_IsPlayedOffFaster_WithinTheLimit()
    {
        // Arrange
        target.Retarget(PlayoutTuning.LatencyFor(Links.Wifi));

        // Act: the listener's clock is slower, the buffer sits 100 ms too high for a while
        int rate = 0;
        for (int i = 0; i < 500; i++)
            rate = drift.Update(target.ReachableMs + 100);

        // Assert
        rate.ShouldBeGreaterThan(AuraProtocol.SampleRate);
        rate.ShouldBeLessThanOrEqualTo((int)Math.Round(AuraProtocol.SampleRate * (1 + DriftController.MaxCorrection)));
    }

    public void Dispose()
    {
        encoder.Dispose();
        decoder.Dispose();
    }
}
