namespace AuraMusic.Kernel.FunctionalTests.Journeys;

/// <summary>The music pauses, the master goes quiet or away, another master is around.</summary>
public sealed class InterruptionJourneys
{
    const double MusicHz = 523;

    [Fact]
    public async Task PausedMusic_KeepsTheLinkUp_AndTheSoundComesBack()
    {
        // Arrange: a listener that drops a master silent for a second
        var stoppingToken = TestContext.Current.CancellationToken;
        using var beacons = Journey.BeaconPort();
        using var master = new MasterStation("Salon", new InMemoryPreferences(), Journey.PortOf(beacons));
        using var listener = new ListenerStation("Pixel") { LinkTimeout = TimeSpan.FromSeconds(1) };
        var music = new ToneSource(MusicHz);
        await listener.ConnectBluetoothAsync(master, stoppingToken);
        await Journey.EventuallyAsync(() => master.Connected.Length == 1, "the Bluetooth link", stoppingToken);
        await Journey.StreamAsync(master, music, listener, frames: 20, stoppingToken);

        // Act: paused for two and a half seconds, keep-alives only; the listener plays out what it had, then waits
        var pausedStates = new List<AuraState>();
        for (int tick = 0; tick < 10; tick++)
        {
            master.KeepAlive();
            listener.Play(5);
            pausedStates.Add(listener.State);
            await Task.Delay(250, stoppingToken);
        }
        var stepsWhilePaused = listener.Steps.Count;
        await Journey.StreamAsync(master, music, listener, frames: 30, stoppingToken);

        // Assert: still connected all along, silent once the buffer ran out, the music back afterwards
        pausedStates.All(state => state is Listening).ShouldBeTrue();
        listener.Steps.Take(stepsWhilePaused).ShouldContain(nameof(Rebuffer));
        listener.Stats.TakeReport().Rebuffers.ShouldBeGreaterThan(0);
        Speaker.IsTone(listener.Speaker.Last(20), MusicHz).ShouldBeTrue();
        master.Connected.Length.ShouldBe(1);
    }

    [Fact]
    public async Task MasterGoneSilent_IsDropped_AndTheListenerSearchesAgain()
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        using var beacons = Journey.BeaconPort();
        using var master = new MasterStation("Salon", new InMemoryPreferences(), Journey.PortOf(beacons));
        using var listener = new ListenerStation("Pixel") { LinkTimeout = TimeSpan.FromSeconds(1) };
        var link = await listener.ConnectBluetoothAsync(master, stoppingToken);
        await Journey.EventuallyAsync(() => master.Connected.Length == 1, "the Bluetooth link", stoppingToken);
        await Journey.StreamAsync(master, new ToneSource(MusicHz), listener, frames: 5, stoppingToken);

        // Act: the master's phone freezes, its socket still open but nothing on it
        bool carried = await link.Ended.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken);
        await Journey.EventuallyAsync(() => master.State is Advertising, "the master to see the listener hang up", stoppingToken);

        // Assert
        carried.ShouldBeTrue();
        (listener.State is Searching).ShouldBeTrue($"{listener.State}");
    }

    [Fact]
    public async Task ListenerFollowingOneMaster_IgnoresTheBeaconOfAnother()
    {
        // Arrange: two masters on the same network; the listener follows "Salon" over Bluetooth
        var stoppingToken = TestContext.Current.CancellationToken;
        using var beacons = Journey.BeaconPort();
        using var salon = new MasterStation("Salon", new InMemoryPreferences(), Journey.PortOf(beacons));
        using var cuisine = new MasterStation("Cuisine", new InMemoryPreferences(), Journey.PortOf(beacons));
        using var listener = new ListenerStation("Pixel");
        await listener.ConnectBluetoothAsync(salon, stoppingToken);
        await Journey.EventuallyAsync(() => salon.Connected.Length == 1, "the Bluetooth link", stoppingToken);
        await Journey.StreamAsync(salon, new ToneSource(MusicHz), listener, frames: 1, stoppingToken);

        // Act: the other master's beacon comes first
        var following = listener.FollowBeaconAsync(beacons, stoppingToken);
        await cuisine.AnnounceAsync(stoppingToken);
        await Journey.EventuallyAsync(() => listener.IgnoredBeacons == 1, "the other beacon to be heard", stoppingToken);
        await salon.AnnounceAsync(stoppingToken);
        await following;
        await Journey.EventuallyAsync(() => salon.Connected.Length == 2, "the Wi-Fi link to the followed master", stoppingToken);

        // Assert
        cuisine.Connected.ShouldBeEmpty();
        (cuisine.State is Advertising).ShouldBeTrue();
        salon.Connected.Select(link => link.Kind).Order().ShouldBe([Links.Bluetooth, Links.Wifi]);
    }

    [Fact]
    public async Task SecondLinkReachingAnotherMaster_GivesUp_WithoutMixingTheSound()
    {
        // Arrange
        var stoppingToken = TestContext.Current.CancellationToken;
        using var beacons = Journey.BeaconPort();
        using var salon = new MasterStation("Salon", new InMemoryPreferences(), Journey.PortOf(beacons));
        using var cuisine = new MasterStation("Cuisine", new InMemoryPreferences(), Journey.PortOf(beacons));
        using var listener = new ListenerStation("Pixel");
        var music = new ToneSource(MusicHz);
        await listener.ConnectBluetoothAsync(salon, stoppingToken);
        await Journey.EventuallyAsync(() => salon.Connected.Length == 1, "the Bluetooth link", stoppingToken);
        await Journey.StreamAsync(salon, music, listener, frames: 5, stoppingToken);

        // Act: the paired-phones loop reaches the other master too
        var other = await listener.ConnectBluetoothAsync(cuisine, stoppingToken);
        bool carried = await other.Ended;
        await Journey.StreamAsync(salon, music, listener, frames: 20, stoppingToken);

        // Assert
        carried.ShouldBeFalse();
        cuisine.Connected.ShouldBeEmpty();
        (listener.State is Listening { Master: "Salon", Links: Links.Bluetooth }).ShouldBeTrue($"{listener.State}");
        Speaker.IsTone(listener.Speaker.Last(20), MusicHz).ShouldBeTrue();
    }
}
