namespace AuraMusic.Kernel.FunctionalTests.Journeys;

/// <summary>Both phones on the same Wi-Fi: the master's beacon is found, Wi-Fi takes over, Bluetooth stands by.</summary>
public sealed class WifiJourneys
{
    const double MusicHz = 660;

    [Fact]
    public async Task UnpairedPhone_IsTurnedAwayOverWifi()
    {
        // Arrange: a phone on the same network that never connected over Bluetooth
        var stoppingToken = TestContext.Current.CancellationToken;
        using var beacons = Journey.BeaconPort();
        using var master = new MasterStation("Salon", new InMemoryPreferences(), Journey.PortOf(beacons));
        using var stranger = new ListenerStation("Inconnu");

        // Act
        var following = stranger.FollowBeaconAsync(beacons, stoppingToken);
        await master.AnnounceAsync(stoppingToken);
        var link = await following;
        bool carried = await link.Ended;

        // Assert
        carried.ShouldBeFalse();
        master.Refused.ShouldBe(1);
        (master.State is Advertising).ShouldBeTrue();
        (stranger.State is Searching).ShouldBeTrue();
        stranger.Accepted.ShouldBeEmpty();
    }

    [Fact]
    public async Task PairedPhone_MovesToWifi_BluetoothIdles_AndTheBitrateGoesUp()
    {
        // Arrange: listening over Bluetooth
        var stoppingToken = TestContext.Current.CancellationToken;
        using var beacons = Journey.BeaconPort();
        using var master = new MasterStation("Salon", new InMemoryPreferences(), Journey.PortOf(beacons)) { HealthyAfterFrames = 20 };
        using var listener = new ListenerStation("Pixel");
        var music = new ToneSource(MusicHz);
        await listener.ConnectBluetoothAsync(master, stoppingToken);
        await Journey.EventuallyAsync(() => master.Connected.Length == 1, "the Bluetooth link", stoppingToken);
        await Journey.StreamAsync(master, music, listener, frames: 10, stoppingToken);

        // Act: the beacon is heard, the phone joins over Wi-Fi too, and Wi-Fi keeps up
        var following = listener.FollowBeaconAsync(beacons, stoppingToken);
        await master.AnnounceAsync(stoppingToken);
        await following;
        await Journey.EventuallyAsync(() => master.Connected.Length == 2, "the Wi-Fi link", stoppingToken);
        await Journey.StreamAsync(master, music, listener, frames: 30, stoppingToken);
        listener.Stats.TakeReport(); // start a fresh window once Wi-Fi is healthy
        await Journey.StreamAsync(master, music, listener, frames: 40, stoppingToken);
        var report = listener.Stats.TakeReport();

        // Assert: one phone on the master's screen, both links on the listener's
        (master.State is Streaming { Listeners: ["Pixel"] }).ShouldBeTrue($"{master.State}");
        (listener.State is Listening { Links: Links.Wifi | Links.Bluetooth }).ShouldBeTrue($"{listener.State}");
        // Bluetooth idles behind Wi-Fi, which gets the better sound
        master.LastPlan.AllOnWifi.ShouldBeTrue();
        master.LastPlan.BluetoothCarriers.ShouldBe(0);
        master.Bitrate.ShouldBe(AuraProtocol.WifiBitrate);
        report.BluetoothFramesPerSecond.ShouldBe(0);
        report.WifiFramesPerSecond.ShouldBeGreaterThan(0);
        // every packet once, none lost, none played twice
        listener.Accepted.ShouldBe([.. Enumerable.Range(0, master.Sent).Select(i => (uint)i)]);
        listener.LatencyMs.ShouldBe(PlayoutTuning.TargetLatencyMs);
        Speaker.IsTone(listener.Speaker.Last(30), MusicHz).ShouldBeTrue();
    }

    [Fact]
    public async Task WifiLost_BluetoothTakesOver_WithoutLosingTheStream()
    {
        // Arrange: listening over Wi-Fi with Bluetooth idle
        var stoppingToken = TestContext.Current.CancellationToken;
        using var beacons = Journey.BeaconPort();
        using var master = new MasterStation("Salon", new InMemoryPreferences(), Journey.PortOf(beacons)) { HealthyAfterFrames = 10 };
        using var listener = new ListenerStation("Pixel");
        var music = new ToneSource(MusicHz);
        await listener.ConnectBluetoothAsync(master, stoppingToken);
        await Journey.EventuallyAsync(() => master.Connected.Length == 1, "the Bluetooth link", stoppingToken);
        var following = listener.FollowBeaconAsync(beacons, stoppingToken);
        await master.AnnounceAsync(stoppingToken);
        var wifi = await following;
        await Journey.EventuallyAsync(() => master.Connected.Length == 2, "the Wi-Fi link", stoppingToken);
        await Journey.StreamAsync(master, music, listener, frames: 30, stoppingToken);
        int refillsOnWifi = listener.Refills;

        // Act: the phone leaves the Wi-Fi
        wifi.Close();
        await Journey.EventuallyAsync(() => master.Connected.Length == 1, "the master to drop the Wi-Fi link", stoppingToken);
        await Journey.EventuallyAsync(() => listener.State is Listening { Links: Links.Bluetooth }, "the listener on Bluetooth alone", stoppingToken);
        int sentBeforeTakeOver = master.Sent;
        await Journey.StreamAsync(master, music, listener, frames: 50, stoppingToken);

        // Assert: Bluetooth carries again, at its own bitrate and latency
        master.LastPlan.AllOnWifi.ShouldBeFalse();
        master.LastPlan.BluetoothCarriers.ShouldBe(1);
        master.Bitrate.ShouldBe(AuraProtocol.Bitrate);
        listener.LatencyMs.ShouldBe(PlayoutTuning.BluetoothMinLatencyMs);
        listener.Refills.ShouldBeGreaterThan(refillsOnWifi);
        // everything sent once the master knew is there, in order
        listener.Accepted.Where(sequence => sequence >= sentBeforeTakeOver)
            .ShouldBe([.. Enumerable.Range(sentBeforeTakeOver, master.Sent - sentBeforeTakeOver).Select(i => (uint)i)]);
        Speaker.IsTone(listener.Speaker.Last(40), MusicHz).ShouldBeTrue();
        listener.Steps.ShouldNotContain(nameof(Rebuffer));
    }
}
