namespace AuraMusic.Mobile;

/// <summary>
/// Gives the Back key to the app on every page. From Android 16, Back goes to the system unless the app keeps a
/// callback enabled, and MAUI only enables its own while it has something to go back to (a pushed page, a modal
/// page, an open flyout). On the home page Back then reached the Shell once and, told to leave, came back to it
/// again: the second press of the double Back did not leave the app (seen on Android 17). Same class as in the
/// other Mafyou IT apps.
/// </summary>
static class BackKey
{
    /// <summary>Call once, after base.OnCreate: the callback added last is the one Android asks first.</summary>
    public static void GiveToTheApp(AndroidX.Activity.ComponentActivity activity) =>
        activity.OnBackPressedDispatcher.AddCallback(activity, new Callback(activity));

    // Always enabled: Android hands every press on Back to the app. Depending on how MAUI shows the page, this
    // callback is the first to hear of the press, or the second: MAUI may ask the Shell itself and only pass on a
    // press nobody handled. The Shell can therefore be asked twice for one press, and has to give the same answer
    // both times (DoubleBackToExit.SamePress). A modal page (tutorial, diagnostics) lives in a dialog window with
    // its own Back dispatcher, so its presses do not come here.
    sealed class Callback(AndroidX.Activity.ComponentActivity activity) : AndroidX.Activity.OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed()
        {
            try
            {
                // The window hands Back to what is on display, as MAUI does: a modal page closes, the home page
                // asks before leaving.
                if (Application.Current?.Windows.FirstOrDefault() is IWindow window && window.BackButtonClicked())
                    return;
            }
            catch (Exception ex)
            {
                // A page that fails on Back keeps the app open: leaving it here would look like a crash.
                Log.Error(AuraLog.Tag, $"Back: the page on display failed to handle it: {ex}");
                return;
            }

            // Nobody kept it: the app is left the way Android leaves the first activity of a task (it goes to the
            // background, it is not destroyed; a broadcast or a listening goes on in its service). Handing the
            // press back to the dispatcher instead would bring it to the Shell a second time.
            if (activity.IsTaskRoot)
                activity.MoveTaskToBack(true);
            else
                activity.FinishAfterTransition();
        }
    }
}
