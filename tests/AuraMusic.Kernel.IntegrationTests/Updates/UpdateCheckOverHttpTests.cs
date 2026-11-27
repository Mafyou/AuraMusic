namespace AuraMusic.Kernel.IntegrationTests.Updates;

/// <summary>
/// The update check of the APK build, through the real <see cref="GitHubReleaseFeed"/>, a real <see cref="HttpClient"/>
/// and real sockets, against a local server answering as GitHub does.
/// </summary>
public sealed class UpdateCheckOverHttpTests
{
    const string Owner = "Mafyou";
    const string Repository = "AuraMusic";

    static string Release(string tag) => $$"""
        {
          "tag_name": "{{tag}}",
          "name": "AuraMusic {{tag}}",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://github.com/Mafyou/AuraMusic/releases/download/{{tag}}/notes.txt" },
            { "name": "AuraMusic-{{tag}}.apk", "browser_download_url": "https://github.com/Mafyou/AuraMusic/releases/download/{{tag}}/AuraMusic-{{tag}}.apk" }
          ]
        }
        """;

    static async Task<UpdateCheck> CheckAsync(LoopbackHttpServer server, string installed, TimeSpan? timeout = null)
    {
        using var http = server.CreateClient(timeout);
        var checker = new UpdateChecker(new GitHubReleaseFeed(http, Owner, Repository));
        return await checker.CheckAsync(installed, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NewerRelease_IsOffered_WithItsApk()
    {
        // Arrange
        using var server = new LoopbackHttpServer(new HttpAnswer(200, Release("v1.8.0")));

        // Act
        var check = await CheckAsync(server, "1.7.20");

        // Assert
        var available = (check is UpdateAvailable offered ? offered : null).ShouldNotBeNull();
        available.Release.Version.ShouldBe(new Version(1, 8, 0));
        available.Release.Tag.ShouldBe("v1.8.0");
        available.Release.ApkUrl.ShouldBe(new Uri("https://github.com/Mafyou/AuraMusic/releases/download/v1.8.0/AuraMusic-v1.8.0.apk"));
    }

    [Fact]
    public async Task Request_IsTheOneGitHubExpects()
    {
        // Arrange
        using var server = new LoopbackHttpServer(new HttpAnswer(200, Release("v1.7.20")));

        // Act
        await CheckAsync(server, "1.7.20");

        // Assert
        var request = server.Requests.ShouldHaveSingleItem();
        request.ShouldStartWith($"GET /repos/{Owner}/{Repository}/releases/latest HTTP/1.1");
        request.ShouldContain("User-Agent: AuraMusic", Case.Insensitive);
        request.ShouldContain("Accept: application/vnd.github+json", Case.Insensitive);
    }

    [Theory]
    [InlineData("v1.7.20", "1.7.20")]
    [InlineData("v1.7.19", "1.7.20")]
    [InlineData("v1.8", "1.8.0")]
    public async Task SameOrOlderRelease_IsNotOffered(string published, string installed)
    {
        // Arrange
        using var server = new LoopbackHttpServer(new HttpAnswer(200, Release(published)));

        // Act
        var check = await CheckAsync(server, installed);

        // Assert
        (check is UpToDate).ShouldBeTrue();
    }

    [Fact]
    public async Task NoReleaseYet_IsUpToDate()
    {
        // Arrange
        using var server = new LoopbackHttpServer(new HttpAnswer(404, """{ "message": "Not Found" }"""));

        // Act
        var check = await CheckAsync(server, "1.7.20");

        // Assert
        (check is UpToDate).ShouldBeTrue();
    }

    [Fact]
    public async Task ReleaseWithoutApk_IsNotOffered()
    {
        // Arrange
        using var server = new LoopbackHttpServer(new HttpAnswer(200, """{ "tag_name": "v9.0.0", "assets": [] }"""));

        // Act
        var check = await CheckAsync(server, "1.7.20");

        // Assert
        (check is UpToDate).ShouldBeTrue();
    }

    [Theory]
    [InlineData(503, """{ "message": "Service Unavailable" }""")]
    [InlineData(200, """{ "tag_name": "v2.0.0", "assets": [ { "name": "Aura""")]
    [InlineData(200, """{ "name": "no tag" }""")]
    public async Task GitHubDownOrOddPayload_FailsQuietly(int status, string body)
    {
        // Arrange
        using var server = new LoopbackHttpServer(new HttpAnswer(status, body));

        // Act
        var check = await CheckAsync(server, "1.7.20");

        // Assert
        (check is CheckFailed failed ? failed.Reason : null).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ConnectionDropped_FailsQuietly()
    {
        // Arrange
        using var server = new LoopbackHttpServer(new HttpAnswer(0, "", HangUp: true));

        // Act
        var check = await CheckAsync(server, "1.7.20");

        // Assert
        (check is CheckFailed).ShouldBeTrue();
    }

    [Fact]
    public async Task ServerThatNeverAnswers_TimesOut_AndFailsQuietly()
    {
        // Arrange
        using var server = new LoopbackHttpServer(new HttpAnswer(0, "", Stall: true));

        // Act
        var check = await CheckAsync(server, "1.7.20", timeout: TimeSpan.FromMilliseconds(500));

        // Assert
        (check is CheckFailed).ShouldBeTrue();
    }
}
