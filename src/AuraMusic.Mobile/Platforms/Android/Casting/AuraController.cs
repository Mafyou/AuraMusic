namespace AuraMusic.Mobile.Casting;

/// <summary>Entry point for the UI: asks for permissions and starts/stops the streaming services.</summary>
public static class AuraController
{
    static Context Context => global::Android.App.Application.Context;

    public static async Task StartBroadcastAsync()
    {
        await EnsurePermissionsAsync(capture: true);

        var activity = Platform.CurrentActivity as MainActivity
            ?? throw new InvalidOperationException(AppStrings.ErrorOpenApp);
        var (resultCode, data) = await activity.RequestMediaProjectionAsync();
        if (resultCode != Result.Ok || data is null)
            throw new InvalidOperationException(AppStrings.ErrorCaptureDenied);

        var intent = new Intent(Context, typeof(BroadcastService))
            .PutExtra(BroadcastService.ExtraResultCode, (int)resultCode)
            .PutExtra(BroadcastService.ExtraResultData, data);
        Context.StartForegroundService(intent);
    }

    public static async Task StartListeningAsync()
    {
        await EnsurePermissionsAsync(capture: false);
        Context.StartForegroundService(new Intent(Context, typeof(ListenService)));
    }

    public static void Stop()
    {
        Context.StopService(new Intent(Context, typeof(BroadcastService)));
        Context.StopService(new Intent(Context, typeof(ListenService)));
        AuraHub.Publish(new Idle());
    }

    static async Task EnsurePermissionsAsync(bool capture)
    {
        if (await new AuraPermissions(capture).RequestAsync() != PermissionStatus.Granted)
            throw new InvalidOperationException(capture
                ? AppStrings.ErrorPermissionsBroadcast
                : AppStrings.ErrorPermissionsListen);

        // Optional: refused, Bluetooth alone carries the stream. Said plainly, or two phones that only share a
        // Wi-Fi network would search for each other forever without a clue why. Short: Android cuts a toast at
        // two lines.
        if (await new WifiPermission().RequestAsync() != PermissionStatus.Granted)
            await CommunityToolkit.Maui.Alerts.Toast
                .Make(AppStrings.WifiPermissionDenied, CommunityToolkit.Maui.Core.ToastDuration.Long)
                .Show();
    }
}
