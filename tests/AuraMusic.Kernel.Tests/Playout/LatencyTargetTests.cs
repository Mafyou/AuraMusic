namespace AuraMusic.Kernel.Tests.Playout;

/// <summary>The latency aimed for, and how the jitter buffer and the drift controller follow it.</summary>
public sealed class LatencyTargetTests
{
    readonly PlayoutController playout = new(Mock.Of<IPlayoutMetrics>());
    readonly DriftController drift = new(48_000);

    [Fact]
    public void Retarget_SetsTheCushionAndTheDriftTarget()
    {
        var target = new LatencyTarget(playout, drift);

        target.Retarget(200);

        target.LatencyMs.ShouldBe(200);
        target.ReachableMs.ShouldBe(PlayoutTuning.ReachableLatencyMs(200));
        playout.PrebufferFrames.ShouldBe(PlayoutTuning.CushionFrames(200));
        drift.TargetMs.ShouldBe(target.ReachableMs);
    }

    [Fact]
    public void Retarget_FollowsWhatTheOutputReallyHolds()
    {
        var target = new LatencyTarget(playout, drift, outputMs: 100);

        target.Retarget(200);

        target.ReachableMs.ShouldBe(PlayoutTuning.ReachableLatencyMs(200, 100));
        playout.PrebufferFrames.ShouldBe(PlayoutTuning.CushionFrames(200, 100));
    }

    [Fact]
    public void Retarget_SameLatency_ChangesNothing()
    {
        var target = new LatencyTarget(playout, drift);
        target.Retarget(200);
        playout.PrebufferFrames = 1;

        target.Retarget(200).ShouldBeFalse();

        playout.PrebufferFrames.ShouldBe(1);
    }

    [Theory]
    [InlineData(200, 300, true)]
    [InlineData(200, 500, true)]
    [InlineData(200, 280, false)]
    [InlineData(500, 200, false)]
    public void Retarget_AsksToRefillOnlyWhenRaisedFarAtOnce(int from, int to, bool refill)
    {
        var target = new LatencyTarget(playout, drift);
        target.Retarget(from);

        target.Retarget(to).ShouldBe(refill);
    }
}
