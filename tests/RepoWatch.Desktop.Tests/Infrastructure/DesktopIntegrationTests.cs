using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Platform;
using RepoWatch.Desktop.Platform.Notifications;
using RepoWatch.Desktop.Platform.Startup;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.Tests.Notifications;

namespace RepoWatch.Desktop.Tests.Infrastructure;

/// <summary>Redaction, the diagnostics archive, single-instance activation and startup registration.</summary>
public sealed class DesktopIntegrationTests
{
    [Theory]
    [InlineData("token ghu_16C7e42F292c6912E7710c838347Ae178B4a in a message", "[redacted token]")]
    [InlineData("refresh ghr_1B4a2e77838347a7E420ce178F2E7c6912E169246c34E1ccbF66C46812d16D5B1A9Dc86A1498", "[redacted token]")]
    [InlineData("pat github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz", "[redacted token]")]
    [InlineData("Authorization: Bearer abcdef0123456789xyz", "Bearer [redacted]")]
    [InlineData("""{"device_code":"3584d83530557fdd1f46af8289938c8ef79f9dc5","interval":5}""", "\"device_code\":\"[redacted]")]
    [InlineData("access_token=abc123&expires_in=28800", "access_token=[redacted]")]
    [InlineData("enter WDJB-MJHT at github.com/login/device", "[redacted code]")]
    public void Secrets_are_redacted(string input, string expected)
    {
        var redacted = Redactor.Redact(input);

        Assert.Contains(expected, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("ghu_16C7", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("3584d835", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("WDJB-MJHT", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_log_lines_are_left_alone()
    {
        const string line = "2026-10-03 12:00:00.000 +02:00 INFO RepoWatch.Desktop.Shell.AppShell: Visuals applied: requested Frosted, achieved AcrylicBlur";

        Assert.Equal(line, Redactor.Redact(line));
    }

    [Fact]
    public async Task The_diagnostics_archive_is_redacted_and_holds_no_repository_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "repowatch-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root, Path.Combine(root, "d.json"), Path.Combine(root, "u.json"), Path.Combine(root, "logs"));
            Directory.CreateDirectory(paths.LogDirectory);
            await File.WriteAllTextAsync(Path.Combine(paths.LogDirectory, "repowatch-20261003.log"),
                "2026-10-03 INFO started\n2026-10-03 WARN sent Bearer ghu_16C7e42F292c6912E7710c838347Ae178B4a by mistake\n", TestContext.Current.CancellationToken);

            var kit = new AccountKit().Start();
            await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
            var monitor = new FakeMonitor();
            kit.Monitors.SetBase(monitor);
            using var conditions = new PollingConditions(kit.Settings, TimeProvider.System, () => false, watchSystem: false);
            var cache = new RepoWatch.Desktop.Storage.RepositoryCache(new RepoWatch.Desktop.Storage.LocalDatabase(Path.Combine(root, "repowatch.db")), TimeProvider.System, NullLogger<RepoWatch.Desktop.Storage.RepositoryCache>.Instance);
            using var notifications = new NotificationService(kit.Monitors, kit.Settings, kit.Accounts, cache, new RecordingSink(), TimeProvider.System, NullLogger<NotificationService>.Instance);
            var diagnostics = new DiagnosticsService(paths, kit.Accounts, kit.Monitors, notifications, conditions, TimeProvider.System);

            var file = diagnostics.Export();

            using var archive = ZipFile.OpenRead(file);
            Assert.Equal(["logs/repowatch-20261003.log", "summary.txt"], archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            var everything = string.Concat(archive.Entries.Select(e =>
            {
                using var reader = new StreamReader(e.Open());
                return reader.ReadToEnd();
            }));
            Assert.DoesNotContain("ghu_", everything, StringComparison.Ordinal);
            Assert.Contains("[redacted token]", everything, StringComparison.Ordinal);
            Assert.Contains("Account: SignedIn", everything, StringComparison.Ordinal);
            Assert.DoesNotContain("octo-test", everything, StringComparison.Ordinal); // no login or repository names
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_second_launch_activates_the_first_and_other_data_folders_run_independently()
    {
        var folder = Path.Combine(Path.GetTempPath(), "repowatch-instance-" + Guid.NewGuid().ToString("N"));
        using var first = new SingleInstance(folder);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ActivationRequested += (_, _) => activated.TrySetResult();
        first.Listen();

        using (var second = new SingleInstance(folder))
        {
            Assert.True(first.IsFirst);
            Assert.False(second.IsFirst);
            Assert.True(second.SignalFirst(TimeSpan.FromSeconds(5)));
        }

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var elsewhere = new SingleInstance(folder + "-other");
        Assert.True(elsewhere.IsFirst);
    }

    [Fact]
    public void Startup_registration_writes_and_removes_its_own_entry()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only");
            return;
        }

        var testKey = $@"Software\RepoWatchTests\Run-{Guid.NewGuid():N}"; // never the real Run key
        try
        {
            var registration = new WindowsStartupRegistration(@"C:\Program Files\Repo Watch\RepoWatch.exe", "RepoWatch", testKey);
            Assert.False(registration.IsRegistered);

            Assert.True(registration.Set(true));
            Assert.True(registration.IsRegistered);
            using (var key = Registry.CurrentUser.OpenSubKey(testKey))
            {
                Assert.Equal("\"C:\\Program Files\\Repo Watch\\RepoWatch.exe\" --startup", key!.GetValue("RepoWatch"));
            }

            // A moved executable is not "registered" until the entry is repaired.
            Assert.False(new WindowsStartupRegistration(@"D:\Apps\RepoWatch.exe", "RepoWatch", testKey).IsRegistered);

            Assert.True(registration.Set(false));
            Assert.False(registration.IsRegistered);
            Assert.True(registration.Set(false)); // idempotent
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\RepoWatchTests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Settings_expose_notifications_startup_and_the_shortcut()
    {
        var settings = TestServices.Settings();
        using var monitors = new MonitorHost(TimeProvider.System);
        var startup = new FakeStartup();
        var integration = new DesktopIntegration(startup);
        integration.SetShortcutStatus("Ctrl+Alt+R shows or hides the widget from anywhere.");
        var viewModel = SettingsViewModels.Create(settings, monitors, new FakeShell(), integration: integration);

        viewModel.NotifyCiRecovery = false;
        viewModel.HidePrivateDetails = true;
        viewModel.QuietHoursEnabled = true;
        viewModel.QuietStart = "23:00";
        viewModel.QuietEnd = "06:00";
        viewModel.StartAtLogin = true;
        viewModel.StartMinimized = true;
        viewModel.ShowHideShortcut = false;

        var app = settings.App;
        Assert.False(app.Notifications.CiRecovery);
        Assert.True(app.Notifications.HidePrivateDetails);
        Assert.Equal(new Core.Settings.QuietHours { Enabled = true, Start = new(23, 0), End = new(6, 0) }, app.Notifications.QuietHours);
        Assert.True(app.Startup.StartAtLogin);
        Assert.True(app.Startup.StartMinimized);
        Assert.False(app.Window.ShowHideShortcut);
        Assert.True(viewModel.CanStartMinimized);
        Assert.Equal("Ctrl+Alt+R shows or hides the widget from anywhere.", viewModel.ShortcutStatus);
        Assert.Contains("aren't supported", viewModel.NotificationStatus, StringComparison.Ordinal);
    }

    private sealed class FakeStartup : IStartupRegistration
    {
        public bool IsSupported => true;

        public bool IsRegistered { get; private set; }

        public bool Set(bool enabled)
        {
            IsRegistered = enabled;
            return true;
        }
    }
}
