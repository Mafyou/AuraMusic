namespace AuraMusic.Kernel.Multipath;

/// <param name="Phone">The listener's phone (<see cref="Hello.Id"/>): it may have one link of each kind.</param>
/// <param name="HealthyWifi">A Wi-Fi link that has been delivering in real time for a few seconds.</param>
public readonly record struct LinkStatus(Guid Phone, Links Kind, bool HealthyWifi);

/// <param name="AllOnWifi">Every phone is served by healthy Wi-Fi: the higher Wi-Fi bitrate is then safe.</param>
/// <param name="BluetoothCarriers">Bluetooth links carrying the stream, which share the master's radio.</param>
public readonly record struct RoutePlan(bool AllOnWifi, int BluetoothCarriers)
{
    /// <summary>What the encoder should produce for these links.</summary>
    public int Bitrate => AllOnWifi ? AuraProtocol.WifiBitrate : AuraProtocol.BluetoothBitrate(BluetoothCarriers);
}

/// <summary>Decides, for each packet, which of the master's links carry it.</summary>
public static class LinkRouter
{
    /// <summary>
    /// Wi-Fi links always carry the stream. A Bluetooth link idles while its phone has a healthy Wi-Fi link
    /// (less radio and battery for the same sound) and carries again the moment that Wi-Fi link falls behind.
    /// </summary>
    /// <param name="carries">Set, per link, to whether it gets the packet.</param>
    public static RoutePlan Plan(ReadOnlySpan<LinkStatus> links, Span<bool> carries)
    {
        bool allOnWifi = !links.IsEmpty;
        int bluetoothCarriers = 0;
        for (int i = 0; i < links.Length; i++)
        {
            bool phoneOnHealthyWifi = false;
            foreach (var other in links)
                phoneOnHealthyWifi |= other.Phone == links[i].Phone && other.HealthyWifi;

            carries[i] = links[i].Kind != Links.Bluetooth || !phoneOnHealthyWifi;
            if (carries[i] && links[i].Kind == Links.Bluetooth)
                bluetoothCarriers++;
            allOnWifi &= phoneOnHealthyWifi;
        }
        return new RoutePlan(allOnWifi, bluetoothCarriers);
    }
}
