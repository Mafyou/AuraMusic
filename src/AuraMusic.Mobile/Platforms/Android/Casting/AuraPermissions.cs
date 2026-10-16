namespace AuraMusic.Mobile.Casting;

/// <summary>Runtime permissions for Bluetooth and Wi-Fi streaming; the master additionally needs the microphone permission for playback capture.</summary>
sealed class AuraPermissions(bool capture) : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions
    {
        get
        {
            // Before Android 12, Bluetooth on paired devices only needs install-time permissions.
            List<(string, bool)> permissions = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? [(Manifest.Permission.BluetoothConnect, true)]
                : [];

            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                permissions.Add((Manifest.Permission.PostNotifications, true));
            if (capture)
                permissions.Add((Manifest.Permission.RecordAudio, true));

            return [.. permissions];
        }
    }
}

/// <summary>
/// Newer Android versions gate the local network behind "nearby devices": Android 16 behind "nearby Wi-Fi
/// devices", Android 17 behind its own ACCESS_LOCAL_NETWORK, without which the beacons fail with EPERM and the
/// TCP link times out. Asked on its own and optional: refused, only the Wi-Fi link is lost, Bluetooth still works.
/// </summary>
sealed class WifiPermission : Permissions.BasePlatformPermission
{
    /// <summary>Android 17 (API 37). Not in the Mono.Android bindings yet, hence the plain name.</summary>
    public const string AccessLocalNetwork = "android.permission.ACCESS_LOCAL_NETWORK";

    public override (string androidPermission, bool isRuntime)[] RequiredPermissions
    {
        get
        {
            List<(string, bool)> permissions = [];
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                permissions.Add((Manifest.Permission.NearbyWifiDevices, true));
            if (OperatingSystem.IsAndroidVersionAtLeast(37))
                permissions.Add((AccessLocalNetwork, true));
            return [.. permissions];
        }
    }
}
