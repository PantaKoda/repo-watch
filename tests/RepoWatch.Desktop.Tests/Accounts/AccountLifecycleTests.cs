using System.Net;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Platform.Windows;
using RepoWatch.Desktop.Services;

namespace RepoWatch.Desktop.Tests.Accounts;

/// <summary>Stage 04 acceptance: sign-in, restart, denial/expiry/cancel, renewal, revocation and sign-out cleanup.</summary>
public sealed class AccountLifecycleTests
{
    [Fact]
    public async Task Signing_in_stores_tokens_securely_and_keys_settings_by_user_id()
    {
        var kit = new AccountKit().Start();

        var result = await kit.SignInAsync("""{"error":"authorization_pending"}""", AccountKit.TokenJson(), AccountKit.UserJson());

        Assert.Equal(SignInOutcome.SignedIn, result.Outcome);
        Assert.Equal(AccountState.SignedIn, kit.Accounts.State);
        Assert.Equal("octo-test", kit.Accounts.Identity!.Login);
        Assert.Equal(AccountKit.Octo, kit.Settings.App.ActiveAccount);
        Assert.Equal("ghu_a", kit.Credentials.Items[AccountKit.Octo].AccessToken);
        Assert.Equal("octo-test", kit.Settings.GetAccount(AccountKit.Octo).LastKnownLogin);
        Assert.Equal(ConnectionState.SignedInIdle, kit.Monitors.Current.State); // signed in, nothing monitored yet

        // Tokens never reach the settings database.
        var stored = kit.SettingsStore.LoadAppSettings();
        Assert.DoesNotContain("ghu_", SettingsCodecs.App.Serialize(stored.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restart_restores_the_session_and_confirms_the_identity()
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());

        kit.Start(); // new services over the same stores
        kit.Http.Json(AccountKit.UserJson());
        await kit.Accounts.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AccountState.SignedIn, kit.Accounts.State);
        Assert.Equal("octo-test", kit.Accounts.Identity!.Login);
    }

    [Theory]
    [InlineData("""{"error":"access_denied"}""", SignInOutcome.Denied)]
    [InlineData("""{"error":"expired_token"}""", SignInOutcome.Expired)]
    [InlineData("""{"error":"device_flow_disabled"}""", SignInOutcome.Failed)]
    public async Task Unsuccessful_sign_in_changes_nothing(string poll, SignInOutcome expected)
    {
        var kit = new AccountKit().Start();

        var result = await kit.SignInAsync(poll);

        Assert.Equal(expected, result.Outcome);
        Assert.NotNull(result.Message);
        Assert.Equal(AccountState.SignedOut, kit.Accounts.State);
        Assert.Null(kit.Settings.App.ActiveAccount);
        Assert.Empty(kit.Credentials.Items);
    }

    [Fact]
    public async Task Cancelling_sign_in_stops_polling_and_signs_nobody_in()
    {
        var kit = new AccountKit().Start();
        kit.Http.Json(AccountKit.DeviceCodeJson).Json("""{"error":"authorization_pending"}""");
        var code = await kit.Accounts.BeginSignInAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();

        var completion = kit.Accounts.CompleteSignInAsync(code, null, cancel.Token);
        kit.Time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        var result = await completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        kit.Time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(SignInOutcome.Cancelled, result.Outcome);
        Assert.Equal(2, kit.Http.Requests.Count);
        Assert.Equal(AccountState.SignedOut, kit.Accounts.State);
    }

    [Fact]
    public async Task An_expired_access_token_is_renewed_at_startup()
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        kit.Time.Advance(TimeSpan.FromHours(9)); // past the 8-hour access token lifetime

        kit.Start();
        kit.Http.Json(AccountKit.TokenJson("ghu_b", "ghr_b")).Json(AccountKit.UserJson());
        await kit.Accounts.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AccountState.SignedIn, kit.Accounts.State);
        Assert.Equal("ghr_b", kit.Credentials.Items[AccountKit.Octo].RefreshToken);
    }

    [Fact]
    public async Task Revoked_access_becomes_a_reconnect_state_without_a_retry_loop()
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());

        kit.Start();
        kit.Http.Status(HttpStatusCode.Unauthorized).Json("""{"error":"bad_refresh_token"}""");
        await kit.Accounts.RestoreAsync(TestContext.Current.CancellationToken);
        var requestsAfterRestore = kit.Http.Requests.Count;
        kit.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(AccountState.ReconnectRequired, kit.Accounts.State);
        Assert.Equal(ConnectionState.ReconnectRequired, kit.Monitors.Current.State);
        Assert.Empty(kit.Credentials.Items);
        Assert.Equal(requestsAfterRestore, kit.Http.Requests.Count);
        Assert.Equal("octo-test", kit.Accounts.Identity!.Login); // shown from the last known login
    }

    [Fact]
    public async Task GitHub_being_unreachable_at_startup_is_offline_not_signed_out()
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());

        kit.Start();
        kit.Http.Status(HttpStatusCode.BadGateway);
        await kit.Accounts.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AccountState.Offline, kit.Accounts.State);
        Assert.Equal(ConnectionState.Offline, kit.Monitors.Current.State);
        Assert.NotEmpty(kit.Credentials.Items);
    }

    [Fact]
    public async Task Sign_out_removes_credentials_and_keeps_watchlist_preferences()
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        kit.Settings.UpdateAccount(AccountKit.Octo, a => a with { Watchlist = [new WatchedRepository { RepositoryId = 9, Owner = "o", Name = "r" }] });
        var session = kit.Accounts.Session!;

        await kit.Accounts.SignOutAsync();

        Assert.Equal(AccountState.SignedOut, kit.Accounts.State);
        Assert.Null(kit.Accounts.Session);
        Assert.True(session.Lifetime.IsCancellationRequested); // in-flight work is cancelled
        Assert.Empty(kit.Credentials.Items);
        Assert.Null(kit.Settings.App.ActiveAccount);
        Assert.Equal(ConnectionState.NotSignedIn, kit.Monitors.Current.State);
        Assert.Single(kit.Settings.GetAccount(AccountKit.Octo).Watchlist);

        kit.Start();
        await kit.Accounts.RestoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AccountState.SignedOut, kit.Accounts.State);
    }

    [Fact]
    public async Task Signing_in_as_another_account_replaces_the_previous_credentials()
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());

        await kit.SignInAsync(AccountKit.TokenJson("ghu_other", "ghr_other"), AccountKit.UserJson(7777, "other-user"));

        Assert.Equal(7777, kit.Settings.App.ActiveAccount!.UserId);
        Assert.False(kit.Credentials.Items.ContainsKey(AccountKit.Octo));
        Assert.Equal("ghu_other", Assert.Single(kit.Credentials.Items).Value.AccessToken);
    }

    [Fact]
    public async Task If_secure_storage_fails_the_session_continues_in_memory_and_says_so()
    {
        var kit = new AccountKit().Start();
        kit.Credentials.FailWrites = true;

        var result = await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());

        Assert.Equal(SignInOutcome.SignedIn, result.Outcome);
        Assert.False(kit.Accounts.CredentialsArePersistent);
        Assert.Empty(kit.Credentials.Items);
    }

    [Fact]
    public async Task Windows_credential_manager_round_trip()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows Credential Manager is only available on Windows.");
            return;
        }

        await WindowsRoundTripAsync();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task WindowsRoundTripAsync()
    {
        var store = new WindowsCredentialStore($"RepoWatchTest:{Guid.NewGuid():N}/");
        var credential = new StoredCredential("ghu_test_only", DateTimeOffset.UtcNow.AddHours(8), "ghr_test_only", DateTimeOffset.UtcNow.AddDays(180));
        try
        {
            Assert.Null(await store.ReadAsync(AccountKit.Octo, TestContext.Current.CancellationToken));

            await store.WriteAsync(AccountKit.Octo, credential, TestContext.Current.CancellationToken);
            var read = await store.ReadAsync(AccountKit.Octo, TestContext.Current.CancellationToken);
            Assert.Equal(credential.AccessToken, read!.AccessToken);
            Assert.Equal(credential.RefreshToken, read.RefreshToken);
            Assert.Equal(credential.AccessTokenExpiresAt, read.AccessTokenExpiresAt);
        }
        finally
        {
            await store.DeleteAsync(AccountKit.Octo, TestContext.Current.CancellationToken);
        }

        Assert.Null(await store.ReadAsync(AccountKit.Octo, TestContext.Current.CancellationToken));
        await store.DeleteAsync(AccountKit.Octo, TestContext.Current.CancellationToken); // deleting twice is fine
    }
}
