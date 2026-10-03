using Microsoft.Extensions.Logging.Abstractions;
using RepoWatch.Core.Accounts;
using RepoWatch.GitHub.Auth;
using static RepoWatch.GitHub.Tests.Fixture;

namespace RepoWatch.GitHub.Tests;

/// <summary>Renewal edge cases from the PR #3 review: rotated tokens must never be lost or resurrected.</summary>
public sealed class RenewalRaceTests
{
    private static (AccountSession Session, StubHandler Handler, MemoryCredentialStore Store) Create()
    {
        var time = Time();
        var handler = new StubHandler(time);
        var store = new MemoryCredentialStore();
        var credential = new StoredCredential("ghu_old", time.GetUtcNow(), "ghr_old", time.GetUtcNow().AddDays(100)); // due for renewal
        store.Items[Account] = credential;
        var client = new DeviceFlowClient(GitHubHttp.CreateClient(handler), Endpoints, ClientId, time);
        return (new AccountSession(Account, credential, store, client, time, NullLogger.Instance), handler, store);
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_does_not_abort_the_shared_refresh()
    {
        var (session, handler, store) = Create();
        var release = new TaskCompletionSource();
        var received = new TaskCompletionSource();
        handler.Deferred(release.Task, TokenJson("ghu_new", "ghr_new"), received);
        using var impatient = new CancellationTokenSource();

        var first = session.GetAccessTokenAsync(impatient.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await impatient.CancelAsync();                       // GitHub has already rotated the tokens
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();                                 // the response still arrives

        Assert.Equal("ghu_new", await session.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.Equal("ghr_new", store.Items[Account].RefreshToken);
        Assert.Single(handler.Requests);                     // adopted without a second (now-invalid) refresh
        Assert.Equal(SessionStatus.Active, session.Status);
    }

    [Fact]
    public async Task Rotated_tokens_are_kept_in_memory_when_they_cannot_be_saved()
    {
        var (session, handler, store) = Create();
        handler.Json(TokenJson("ghu_new", "ghr_new"));
        store.FailWrites = true;
        Exception? reported = null;
        session.PersistenceFailed += (_, ex) => reported = ex;

        var token = await session.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ghu_new", token);
        Assert.IsType<System.ComponentModel.Win32Exception>(reported);
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal("ghu_new", await session.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Closing_during_a_renewal_waits_for_it_and_writes_nothing_afterwards()
    {
        var (session, handler, store) = Create();
        var release = new TaskCompletionSource();
        var received = new TaskCompletionSource();
        handler.Deferred(release.Task, TokenJson("ghu_new", "ghr_new"), received);

        var renewal = session.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var close = session.CloseAsync();                    // sign-out starts while the refresh is in flight
        Assert.False(close.IsCompleted);
        release.SetResult();
        await close.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        store.Items.Clear();                                 // sign-out deletes the credential now

        Assert.Null(await renewal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("write", store.Operations);
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task Disposing_during_a_renewal_does_not_throw_object_disposed()
    {
        var (session, handler, _) = Create();
        var release = new TaskCompletionSource();
        var received = new TaskCompletionSource();
        handler.Deferred(release.Task, TokenJson(), received);

        var renewal = session.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        session.Dispose();
        release.SetResult();

        Assert.Null(await renewal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(SessionStatus.Closed, session.Status);
    }
}
