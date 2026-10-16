namespace AuraMusic.Kernel.Navigation;

/// <summary>
/// Leaving the app with the system Back button takes two presses: the first one only asks, a second one
/// within the window confirms. Call <see cref="ConfirmsExit"/> for each press made where Back would leave the app.
/// </summary>
public sealed class DoubleBackToExit(TimeProvider timeProvider, TimeSpan window)
{
    /// <summary>The time left to press Back a second time: about as long as a short toast stays on screen.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the answer to the press that confirmed stands. One press can be handed over twice: MAUI asks the
    /// Shell first and, told the press was not handled, the next Back callback (<c>BackKey</c> on Android) asks it
    /// again. Both must hear "leave": taken a second time as a first press, it showed the toast again and the app
    /// could never be left (seen on a phone, Android 17).
    /// </summary>
    public static readonly TimeSpan SamePress = TimeSpan.FromMilliseconds(500);

    // Timestamp of the press that is waiting for its confirmation, if any.
    long? pendingPress;

    // Timestamp of the press that confirmed, while it may still be handed over again.
    long? confirmedPress;

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

        // The press that has just confirmed, handed over a second time.
        if (confirmedPress is { } confirmed && timeProvider.GetElapsedTime(confirmed, now) <= SamePress)
            return true;
        confirmedPress = null;

        if (pendingPress is { } pending && timeProvider.GetElapsedTime(pending, now) <= window)
        {
            // Back in the app later, leaving it takes two presses again.
            pendingPress = null;
            confirmedPress = now;
            return true;
        }

        pendingPress = now;
        return false;
    }
}
