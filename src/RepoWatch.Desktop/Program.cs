using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Infrastructure.Logging;

namespace RepoWatch.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The staged copy of an update replaces the installed folder and starts it; nothing else runs.
        if (Updates.UpdateApplier.TryRun(args, out var applied))
        {
            return applied;
        }

        var paths = AppPaths.Resolve();

        // One instance per user and data folder: a second launch shows the running widget and exits.
        using var instance = new Platform.SingleInstance(paths.DataDirectory);
        if (!instance.IsFirst)
        {
            return instance.SignalFirst(TimeSpan.FromSeconds(3)) ? 0 : 1;
        }

        using var loggerFactory = CreateLoggerFactory(paths);
        var logger = loggerFactory.CreateLogger(typeof(Program));

        var configuration = ConfigurationLoader.Load(paths);
        logger.LogInformation("Repo Watch {Version} starting on {OS}; data directory {DataDirectory}",
            AppInfo.Display, Environment.OSVersion, paths.DataDirectory);
        foreach (var error in configuration.Errors)
        {
            logger.LogError("Configuration error: {Error}", error);
        }

        var install = Updates.InstallInfo.Detect(args);
        if (install.UpdatedFrom is { } from)
        {
            logger.LogInformation("Updated from {From} to {Version}", from, install.Version);
        }

        using var services = CompositionRoot.Build(paths, configuration, loggerFactory, instance, install);
        try
        {
            return BuildAvaloniaApp(() => new App(services)).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Unhandled exception; shutting down");
            throw;
        }
    }

    // Entry point used by the Avalonia designer/previewer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(() => new App());

    private static AppBuilder BuildAvaloniaApp(Func<App> createApp) =>
        AppBuilder.Configure(createApp)
            .UsePlatformDetect()
            .LogToTrace();

    private static ILoggerFactory CreateLoggerFactory(AppPaths paths) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddDebug();
            try
            {
                builder.AddProvider(new FileLoggerProvider(paths.LogDirectory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unwritable data directory: continue with debug logging only.
            }
        });
}
