namespace AuraMusic.Kernel.Localization;

/// <summary>Which language the app speaks: French by default, then English. The one the user picked, else English for
/// an English phone, else French; never a third language.</summary>
public static class AppLanguages
{
    public const string Default = "fr";

    /// <summary>Order of the language button: each tap moves to the next one.</summary>
    public static readonly ImmutableArray<string> SwitchOrder = ["fr", "en"];

    public static readonly FrozenSet<string> Supported = SwitchOrder.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The saved choice when there is one, else the phone's language. A saved value that is neither fr nor en
    /// (an old format, garbage) reads as French rather than falling back to the phone.</summary>
    public static string Resolve(string? chosen, CultureInfo device) =>
        FromCode(string.IsNullOrWhiteSpace(chosen) ? device.Name : chosen);

    /// <summary>Language of the app for a code ("en", "en-GB", "de_DE"…): a supported language for its own code
    /// (with or without a region), French for any other code, an empty one or garbage.</summary>
    public static string FromCode(string? code)
    {
        var language = code?.Trim().Split('-', '_')[0] ?? string.Empty;
        return Supported.TryGetValue(language, out var supported) ? supported : Default;
    }

    /// <summary>Culture the app runs in for <paramref name="language"/> on a phone set to <paramref name="device"/>:
    /// the phone's own regional culture when it speaks that language (fr-CA, en-GB: its numbers), fr-FR or en-US
    /// otherwise.</summary>
    public static CultureInfo CultureFor(string? language, CultureInfo device)
    {
        var appLanguage = FromCode(language);
        if (string.Equals(device.TwoLetterISOLanguageName, appLanguage, StringComparison.OrdinalIgnoreCase))
            return device;
        return CultureInfo.GetCultureInfo(appLanguage == "en" ? "en-US" : "fr-FR");
    }

    public static string Next(string current)
    {
        int index = SwitchOrder.IndexOf(FromCode(current));
        return SwitchOrder[(index + 1) % SwitchOrder.Length];
    }
}
