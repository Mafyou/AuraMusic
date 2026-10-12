namespace AuraMusic.Kernel.Navigation;

/// <summary>
/// Leaving the app with the system Back button takes two presses: the first one only asks, a second one
/// within the window confirms. Call <see cref="ConfirmsExit"/> for each press made where Back would leave the app.
/// </summary>
public sealed class DoubleBackToExit(TimeProvider timeProvider, TimeSpan window)
{
    /// <summary>The time left to press Back a second time: about as long as a short toast stays on screen.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(2);

    // Timestamp of the press that is waiting for its confirmation, if any.
    long? pendingPress;

    public DoubleBackToExit(TimeProvider timeProvider) : this(timeProvider, DefaultWindow)
    {
    }

    /// <summary>Registers a press on Back.</summary>
    /// <returns>
    /// <see langword="true"/> when it confirms a press made within the window: the app may be left.
    /// <see langword="false"/> for a first press, or one that came too late: the user has to be asked to press again.
    /// </returns>
    public bool ConfirmsExit()
    {
        long now = timeProvider.GetTimestamp();

        if (pendingPress is { } pending && timeProvider.GetElapsedTime(pending, now) <= window)
        {
            // Back in the app later, leaving it takes two presses again.
            pendingPress = null;
            return true;
        }

        pendingPress = now;
        return false;
    }
}
