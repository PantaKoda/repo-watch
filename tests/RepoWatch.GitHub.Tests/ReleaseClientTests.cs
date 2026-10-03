using System.Net;
using RepoWatch.Core.Configuration;
using RepoWatch.GitHub.Updates;
using static RepoWatch.GitHub.Tests.Fixture;

namespace RepoWatch.GitHub.Tests;

public sealed class ReleaseClientTests
{
    private static (ReleaseClient Client, StubHandler Handler) Create()
    {
        var handler = new StubHandler(Time());
        return (new ReleaseClient(GitHubHttp.CreateClient(handler), new GitHubEndpoints(new GitHubOptions()), "octo/repo-watch"), handler);
    }

    private const string Releases = """
        [
          {"tag_name":"v0.3.0","name":"Repo Watch 0.3.0","body":"## Added\n- Faster","html_url":"https://github.com/octo/repo-watch/releases/tag/v0.3.0",
           "draft":false,"prerelease":false,"published_at":"2026-10-05T10:00:00Z",
           "assets":[{"name":"RepoWatch-0.3.0-win-x64.zip","browser_download_url":"https://github.com/octo/repo-watch/releases/download/v0.3.0/RepoWatch-0.3.0-win-x64.zip","size":57000000},
                     {"name":"RepoWatch-0.3.0-win-x64.zip.sha256","browser_download_url":"https://github.com/octo/repo-watch/releases/download/v0.3.0/RepoWatch-0.3.0-win-x64.zip.sha256","size":95}]},
          {"tag_name":"nightly","name":"Nightly","body":"","html_url":"https://github.com/octo/repo-watch/releases/tag/nightly","draft":false,"prerelease":true,"assets":[]}
        ]
        """;

    [Fact]
    public async Task Releases_are_read_without_signing_in_and_mapped()
    {
        var (client, handler) = Create();
        handler.Json(Releases);

        var result = await client.GetReleasesAsync(TestContext.Current.CancellationToken);

        var request = handler.Requests.Single();
        Assert.Equal("https://api.github.com/repos/octo/repo-watch/releases?per_page=30", request.Uri.ToString());
        Assert.Null(request.Header("Authorization")); // never sends the user's token
        var release = Assert.Single(result.Releases!); // "nightly" is not a version
        Assert.Equal("0.3.0", release.Version.ToString());
        Assert.True(release.CanInstallOnWindows);
        Assert.Equal("## Added\n- Faster", release.Notes);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero), release.PublishedAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "no public releases")]
    [InlineData(HttpStatusCode.Forbidden, "limit")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Failures_are_explained(HttpStatusCode status, string expected)
    {
        var (client, handler) = Create();
        handler.Status(status);

        var result = await client.GetReleasesAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://github.com/octo/repo-watch/releases/download/v0.3.0/a.zip", true)]
    [InlineData("http://github.com/octo/repo-watch/releases/download/v0.3.0/a.zip", false)]
    [InlineData("https://github.com/someone-else/repo-watch/releases/download/v0.3.0/a.zip", false)]
    [InlineData("https://example.com/octo/repo-watch/releases/download/v0.3.0/a.zip", false)]
    [InlineData("https://github.com/octo/repo-watch/archive/main.zip", false)]
    public void Only_release_files_of_the_configured_repository_are_downloaded(string url, bool allowed)
    {
        var (client, _) = Create();

        Assert.Equal(allowed, client.IsReleaseDownload(new Uri(url)));
    }

    [Fact]
    public async Task Downloads_stop_at_the_size_limit()
    {
        var (client, handler) = Create();
        handler.Content(new ByteArrayContent(new byte[2048]));
        using var target = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadAsync(
            new Uri("https://github.com/octo/repo-watch/releases/download/v0.3.0/a.zip"), target, 1024, null, TestContext.Current.CancellationToken));
    }
}
