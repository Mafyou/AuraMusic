namespace AuraMusic.Kernel.Tests.Multipath;

public sealed class LinkRouterTests
{
    static readonly Guid Nord2 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid OnePlus7 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    static readonly Guid Pixel = Guid.Parse("33333333-3333-3333-3333-333333333333");

    static (bool[] Carries, bool AllOnWifi) Plan(params LinkStatus[] links)
    {
        var carries = new bool[links.Length];
        var plan = LinkRouter.Plan(links, carries);
        return (carries, plan.AllOnWifi);
    }

    static RoutePlan PlanOf(params LinkStatus[] links) => LinkRouter.Plan(links, new bool[links.Length]);

    [Fact]
    public void ThreeBluetoothListeners_ShareTheRadioAtALowerBitrate()
    {
        var plan = PlanOf(
            new LinkStatus(Nord2, Links.Bluetooth, false),
            new LinkStatus(OnePlus7, Links.Bluetooth, false),
            new LinkStatus(Pixel, Links.Bluetooth, false));

        plan.BluetoothCarriers.ShouldBe(3);
        plan.Bitrate.ShouldBeLessThan(AuraProtocol.Bitrate);
    }

    [Fact]
    public void BluetoothResting_BehindWifi_DoesNotCountAgainstTheRadio()
    {
        var plan = PlanOf(
            new LinkStatus(Nord2, Links.Bluetooth, false),
            new LinkStatus(Nord2, Links.Wifi, true),
            new LinkStatus(OnePlus7, Links.Bluetooth, false),
            new LinkStatus(Pixel, Links.Bluetooth, false));

        plan.BluetoothCarriers.ShouldBe(2);
        plan.Bitrate.ShouldBe(AuraProtocol.Bitrate);
    }

    [Fact]
    public void EveryoneOnHealthyWifi_UsesTheWifiBitrate()
    {
        PlanOf(new LinkStatus(Nord2, Links.Wifi, true)).Bitrate.ShouldBe(AuraProtocol.WifiBitrate);
    }

    [Fact]
    public void BluetoothOnly_CarriesAtTheBluetoothBitrate()
    {
        var (carries, allOnWifi) = Plan(new LinkStatus(Nord2, Links.Bluetooth, false));

        carries.ShouldBe([true]);
        allOnWifi.ShouldBeFalse();
    }

    [Fact]
    public void HealthyWifi_PutsTheSamePhonesBluetoothToRest()
    {
        var (carries, allOnWifi) = Plan(
            new LinkStatus(Nord2, Links.Bluetooth, false),
            new LinkStatus(Nord2, Links.Wifi, true));

        carries.ShouldBe([false, true]);
        allOnWifi.ShouldBeTrue();
    }

    [Fact]
    public void WifiFallingBehind_WakesBluetoothAtOnce()
    {
        var (carries, allOnWifi) = Plan(
            new LinkStatus(Nord2, Links.Bluetooth, false),
            new LinkStatus(Nord2, Links.Wifi, false)); // just connected, or its queue is backing up

        carries.ShouldBe([true, true]);
        allOnWifi.ShouldBeFalse();
    }

    [Fact]
    public void OnePhoneOnWifi_DoesNotRestAnotherPhonesBluetooth()
    {
        var (carries, allOnWifi) = Plan(
            new LinkStatus(Nord2, Links.Wifi, true),
            new LinkStatus(OnePlus7, Links.Bluetooth, false));

        carries.ShouldBe([true, true]);
        allOnWifi.ShouldBeFalse(); // the Bluetooth-only phone keeps everyone at the safe bitrate
    }

    [Fact]
    public void EveryPhoneOnHealthyWifi_AllowsTheHigherBitrate()
    {
        var (carries, allOnWifi) = Plan(
            new LinkStatus(Nord2, Links.Wifi, true),
            new LinkStatus(OnePlus7, Links.Wifi, true),
            new LinkStatus(OnePlus7, Links.Bluetooth, false));

        carries.ShouldBe([true, true, false]);
        allOnWifi.ShouldBeTrue();
    }

    [Fact]
    public void NoListener_IsNotAllOnWifi()
    {
        Plan().AllOnWifi.ShouldBeFalse();
    }
}
