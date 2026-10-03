using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Monitoring;
using RepoWatch.Desktop.Platform;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.ViewModels;
using RepoWatch.GitHub;

namespace RepoWatch.Desktop.Tests.Accounts;

/// <summary>Account fixes from the PR #3 review.</summary>
public sealed class AccountReviewTests
{
    [Fact]
    public async Task Falling_back_to_session_only_storage_still_removes_the_previous_account_from_the_secure_store()
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        Assert.True(kit.Credentials.Items.ContainsKey(AccountKit.Octo));

        kit.Credentials.FailWrites = true; // Credential Manager stops working
        var result = await kit.SignInAsync(AccountKit.TokenJson("ghu_b", "ghr_b"), AccountKit.UserJson(7777, "other-user"));

        Assert.Equal(SignInOutcome.SignedIn, result.Outcome);
        Assert.False(kit.Accounts.CredentialsArePersistent);
        Assert.Empty(kit.Credentials.Items); // the previous account's live tokens are gone

        await kit.Accounts.SignOutAsync();
        Assert.Empty(kit.Credentials.Items);
    }

    [Fact]
    public async Task Rejection_after_a_successful_renewal_ends_the_session_and_deletes_tokens()
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());

        kit.Start();
        kit.Http.Status(HttpStatusCode.Unauthorized)                // /user rejects the stored token
            .Json(AccountKit.TokenJson("ghu_b", "ghr_b"))           // renewal succeeds
            .Status(HttpStatusCode.Unauthorized);                   // /user still rejects
        await kit.Accounts.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AccountState.ReconnectRequired, kit.Accounts.State);
        Assert.Equal(GitHub.Auth.SessionStatus.ReconnectRequired, kit.Accounts.Session!.Status); // no longer handed out as active
        Assert.True(kit.Accounts.Session.Lifetime.IsCancellationRequested);
        Assert.Empty(kit.Credentials.Items);
    }

    [Fact]
    public async Task The_badge_says_connecting_during_restore_and_never_claims_polling()
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        kit.Start();
        var states = new List<ConnectionState>();
        kit.Monitors.CurrentChanged += (_, _) => states.Add(kit.Monitors.Current.State);
        kit.Http.Json(AccountKit.UserJson());

        await kit.Accounts.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal([ConnectionState.Connecting, ConnectionState.SignedInIdle], states);
        Assert.DoesNotContain(ConnectionState.Polling, states);
        Assert.Equal(("Connecting…", StatusTone.Unknown), StatusPresentation.Connection(ConnectionState.Connecting));
        Assert.Equal("Signed in", StatusPresentation.Connection(ConnectionState.SignedInIdle).Label);
    }

    [Fact]
    public async Task Closing_settings_during_sign_in_does_not_cancel_it()
    {
        var kit = new AccountKit().Start();
        kit.Http.Json(AccountKit.DeviceCodeJson)
            .Json("""{"error":"authorization_pending"}""")
            .Json(AccountKit.TokenJson())
            .Json(AccountKit.UserJson());
        var http = GitHubHttp.CreateClient(new QueueHandler());
        var account = new AccountViewModel(kit.Accounts, new FakeShell(), new RecordingBrowser(),
            new AvatarLoader(http, NullLogger<AvatarLoader>.Instance), new GitHubEndpoints(kit.Options.GitHub), new ImmediateDispatcher(), kit.Time);

        var signIn = account.SignInCommand.ExecuteAsync(null);
        for (var i = 0; i < 100 && kit.Accounts.Flow?.UserCode is null; i++)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        account.Dispose(); // the settings window closes (e.g. Esc) while the user approves in the browser
        Assert.NotNull(kit.Accounts.Flow);

        await kit.Drive(signIn);

        Assert.Equal(AccountState.SignedIn, kit.Accounts.State);
        Assert.Null(kit.Accounts.Flow);

        // Reopening settings shows the result rather than a lost flow.
        var reopened = new AccountViewModel(kit.Accounts, new FakeShell(), new RecordingBrowser(),
            new AvatarLoader(http, NullLogger<AvatarLoader>.Instance), new GitHubEndpoints(kit.Options.GitHub), new ImmediateDispatcher(), kit.Time);
        Assert.True(reopened.IsSignedIn);
    }

    [Fact]
    public async Task Avatar_downloads_without_a_length_stop_at_the_cap()
    {
        var body = new CountingStream();
        var handler = new StreamHandler(body);
        var loader = new AvatarLoader(new HttpClient(handler), NullLogger<AvatarLoader>.Instance);

        var result = await loader.LoadAsync(new Uri("https://avatars.githubusercontent.com/u/1"), TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.True(body.BytesRead <= (1024 * 1024) + (64 * 1024), $"read {body.BytesRead} bytes");
    }

    /// <summary>An endless response body that counts how much was read.</summary>
    private sealed class CountingStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StreamHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = null; // chunked: no length
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
