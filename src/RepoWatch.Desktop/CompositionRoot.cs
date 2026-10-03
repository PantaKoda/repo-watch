using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Platform;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Platform;
using RepoWatch.Desktop.Platform.Tray;
using RepoWatch.Desktop.Platform.Windowing;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.Shell;
using RepoWatch.Desktop.Storage;
using RepoWatch.Desktop.ViewModels;
using RepoWatch.GitHub;

namespace RepoWatch.Desktop;

internal static class CompositionRoot
{
    public static ServiceProvider Build(AppPaths paths, ConfigurationLoadResult configuration, ILoggerFactory loggerFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(configuration);
        services.AddSingleton(configuration.Options);
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(sp => new GitHubEndpoints(configuration.Options.GitHub));
        services.AddSingleton(TimeProvider.System);

        // Storage: the database is opened and migrated on first use.
        services.AddSingleton(_ =>
        {
            var database = new LocalDatabase(paths.DatabaseFile);
            database.Initialize();
            return database;
        });
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton(sp => new SettingsService(
            () => sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<ILogger<SettingsService>>()));

        // GitHub access. Tokens go only to the credential store: Credential Manager on Windows,
        // otherwise a clearly labeled session-only store (never plaintext on disk).
        services.AddSingleton(_ => GitHubHttp.CreateClient());
        services.AddSingleton<ICredentialStore>(_ => OperatingSystem.IsWindows()
            ? new Platform.Windows.WindowsCredentialStore()
            : new SessionCredentialStore());
        services.AddSingleton<AccountService>();
        services.AddSingleton<WatchlistService>();
        services.AddSingleton<AccessCatalogService>();
        services.AddSingleton<IRepositoryMonitorFactory, GitHubMonitorFactory>();
        services.AddSingleton<MonitorCoordinator>();
        services.AddSingleton<AvatarLoader>();

        // Monitoring and platform adapters.
        services.AddSingleton<MonitorHost>();
        services.AddSingleton<VisualStateService>();
        services.AddSingleton<IExternalBrowser, BrowserLauncher>();
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<TrayService>();
        services.AddSingleton<WindowPlacementService>();
        services.AddSingleton<AppShell>();

        services.AddTransient<ConfigurationErrorViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
