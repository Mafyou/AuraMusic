namespace AuraMusic.Kernel.Tests.Diagnostics;

/// <summary>The listener's figures for the diagnostics screen and the log.</summary>
public sealed class ListenerStatsTests
{
    readonly Mock<TimeProvider> clock = new();
    readonly ListenerStats sut;
    long now = 1_000_000;

    public ListenerStatsTests()
    {
        // One timestamp unit is one tick, so the clock of the tests moves by TimeSpan values.
        clock.SetupGet(c => c.TimestampFrequency).Returns(TimeSpan.TicksPerSecond);
        clock.Setup(c => c.GetTimestamp()).Returns(() => now);
        sut = new ListenerStats(clock.Object);
    }

    void Wait(double milliseconds) => now += TimeSpan.FromMilliseconds(milliseconds).Ticks;

    [Fact]
    public void TakeReport_CountsFramesPerSecondPerLink()
    {
        for (int i = 0; i < 50; i++)
            sut.Received(Links.Bluetooth, 240);
        for (int i = 0; i < 25; i++)
            sut.Received(Links.Wifi, 240);
        Wait(1_000);

        var report = sut.TakeReport();

        report.BluetoothFramesPerSecond.ShouldBe(50, tolerance: 1e-9);
        report.WifiFramesPerSecond.ShouldBe(25, tolerance: 1e-9);
    }

    [Fact]
    public void TakeReport_CountsTheFramePrefixInTheBitrate()
    {
        // 50 packets of 244 bytes plus their 6-byte prefix in one second: 250 * 8 * 50 = 100 kbps.
        for (int i = 0; i < 50; i++)
            sut.Received(Links.Bluetooth, 244);
        Wait(1_000);

        sut.TakeReport().Kbps.ShouldBe(100);
    }

    [Fact]
    public void TakeReport_StartsANewWindow()
    {
        sut.Received(Links.Wifi, 100);
        Wait(1_000);
        sut.TakeReport();
        Wait(1_000);

        var report = sut.TakeReport();

        report.WifiFramesPerSecond.ShouldBe(0);
        report.Kbps.ShouldBe(0);
    }

    [Fact]
    public void TakeReport_KeepsPlayoutCountersSinceTheStart()
    {
        sut.Concealed();
        sut.Dropped();
        sut.TakeReport();
        sut.Concealed();
        sut.CaughtUp();
        sut.Skipped();
        sut.Rebuffered();

        var report = sut.TakeReport();

        (report.Concealed, report.Dropped, report.CaughtUp, report.Skipped, report.Rebuffers).ShouldBe((2, 1, 1, 1, 1));
    }

    [Fact]
    public void TakeReport_GivesTheLatestPlayoutLevels()
    {
        sut.NativeCodec = true;
        sut.Links = Links.Wifi | Links.Bluetooth;
        sut.Playing(buffered: 180, target: 200, drift: -12, outputUnderruns: 3);

        var report = sut.TakeReport();

        (report.NativeCodec, report.Links, report.BufferedMs, report.TargetMs, report.DriftPpm)
            .ShouldBe((true, Links.Wifi | Links.Bluetooth, 180, 200, -12.0));
        sut.Underruns.ShouldBe(3);
    }

    [Fact]
    public void TakeReport_OverlappingCalls_NeverMoveTheWindowBack()
    {
        // The timer fires again while a report is still being taken: the late call must not reopen the window the
        // other one already closed, or the next figures are spread over too long a time.
        Wait(1_000);
        Thread? other = null;
        clock.Setup(c => c.GetTimestamp()).Returns(() =>
        {
            long read = now;
            if (other is null)
            {
                Wait(500);
                other = new Thread(() => sut.TakeReport());
                other.Start();
                // Read under the lock, the other call can only wait for this one: give it the time to overtake if it can.
                other.Join(TimeSpan.FromMilliseconds(200));
            }
            return read;
        });
        sut.TakeReport();
        other!.Join();
        for (int i = 0; i < 50; i++)
            sut.Received(Links.Bluetooth, 240);
        Wait(1_000);

        sut.TakeReport().BluetoothFramesPerSecond.ShouldBe(50, tolerance: 1e-9);
    }

    [Fact]
    public void TakeReport_RightAfterTheStart_DoesNotDivideByZero()
    {
        sut.Received(Links.Bluetooth, 100);

        var report = sut.TakeReport();

        double.IsFinite(report.BluetoothFramesPerSecond).ShouldBeTrue();
    }

    [Fact]
    public void TakeLongestGap_IsTheLongestTimeBetweenTwoPacketsPlayed()
    {
        sut.Playing(0, 0, 0, 0);
        Wait(20);
        sut.Playing(0, 0, 0, 0);
        Wait(350);
        sut.Playing(0, 0, 0, 0);
        Wait(40);
        sut.Playing(0, 0, 0, 0);

        sut.TakeLongestGap().ShouldBe(TimeSpan.FromMilliseconds(350));
    }

    [Fact]
    public void TakeLongestGap_StartsAfresh()
    {
        sut.Playing(0, 0, 0, 0);
        Wait(300);
        sut.Playing(0, 0, 0, 0);
        sut.TakeLongestGap();
        Wait(20);
        sut.Playing(0, 0, 0, 0);

        sut.TakeLongestGap().ShouldBe(TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public void TakeLongestGap_NothingPlayedYet_IsZero()
    {
        sut.Playing(0, 0, 0, 0);

        sut.TakeLongestGap().ShouldBe(TimeSpan.Zero);
    }
}
