namespace AuraMusic.Mobile.Controls;

public static partial class BrandSplash
{
    /// <summary>On the decor view, above the activity's content, at the full size of the window. The content is hidden
    /// from TalkBack meanwhile, so that it reads the splash and not the page underneath; Back skips the splash.</summary>
    private static partial Action? Attach(Window window, BrandSplashView splash, IMauiContext context)
    {
        if (window.Handler?.PlatformView is not Android.App.Activity { Window.DecorView: Android.Views.ViewGroup decor } activity)
            return null;

        var view = Microsoft.Maui.Platform.ElementExtensions.ToPlatform(splash, context);
        decor.AddView(view, new Android.Widget.FrameLayout.LayoutParams(
            Android.Views.ViewGroup.LayoutParams.MatchParent, Android.Views.ViewGroup.LayoutParams.MatchParent));

        var content = activity.FindViewById(Android.Resource.Id.Content);
        // Given back as it was, whatever it was
        var accessibility = content?.ImportantForAccessibility ?? Android.Views.ImportantForAccessibility.Auto;
        content?.ImportantForAccessibility = Android.Views.ImportantForAccessibility.NoHideDescendants;

        // Added at Window.Created (Activity.OnPostCreate), after BackKey's callback (MainActivity.OnCreate): Android
        // asks this one first. Both are tied to the activity, which adds them again in the same order when the app
        // comes back from the background, so this one stays first. Back never reaches the page nobody sees yet, and
        // never leaves the app from the splash.
        var back = new SkipOnBack(splash);
        if (activity is AndroidX.Activity.ComponentActivity component)
            component.OnBackPressedDispatcher.AddCallback(component, back);

        return () =>
        {
            back.Remove();
            decor.RemoveView(view);
            content?.ImportantForAccessibility = accessibility;
        };
    }

    private sealed class SkipOnBack(BrandSplashView splash) : AndroidX.Activity.OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed() => splash.Skip();
    }
}
