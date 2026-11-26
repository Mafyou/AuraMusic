namespace AuraMusic.Kernel.IntegrationTests.Multipath;

/// <summary>
/// The phones trusted for Wi-Fi survive a restart of the master: <see cref="TrustedDevices"/> written to real storage
/// (a file in a temp folder, where the app uses its preferences) and read back by a new instance.
/// </summary>
public sealed class TrustedDevicesStorageTests : IDisposable
{
    readonly TempFolder folder = new();

    string StoreFile => folder.File("trustedListeners.txt");

    TrustedDevices Load() => TrustedDevices.Parse(File.Exists(StoreFile) ? File.ReadAllText(StoreFile, Encoding.UTF8) : null);

    void Save(TrustedDevices trusted) => File.WriteAllText(StoreFile, trusted.Serialize(), Encoding.UTF8);

    [Fact]
    public void PhonesSeenOverBluetooth_AreStillTrustedAfterARestart()
    {
        // Arrange
        var pixel = Guid.NewGuid();
        var nord = Guid.NewGuid();

        // Act: one session trusts two phones, a later one starts from what was stored
        Save(Load().With(pixel).With(nord));
        var restarted = Load();

        // Assert
        restarted.Count.ShouldBe(2);
        restarted.Contains(pixel).ShouldBeTrue();
        restarted.Contains(nord).ShouldBeTrue();
        restarted.Contains(Guid.NewGuid()).ShouldBeFalse();
    }

    [Fact]
    public void FirstLaunch_TrustsNobody()
    {
        // Act
        var trusted = Load();

        // Assert
        trusted.Count.ShouldBe(0);
    }

    [Fact]
    public void DamagedStore_KeepsTheReadableEntries()
    {
        // Arrange
        var kept = Guid.NewGuid();
        File.WriteAllText(StoreFile, $"garbage,,{kept:N}, not-a-guid ,", Encoding.UTF8);

        // Act
        var trusted = Load();
        Save(trusted);

        // Assert
        trusted.Count.ShouldBe(1);
        trusted.Contains(kept).ShouldBeTrue();
        File.ReadAllText(StoreFile, Encoding.UTF8).ShouldBe(kept.ToString("N"));
    }

    [Fact]
    public void SameOrderEveryTime_SoTheStoreIsOnlyRewrittenOnChange()
    {
        // Arrange
        Guid[] phones = [.. Enumerable.Range(0, 5).Select(_ => Guid.NewGuid())];

        // Act
        Save(phones.Aggregate(TrustedDevices.Empty, (trusted, phone) => trusted.With(phone)));
        var first = File.ReadAllText(StoreFile, Encoding.UTF8);
        Save(phones.Reverse().Aggregate(TrustedDevices.Empty, (trusted, phone) => trusted.With(phone)));

        // Assert
        File.ReadAllText(StoreFile, Encoding.UTF8).ShouldBe(first);
    }

    public void Dispose() => folder.Dispose();
}
