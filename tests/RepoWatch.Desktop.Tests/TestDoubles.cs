using Microsoft.Extensions.Logging.Abstractions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Platform;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;

namespace RepoWatch.Desktop.Tests;

internal sealed class InMemorySettingsStore : ISettingsStore
{
    private string? _app;
    private readonly Dictionary<AccountKey, string> _accounts = [];

    public SettingsLoadResult<AppSettings> LoadAppSettings() => SettingsCodecs.App.Deserialize(_app);

    public bool SaveAppSettings(AppSettings settings)
    {
        _app = SettingsCodecs.App.Serialize(settings);
        return true;
    }

    public SettingsLoadResult<AccountSettings> LoadAccountSettings(AccountKey account) =>
        SettingsCodecs.Account.Deserialize(_accounts.GetValueOrDefault(account));

    public bool SaveAccountSettings(AccountKey account, AccountSettings settings)
    {
        _accounts[account] = SettingsCodecs.Account.Serialize(settings);
        return true;
    }
}

internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeShell : IShell
{
    public bool CanHideToTray { get; set; } = true;

    public int HideRequests { get; private set; }

    public int SettingsRequests { get; private set; }

    public void ShowWidget()
    {
    }

    public void HideWidget() => HideRequests++;

    public void OpenSettings() => SettingsRequests++;

    public int SignInRequests { get; private set; }

    public List<string> Copied { get; } = [];

    public void BeginSignIn() => SignInRequests++;

    public Task CopyTextAsync(string text)
    {
        Copied.Add(text);
        return Task.CompletedTask;
    }

    public bool DataFolderOpens { get; set; } = true;

    public Task<bool> OpenDataFolderAsync() => Task.FromResult(DataFolderOpens);

    public void Quit()
    {
    }
}

internal sealed class RecordingBrowser : IExternalBrowser
{
    public List<Uri> Opened { get; } = [];

    public LinkOpenResult Result { get; set; } = LinkOpenResult.Opened;

    public Task<LinkOpenResult> OpenAsync(Uri url)
    {
        Opened.Add(url);
        return Task.FromResult(Result);
    }
}

/// <summary>A monitor whose repositories the test sets directly.</summary>
internal sealed class FakeMonitor : IRepositoryMonitor
{
    public ConnectionState State { get; set; } = ConnectionState.Polling;

    public IReadOnlyList<MonitoredRepository> Repositories { get; private set; } = [];

    public bool IsRefreshing => false;

    public int RefreshRequests { get; private set; }

    public event EventHandler? Changed;

    public void Publish(IReadOnlyList<MonitoredRepository> repositories)
    {
        Repositories = repositories;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshAsync(RepositoryKey? repository = null, CancellationToken cancellationToken = default)
    {
        RefreshRequests++;
        return Task.CompletedTask;
    }
}

internal static class TestServices
{
    public static SettingsService Settings()
    {
        var settings = new SettingsService(() => new InMemorySettingsStore(), NullLogger<SettingsService>.Instance);
        settings.Load();
        return settings;
    }
}
