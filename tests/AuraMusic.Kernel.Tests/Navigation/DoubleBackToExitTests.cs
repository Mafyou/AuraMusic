namespace AuraMusic.Kernel.Tests.Navigation;

/// <summary>Leaving the app with Back takes two presses within two seconds.</summary>
public sealed class DoubleBackToExitTests
{
    readonly Mock<TimeProvider> clock = new();
    readonly DoubleBackToExit sut;
    long now = 1_000_000;

    public DoubleBackToExitTests()
    {
        // One timestamp unit is one tick, so the clock of the tests moves by TimeSpan values.
        clock.SetupGet(c => c.TimestampFrequency).Returns(TimeSpan.TicksPerSecond);
        clock.Setup(c => c.GetTimestamp()).Returns(() => now);
        sut = new DoubleBackToExit(clock.Object);
    }

    void Wait(double milliseconds) => now += TimeSpan.FromMilliseconds(milliseconds).Ticks;

    [Fact]
    public void FirstPress_DoesNotLeaveTheApp()
    {
        sut.ConfirmsExit().ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    [InlineData(1999)]
    [InlineData(2000)]
    public void SecondPressWithinTwoSeconds_LeavesTheApp(double delayInMilliseconds)
    {
        sut.ConfirmsExit();
        Wait(delayInMilliseconds);

        sut.ConfirmsExit().ShouldBeTrue();
    }

    [Theory]
    [InlineData(2001)]
    [InlineData(60_000)]
    public void SecondPressAfterTwoSeconds_AsksAgain(double delayInMilliseconds)
    {
        sut.ConfirmsExit();
        Wait(delayInMilliseconds);

        sut.ConfirmsExit().ShouldBeFalse();
    }

    [Fact]
    public void LatePress_OpensANewWindow()
    {
        // The second press came too late: it counts as a first one.
        sut.ConfirmsExit();
        Wait(5000);
        sut.ConfirmsExit();
        Wait(500);

        sut.ConfirmsExit().ShouldBeTrue();
    }

    [Fact]
    public void PressAfterAConfirmedExit_AsksAgain()
    {
        // Android may keep the app alive after Back: once back in it, leaving takes two presses again.
        sut.ConfirmsExit();
        Wait(500);
        sut.ConfirmsExit().ShouldBeTrue();
        Wait(600);

        sut.ConfirmsExit().ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(500)]
    public void ConfirmingPressHandedOverAgain_StillLeaves(double delayInMilliseconds)
    {
        // MAUI's Shell then the activity's Back callback can both be asked about the same press.
        sut.ConfirmsExit();
        Wait(300);
        sut.ConfirmsExit().ShouldBeTrue();
        Wait(delayInMilliseconds);

        sut.ConfirmsExit().ShouldBeTrue();
    }

    [Fact]
    public void PressAfterTheSamePressWindow_AsksAgain()
    {
        sut.ConfirmsExit();
        sut.ConfirmsExit().ShouldBeTrue();
        Wait(DoubleBackToExit.SamePress.TotalMilliseconds + 1);

        sut.ConfirmsExit().ShouldBeFalse();
        DoubleBackToExit.SamePress.ShouldBe(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void CustomWindow_ReplacesTheTwoSeconds()
    {
        var shortWindow = new DoubleBackToExit(clock.Object, TimeSpan.FromMilliseconds(500));
        shortWindow.ConfirmsExit();
        Wait(501);

        shortWindow.ConfirmsExit().ShouldBeFalse();
        DoubleBackToExit.DefaultWindow.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void EveryPress_ReadsTheInjectedClock()
    {
        sut.ConfirmsExit();
        sut.ConfirmsExit();

        clock.Verify(c => c.GetTimestamp(), Times.Exactly(2));
    }
}
