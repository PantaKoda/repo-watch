using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Infrastructure;
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
        services.AddSingleton(_ =>
        {
            var database = new LocalDatabase(paths.DatabaseFile);
            database.Initialize();
            return database;
        });
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();

        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<ConfigurationErrorViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
