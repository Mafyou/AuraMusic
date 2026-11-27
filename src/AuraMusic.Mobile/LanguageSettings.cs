namespace AuraMusic.Mobile;

/// <summary>Applies the language the user picked (or, on a first launch, English for an English phone and French for
/// any other) to every resource lookup.</summary>
static class LanguageSettings
{
    const string PreferenceKey = "language";

    // Captured before we override it, so "follow the phone" keeps meaning the phone: the language picked for the app
    // under App languages (Android 13+), the phone's when none. iOS gives the language picked for the app through the
    // preferred languages, which .NET reads.
    static readonly CultureInfo DeviceCulture = AppLanguages.SystemCulture(
#if ANDROID
        AppLanguageTags.Read(),
#else
        null,
#endif
        CultureInfo.CurrentUICulture);

    public static string Current { get; private set; } = AppLanguages.Default;

    public static void Apply() => Apply(AppLanguages.Resolve(Preferences.Get(PreferenceKey, null), DeviceCulture));

    public static void SwitchToNext()
    {
        var next = AppLanguages.Next(Current);
        Preferences.Set(PreferenceKey, next);
        Apply(next);
    }

    static void Apply(string language)
    {
        // Texts and formats alike: a German phone runs the app in French, never in German.
        var culture = AppLanguages.CultureFor(language, DeviceCulture);
        CultureInfo.DefaultThreadCurrentCulture = culture; // background services too
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Current = AppLanguages.FromCode(language);
    }
}
