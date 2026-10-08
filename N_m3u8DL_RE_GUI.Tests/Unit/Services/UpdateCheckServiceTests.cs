#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using N_m3u8DL_RE_GUI.Core.Services;
using Xunit;

namespace N_m3u8DL_RE_GUI.Tests.Unit.Services;

public class UpdateCheckServiceTests
{
    /// <summary>Returns one canned response. No library, no container.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _factory;
        public StubHandler(Func<HttpResponseMessage> factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_factory());
    }

    private static GitHubUpdateCheckService WithRedirectTo(string location) =>
        new(new StubHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location);
            return response;
        }));

    private static Task<UpdateCheckResult> Check(GitHubUpdateCheckService service, string current) =>
        service.CheckForUpdateAsync("owner", "repo", Version.Parse(current));

    [Fact]
    public async Task ANewerReleaseIsReportedAsAvailable()
    {
        var result = await Check(WithRedirectTo("https://github.com/o/r/releases/tag/v2.2.0"), "2.1.5");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("v2.2.0", result.LatestVersion);
    }

    [Fact]
    public async Task TheSameVersionIsUpToDate()
    {
        var result = await Check(WithRedirectTo("https://github.com/o/r/releases/tag/v2.1.5"), "2.1.5");

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task AnOlderPublishedReleaseIsNotOfferedAsAnUpdate()
    {
        // A retracted release or a pre-release build running ahead of the tag.
        var result = await Check(WithRedirectTo("https://github.com/o/r/releases/tag/v2.1.4"), "2.1.5");

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task PatchNumbersCompareNumericallyNotAlphabetically()
    {
        // The trap a string comparison falls into: "2.1.10" < "2.1.9" as text.
        var result = await Check(WithRedirectTo("https://github.com/o/r/releases/tag/v2.1.10"), "2.1.9");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
    }

    [Fact]
    public async Task ATagWithoutTheVPrefixStillParses()
    {
        var result = await Check(WithRedirectTo("https://github.com/o/r/releases/tag/2.2.0"), "2.1.5");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task EveryRedirectStatusIsFollowed(HttpStatusCode code)
    {
        // The original clause listed Redirect and Found, which are the same value,
        // and omitted 307 and 308 entirely.
        var service = new GitHubUpdateCheckService(new StubHandler(() =>
        {
            var response = new HttpResponseMessage(code);
            response.Headers.Location = new Uri("https://github.com/o/r/releases/tag/v2.2.0");
            return response;
        }));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await Check(service, "2.1.5")).Status);
    }

    [Fact]
    public async Task ATwoComponentTagIsAFailureNotAQuietUpToDate()
    {
        // A tagging slip must not look identical to "you are current".
        var result = await Check(WithRedirectTo("https://github.com/o/r/releases/tag/v2.2"), "2.1.5");

        Assert.Equal(UpdateCheckStatus.CheckFailed, result.Status);
    }

    [Fact]
    public async Task AMissingLocationHeaderIsAFailure()
    {
        var service = new GitHubUpdateCheckService(
            new StubHandler(() => new HttpResponseMessage(HttpStatusCode.Found)));

        Assert.Equal(UpdateCheckStatus.CheckFailed, (await Check(service, "2.1.5")).Status);
    }

    [Fact]
    public async Task ANonRedirectResponseIsAFailure()
    {
        var service = new GitHubUpdateCheckService(
            new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));

        Assert.Equal(UpdateCheckStatus.CheckFailed, (await Check(service, "2.1.5")).Status);
    }

    [Fact]
    public async Task ANetworkFailureIsReportedAsAFailureNotAsUpToDate()
    {
        // Offline used to read as "✓ Latest version" because both states shared
        // one boolean value.
        var service = new GitHubUpdateCheckService(new StubHandler(
            () => throw new HttpRequestException("no network")));

        Assert.Equal(UpdateCheckStatus.CheckFailed, (await Check(service, "2.1.5")).Status);
    }

    [Fact]
    public async Task AFailedCheckReportsNoLatestVersion()
    {
        var service = new GitHubUpdateCheckService(new StubHandler(
            () => throw new HttpRequestException("no network")));

        Assert.Equal(string.Empty, (await Check(service, "2.1.5")).LatestVersion);
    }

    [Fact]
    public void HasUpdate_IsTrueOnlyWhenAnUpdateIsActuallyAvailable()
    {
        // CheckFailed must not collapse into the UpToDate reading.
        Assert.True(new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, "v1.0.0", "v1.1.0", "u").HasUpdate);
        Assert.False(new UpdateCheckResult(UpdateCheckStatus.UpToDate, "v1.0.0", "", "").HasUpdate);
        Assert.False(new UpdateCheckResult(UpdateCheckStatus.CheckFailed, "v1.0.0", "", "").HasUpdate);
    }

    [Theory]
    [InlineData(null, "repo")]
    [InlineData("owner", null)]
    [InlineData("", "repo")]
    [InlineData("   ", "repo")]
    public async Task MissingOwnerOrRepoIsAFailure(string? owner, string? repo)
    {
        var result = await new GitHubUpdateCheckService()
            .CheckForUpdateAsync(owner!, repo!, new Version(2, 1, 5));

        Assert.Equal(UpdateCheckStatus.CheckFailed, result.Status);
    }

    [Fact]
    public async Task AnUnknownCurrentVersionIsAFailureRatherThanAGuess()
    {
        // Guessing low nags about a version already installed; guessing high
        // suppresses every real update. Neither is acceptable.
        var result = await new GitHubUpdateCheckService()
            .CheckForUpdateAsync("owner", "repo", null!);

        Assert.Equal(UpdateCheckStatus.CheckFailed, result.Status);
    }

    [Fact]
    public void AppVersion_MatchesDirectoryBuildProps()
    {
        // Directory.Build.props is the single source of truth for the release
        // version; the assembly must not drift from it.
        var props = FindDirectoryBuildProps();
        var declared = Regex.Match(
            File.ReadAllText(props), @"<AppVersion>([^<]+)</AppVersion>").Groups[1].Value;

        Assert.False(string.IsNullOrWhiteSpace(declared), $"no <AppVersion> in {props}");
        Assert.Equal(
            declared,
            typeof(IUpdateCheckService).Assembly.GetName().Version?.ToString(3));
    }

    private static string FindDirectoryBuildProps()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Directory.Build.props not found above " + AppContext.BaseDirectory);
    }
}
