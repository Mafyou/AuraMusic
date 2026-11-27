namespace AuraMusic.Kernel.UnitTests.Localization;

public sealed class AppLanguagesTests
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("fr", "fr")]
    public void Resolve_ChosenLanguage_WinsOverThePhone(string chosen, string expected)
    {
        AppLanguages.Resolve(chosen, German).ShouldBe(expected);
    }

    [Fact]
    public void Resolve_NothingChosen_FollowsAnEnglishPhone()
    {
        AppLanguages.Resolve(null, English).ShouldBe("en");
    }

    [Fact]
    public void Resolve_NothingChosen_FollowsAFrenchPhone()
    {
        AppLanguages.Resolve(null, French).ShouldBe("fr");
    }

    [Fact]
    public void Resolve_UnsupportedPhoneLanguage_FallsBackToFrench()
    {
        AppLanguages.Resolve(null, German).ShouldBe(AppLanguages.Default);
    }

    [Theory]
    [InlineData("klingon")]
    [InlineData("de")]
    [InlineData("es-ES")]
    public void Resolve_UnknownChoice_ReadsAsFrench_EvenOnAnEnglishPhone(string chosen)
    {
        AppLanguages.Resolve(chosen, English).ShouldBe("fr");
    }

    [Theory]
    [InlineData("en-GB", "en")]
    [InlineData("fr-FR", "fr")]
    public void Resolve_ChosenLanguageWithARegion_IsKept(string chosen, string expected)
    {
        AppLanguages.Resolve(chosen, German).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_EmptyChoice_FollowsThePhone(string chosen)
    {
        AppLanguages.Resolve(chosen, English).ShouldBe("en");
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("it-IT")]
    [InlineData("pt-BR")]
    public void Resolve_NothingChosen_AnyOtherPhoneLanguage_IsFrench(string phone)
    {
        AppLanguages.Resolve(null, CultureInfo.GetCultureInfo(phone)).ShouldBe("fr");
    }

    [Fact]
    public void Resolve_NothingChosen_InvariantPhone_IsFrench()
    {
        AppLanguages.Resolve(null, CultureInfo.InvariantCulture).ShouldBe("fr");
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("fr", "fr")]
    [InlineData("de", "fr")]
    [InlineData("garbage", "fr")]
    [InlineData(null, "fr")]
    public void FromCode_IsEnglishOnlyForEnglish_FrenchOtherwise(string? code, string expected)
    {
        AppLanguages.FromCode(code).ShouldBe(expected);
    }

    [Theory]
    [InlineData("fr", "fr-CA", "fr-CA")]
    [InlineData("fr", "de-DE", "fr-FR")]
    [InlineData("fr", "en-GB", "fr-FR")]
    [InlineData("en", "en-GB", "en-GB")]
    [InlineData("en", "fr-FR", "en-US")]
    [InlineData("de", "de-DE", "fr-FR")]
    public void CultureFor_IsNeverAThirdLanguage(string language, string phone, string expected)
    {
        AppLanguages.CultureFor(language, CultureInfo.GetCultureInfo(phone)).Name.ShouldBe(expected);
    }

    [Fact]
    public void SwitchOrder_FrenchFirst_ThenEnglish()
    {
        AppLanguages.SwitchOrder.ShouldBe(["fr", "en"]);
    }

    [Theory]
    [InlineData("fr", "en")]
    [InlineData("en", "fr")]
    [InlineData("EN", "fr")]
    public void Next_CyclesThroughTheLanguages(string current, string expected)
    {
        AppLanguages.Next(current).ShouldBe(expected);
    }

    [Fact]
    public void SwitchOrder_CoversExactlyTheSupportedLanguages()
    {
        AppLanguages.SwitchOrder.ShouldBe(AppLanguages.Supported, ignoreOrder: true);
    }

    // The language picked for the app in the Android settings (App languages) comes before the phone's
    [Theory]
    [InlineData("en-US", "fr-FR", "en")]
    [InlineData("en-GB,fr-FR", "fr-FR", "en")]
    [InlineData("de-DE", "fr-FR", "fr")]
    [InlineData("de-DE", "en-US", "fr")]
    [InlineData("fr-FR", "en-US", "fr")]
    [InlineData("", "en-US", "en")]
    [InlineData("", "fr-FR", "fr")]
    [InlineData(null, "en-GB", "en")]
    [InlineData("not a language!", "en-US", "en")]
    public void Resolve_WithoutASavedChoice_FollowsTheAppLanguage_ThenThePhone(string? appLanguage, string phone, string expected) =>
        AppLanguages.Resolve(null, AppLanguages.SystemCulture(appLanguage, CultureInfo.GetCultureInfo(phone))).ShouldBe(expected);

    [Theory]
    [InlineData("fr", "en-US")]
    [InlineData("en", "de-DE")]
    [InlineData("en", "")]
    public void Resolve_TheLanguageChosenInTheApp_WinsOverTheAppLanguage(string saved, string appLanguage) =>
        AppLanguages.Resolve(saved, AppLanguages.SystemCulture(appLanguage, CultureInfo.GetCultureInfo("fr-FR"))).ShouldBe(saved);
}
