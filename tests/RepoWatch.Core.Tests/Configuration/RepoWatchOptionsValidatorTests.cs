using RepoWatch.Core.Configuration;
using RepoWatch.GitHub;

namespace RepoWatch.Core.Tests.Configuration;

public sealed class RepoWatchOptionsValidatorTests
{
    [Fact]
    public void Defaults_are_valid_and_sign_in_is_reported_unconfigured()
    {
        var options = new RepoWatchOptions();

        Assert.Empty(RepoWatchOptionsValidator.Validate(options));
        Assert.False(options.GitHub.IsSignInConfigured);
    }

    [Theory]
    [InlineData("Iv23liAbCdEf012345")]
    [InlineData("Iv1.0123456789abcdef")]
    public void Accepts_github_app_client_ids(string clientId)
    {
        var options = new RepoWatchOptions { GitHub = { ClientId = clientId } };

        Assert.Empty(RepoWatchOptionsValidator.Validate(options));
    }

    [Theory]
    [InlineData("123456")] // numeric App ID, a common mix-up
    [InlineData("ghp_abcdefghijklmnop")] // a token pasted into the wrong field
    [InlineData("Iv23 li")]
    public void Rejects_values_that_are_not_client_ids(string clientId)
    {
        var options = new RepoWatchOptions { GitHub = { ClientId = clientId } };

        var error = Assert.Single(RepoWatchOptionsValidator.Validate(options));
        Assert.Equal("GitHub:ClientId", error.Key);
        Assert.DoesNotContain(clientId, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://api.github.com")]
    [InlineData("api.github.com")]
    [InlineData("https://user:pass@api.github.com")]
    [InlineData("https://api.github.com/?x=1")]
    public void Rejects_unsafe_api_urls(string url)
    {
        var options = new RepoWatchOptions { GitHub = { ApiBaseUrl = url } };

        var error = Assert.Single(RepoWatchOptionsValidator.Validate(options));
        Assert.Equal("GitHub:ApiBaseUrl", error.Key);
    }

    [Theory]
    [InlineData("http://localhost:5080", true)]
    [InlineData("https://relay.example.com", true)]
    [InlineData("http://relay.example.com", false)]
    public void Relay_allows_plain_http_only_on_loopback(string url, bool valid)
    {
        var options = new RepoWatchOptions { Relay = { BaseUrl = url } };

        Assert.Equal(valid, RepoWatchOptionsValidator.Validate(options).Count == 0);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo/releases", true)]
    [InlineData("", true)]
    [InlineData("http://github.com/owner/repo/releases", false)]
    [InlineData("https://example.com/releases", false)]
    public void The_releases_page_must_be_an_https_GitHub_page(string url, bool valid)
    {
        var options = new RepoWatchOptions { Updates = { ReleasesUrl = url } };

        var errors = RepoWatchOptionsValidator.Validate(options);

        Assert.Equal(valid, errors.Count == 0);
        Assert.All(errors, e => Assert.Equal("Updates:ReleasesUrl", e.Key));
    }

    [Fact]
    public void The_default_releases_page_is_not_a_startup_error_for_another_GitHub_host()
    {
        var options = new RepoWatchOptions { GitHub = { WebBaseUrl = "https://github.example.com", ApiBaseUrl = "https://github.example.com/api/v3" } };

        Assert.Empty(RepoWatchOptionsValidator.Validate(options));
        Assert.Null(options.Updates.ResolveFor(new Uri(options.GitHub.WebBaseUrl)));
    }

    [Fact]
    public void Reports_every_out_of_range_interval()
    {
        var options = new RepoWatchOptions
        {
            Polling = { ActiveWorkflowSeconds = 0, QuietSeconds = 100_000 },
        };

        var keys = RepoWatchOptionsValidator.Validate(options).Select(e => e.Key);

        Assert.Equal(["Polling:ActiveWorkflowSeconds", "Polling:QuietSeconds"], keys);
    }

    [Fact]
    public void Endpoints_resolve_relative_to_configured_bases()
    {
        var endpoints = new GitHubEndpoints(new GitHubOptions { AppSlug = "repo-watch" });

        Assert.Equal("https://github.com/login/device/code", endpoints.DeviceCode.ToString());
        Assert.Equal("https://github.com/login/oauth/access_token", endpoints.AccessToken.ToString());
        Assert.Equal("https://github.com/apps/repo-watch/installations/new", endpoints.Installation?.ToString());
        Assert.Equal("https://api.github.com/", endpoints.ApiBase.ToString());
    }
}
