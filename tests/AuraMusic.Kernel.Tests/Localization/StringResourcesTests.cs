namespace AuraMusic.Kernel.Tests.Localization;

/// <summary>
/// The app speaks French by default and English for an English phone: every text it shows must exist in both
/// resource files, with the same placeholders, and every text of the app must be one it shows.
/// </summary>
public sealed partial class StringResourcesTests
{
    public static TheoryData<string> ResourceSets =>
    [
        "src/AuraMusic.Mobile/Resources/Strings/AppStrings",
        "src/AuraMusic.Kernel/Resources/KernelStrings",
    ];

    [Theory]
    [MemberData(nameof(ResourceSets))]
    public void FrenchAndEnglish_HaveTheSameKeys(string resourceSet)
    {
        var french = Read($"{resourceSet}.resx");
        var english = Read($"{resourceSet}.en.resx");

        english.Keys.Order().ShouldBe(french.Keys.Order());
    }

    [Theory]
    [MemberData(nameof(ResourceSets))]
    public void EveryText_IsFilledIn_WithTheSamePlaceholdersInBothLanguages(string resourceSet)
    {
        var french = Read($"{resourceSet}.resx");
        var english = Read($"{resourceSet}.en.resx");

        foreach (var (key, text) in french)
        {
            text.ShouldNotBeNullOrWhiteSpace(key);
            english[key].ShouldNotBeNullOrWhiteSpace(key);
            Placeholders(english[key]).ShouldBe(Placeholders(text), key);
        }
    }

    [Fact]
    public void EveryTextOfTheApp_IsShownSomewhere()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "src", "AuraMusic.Mobile"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();

        var unused = Read("src/AuraMusic.Mobile/Resources/Strings/AppStrings.resx").Keys
            .Where(key => !sources.Any(source => Regex.IsMatch(source, $@"\bAppStrings\.{key}\b")))
            .ToList();

        unused.ShouldBeEmpty();
    }

    static ImmutableArray<string> Placeholders(string text) => [.. Placeholder().Matches(text).Select(match => match.Value).Distinct().Order()];

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    static Dictionary<string, string> Read(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot, relativePath)).Root!
            .Elements("data")
            .ToDictionary(data => (string)data.Attribute("name")!, data => (string?)data.Element("value") ?? string.Empty);

    /// <summary>The folder of AuraMusic.slnx, found from where the tests run.</summary>
    static string RepositoryRoot
    {
        get
        {
            if (field is not null)
                return field;
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
                if (File.Exists(Path.Combine(folder.FullName, "AuraMusic.slnx")))
                    return field = folder.FullName;
            throw new DirectoryNotFoundException("AuraMusic.slnx not found above the test binaries.");
        }
    }
}
