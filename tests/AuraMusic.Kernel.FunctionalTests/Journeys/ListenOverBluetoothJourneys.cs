namespace AuraMusic.Kernel.FunctionalTests.Journeys;

/// <summary>"Share my sound" on one phone, "Listen" on a paired one: the sound gets there over Bluetooth.</summary>
public sealed class ListenOverBluetoothJourneys
{
    const double MusicHz = 880;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PairedPhone_HearsTheMastersMusic(bool listenerHasLibopus)
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        using var beacons = Journey.BeaconPort();
        using var master = new MasterStation("Salon", new InMemoryPreferences(), Journey.PortOf(beacons));
        using var listener = new ListenerStation("Téléphone de Léa", listenerHasLibopus);
        var music = new ToneSource(MusicHz);

        // Act
        var link = await listener.ConnectBluetoothAsync(master, stoppingToken);
        await Journey.EventuallyAsync(() => master.Connected.Length == 1, "the master to take the listener", stoppingToken);
        await Journey.StreamAsync(master, music, listener, frames: 100, stoppingToken);
        var report = listener.Stats.TakeReport();
        var screen = DiagnosticsText.Lines(report).ToDictionary(line => line.Label, line => line.Value);

        // Assert: both screens
        (master.State is Streaming { Listeners: ["Téléphone de Léa"] }).ShouldBeTrue($"{master.State}");
        (listener.State is Listening { Master: "Salon", Links: Links.Bluetooth }).ShouldBeTrue($"{listener.State}");
        // the sound
        Speaker.IsTone(listener.Speaker.Last(50), MusicHz).ShouldBeTrue();
        listener.Steps.ShouldNotContain(nameof(Conceal));
        listener.Steps.ShouldNotContain(nameof(Rebuffer));
        listener.LatencyMs.ShouldBe(PlayoutTuning.BluetoothMinLatencyMs);
        // the diagnostics screen
        screen["links"].ShouldBe("Bluetooth");
        screen["codec"].ShouldBe(listenerHasLibopus ? "libopus" : "managed (fallback)");
        report.BluetoothFramesPerSecond.ShouldBeGreaterThan(0);
        report.WifiFramesPerSecond.ShouldBe(0);
        master.Bitrate.ShouldBe(AuraProtocol.Bitrate);
        link.Ended.IsCompleted.ShouldBeFalse("the link is still up");
    }

    [Fact]
    public async Task FirstBluetoothConnection_TrustsThePhone_ForLaterSessions()
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        var masterPreferences = new InMemoryPreferences();
        using var beacons = Journey.BeaconPort();
        using var listener = new ListenerStation("Pixel");

        // Act: a first broadcast, stopped once the listener is in
        using (var first = new MasterStation("Salon", masterPreferences, Journey.PortOf(beacons)))
        {
            await listener.ConnectBluetoothAsync(first, stoppingToken);
            await Journey.EventuallyAsync(() => first.Connected.Length == 1, "the master to take the listener", stoppingToken);
        }
        var stored = TrustedDevices.Parse(masterPreferences.Get(MasterStation.TrustedKey));

        // Assert
        stored.Contains(listener.Phone.Id).ShouldBeTrue();
        stored.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ListenerStops_TheMasterShowsItIsAloneAgain()
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        using var beacons = Journey.BeaconPort();
        using var master = new MasterStation("Salon", new InMemoryPreferences(), Journey.PortOf(beacons));
        var listener = new ListenerStation("Nord 4");
        await listener.ConnectBluetoothAsync(master, stoppingToken);
        await Journey.EventuallyAsync(() => master.State is Streaming, "the master to stream", stoppingToken);

        // Act
        listener.Dispose();
        await Journey.EventuallyAsync(() => master.State is Advertising, "the master to see the listener go", stoppingToken);

        // Assert
        master.Connected.ShouldBeEmpty();
    }

    [Fact]
    public async Task ThreePhonesOnBluetooth_ShareTheRadio_AtALowerBitrate()
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        using var beacons = Journey.BeaconPort();
        using var master = new MasterStation("Salon", new InMemoryPreferences(), Journey.PortOf(beacons));
        using var lea = new ListenerStation("Léa");
        using var hugo = new ListenerStation("Hugo");
        using var zoe = new ListenerStation("Zoé");
        var music = new ToneSource(MusicHz);

        // Act: two phones, then a third
        await lea.ConnectBluetoothAsync(master, stoppingToken);
        await hugo.ConnectBluetoothAsync(master, stoppingToken);
        await Journey.EventuallyAsync(() => master.Connected.Length == 2, "two listeners", stoppingToken);
        master.Broadcast(music.Next());
        int twoPhones = master.Bitrate;
        await zoe.ConnectBluetoothAsync(master, stoppingToken);
        await Journey.EventuallyAsync(() => master.Connected.Length == 3, "three listeners", stoppingToken);
        for (int i = 0; i < 20; i++)
            master.Broadcast(music.Next());
        foreach (var listener in new[] { lea, hugo, zoe })
            await Journey.EventuallyAsync(() => listener.Accepted.Count >= (listener == zoe ? 20 : 21), $"the packets at {listener.Phone.Name}", stoppingToken);

        // Assert
        twoPhones.ShouldBe(AuraProtocol.Bitrate);
        master.Bitrate.ShouldBe(AuraProtocol.BluetoothBitrate(3));
        master.Bitrate.ShouldBeLessThan(AuraProtocol.Bitrate);
        master.LastPlan.BluetoothCarriers.ShouldBe(3);
        (master.State is Streaming { Count: 3 }).ShouldBeTrue();
    }
}
