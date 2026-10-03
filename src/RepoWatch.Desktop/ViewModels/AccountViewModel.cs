using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Platform;
using RepoWatch.Desktop.Platform;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;
using RepoWatch.GitHub;

namespace RepoWatch.Desktop.ViewModels;

/// <summary>
/// Account section of the settings window. A view over <see cref="AccountService"/>: the sign-in flow
/// is owned by the service, so closing the window does not cancel it and reopening shows it again.
/// Never shows or copies tokens or the device code.
/// </summary>
public sealed partial class AccountViewModel : ObservableObject, IDisposable
{
    private readonly AccountService _accounts;
    private readonly IShell _shell;
    private readonly IExternalBrowser _browser;
    private readonly AvatarLoader _avatars;
    private readonly GitHubEndpoints _endpoints;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private ITimer? _countdown;
    private Uri? _avatarUrl;

    public AccountViewModel(AccountService accounts, IShell shell, IExternalBrowser browser, AvatarLoader avatars, GitHubEndpoints endpoints, IUiDispatcher dispatcher, TimeProvider time)
    {
        _accounts = accounts;
        _shell = shell;
        _browser = browser;
        _avatars = avatars;
        _endpoints = endpoints;
        _dispatcher = dispatcher;
        _time = time;
        _accounts.Changed += OnAccountChanged;
        Refresh();
    }

    public bool IsConfigured => _accounts.IsSignInConfigured;

    public bool IsNotConfigured => !IsConfigured;

    [ObservableProperty] public partial bool ShowSignIn { get; private set; }

    [ObservableProperty] public partial bool IsSigningIn { get; private set; }

    [ObservableProperty] public partial bool IsSignedIn { get; private set; }

    [ObservableProperty] public partial bool NeedsReconnect { get; private set; }

    [ObservableProperty] public partial string? UserCode { get; private set; }

    [ObservableProperty] public partial Uri? VerificationUri { get; private set; }

    [ObservableProperty] public partial string? FlowStatus { get; private set; }

    [ObservableProperty] public partial string? ExpiresText { get; private set; }

    /// <summary>Outcome of the last sign-in attempt (declined, expired, failed), shown until the next attempt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    public bool HasMessage => Message is not null;

    [ObservableProperty] public partial string? Login { get; private set; }

    [ObservableProperty] public partial string? DisplayName { get; private set; }

    [ObservableProperty] public partial Bitmap? Avatar { get; private set; }

    [ObservableProperty] public partial string? StateText { get; private set; }

    [ObservableProperty] public partial string? StorageText { get; private set; }

    [ObservableProperty] public partial bool IsSessionOnly { get; private set; }

    /// <summary>Stops observing. A sign-in in progress keeps running in <see cref="AccountService"/>.</summary>
    public void Dispose()
    {
        _accounts.Changed -= OnAccountChanged;
        StopCountdown();
    }

    /// <summary>Starts sign-in (opening the browser at the verification page) and completes when it ends.</summary>
    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsConfigured && _accounts.Flow is null)
        {
            Message = null;
            await _accounts.StartSignInAsync(uri => _browser.OpenAsync(uri));
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _accounts.CancelSignIn();

    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        if (UserCode is not null)
        {
            await _shell.CopyTextAsync(UserCode);
        }
    }

    [RelayCommand]
    private async Task OpenVerificationPageAsync()
    {
        if (VerificationUri is not null)
        {
            await _browser.OpenAsync(VerificationUri);
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await _accounts.SignOutAsync();
        Message = "Signed out. Repo Watch's local credentials were removed; the authorization on GitHub is unchanged.";
    }

    [RelayCommand]
    private Task ManageAuthorizationAsync() => _browser.OpenAsync(_endpoints.AuthorizationManagement);

    [RelayCommand]
    private Task RetryAsync() => _accounts.RestoreAsync();

    private void OnAccountChanged(object? sender, EventArgs e) => _dispatcher.Post(Refresh);

    private void Refresh()
    {
        var state = _accounts.State;
        var flow = _accounts.Flow;

        IsSigningIn = flow is not null;
        UserCode = flow?.UserCode;
        VerificationUri = flow?.VerificationUri;
        FlowStatus = flow?.Status;
        if (flow is { UserCode: not null })
        {
            StartCountdown();
        }
        else
        {
            StopCountdown();
        }

        if (flow is null && _accounts.LastSignInMessage is { } outcome)
        {
            Message = outcome;
        }

        IsSignedIn = state is AccountState.SignedIn or AccountState.Offline;
        NeedsReconnect = state == AccountState.ReconnectRequired;
        ShowSignIn = IsConfigured && !IsSigningIn && state is AccountState.SignedOut or AccountState.ReconnectRequired;

        var identity = _accounts.Identity;
        Login = identity?.Login;
        DisplayName = identity?.Name;
        StateText = state switch
        {
            AccountState.Restoring => "Checking your GitHub sign-in…",
            AccountState.Offline => $"Offline: {_accounts.Detail}",
            AccountState.ReconnectRequired => _accounts.Detail ?? "Sign in again to continue.",
            AccountState.SignedIn => "Signed in.",
            _ => null,
        };

        IsSessionOnly = !_accounts.CredentialsArePersistent;
        StorageText = _accounts.StorageWarning
            ?? (_accounts.CredentialsArePersistent
                ? $"Your sign-in is stored securely in {_accounts.CredentialStorage}."
                : "Session only: secure credential storage isn't available, so you will need to sign in again after restarting.");

        if (identity?.AvatarUrl != _avatarUrl)
        {
            _avatarUrl = identity?.AvatarUrl;
            Avatar = null;
            if (_avatarUrl is { } url)
            {
                _ = LoadAvatarAsync(url);
            }
        }
    }

    private async Task LoadAvatarAsync(Uri url)
    {
        var bitmap = await _avatars.LoadAsync(url, CancellationToken.None);
        _dispatcher.Post(() =>
        {
            if (_avatarUrl == url)
            {
                Avatar = bitmap;
            }
        });
    }

    private void StartCountdown()
    {
        UpdateExpiry();
        _countdown ??= _time.CreateTimer(_ => _dispatcher.Post(UpdateExpiry), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    private void StopCountdown()
    {
        _countdown?.Dispose();
        _countdown = null;
        ExpiresText = null;
    }

    private void UpdateExpiry()
    {
        if (_accounts.Flow is not { UserCode: not null } flow)
        {
            ExpiresText = null;
            return;
        }

        var remaining = flow.ExpiresAt - _time.GetUtcNow();
        ExpiresText = remaining <= TimeSpan.Zero ? "The code has expired."
            : remaining < TimeSpan.FromMinutes(1) ? "The code expires in less than a minute."
            : $"The code expires in {(int)Math.Ceiling(remaining.TotalMinutes)} minutes.";
    }
}
