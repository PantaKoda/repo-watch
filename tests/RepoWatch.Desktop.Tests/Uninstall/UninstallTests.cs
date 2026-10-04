using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Updates;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Platform.Startup;
using RepoWatch.Desktop.Storage;
using RepoWatch.Desktop.Uninstall;
using RepoWatch.Desktop.Updates;
using RepoWatch.GitHub;

namespace RepoWatch.Desktop.Tests.Uninstall;

internal sealed class FakeUninstallPlatform : IUninstallPlatform
{
    public int AllCredentialRemovals { get; private set; }

    public List<AccountKey> RemovedCredentials { get; } = [];

    public int NotificationRemovals { get; private set; }

    public List<string> Shortcuts { get; } = [];

    public List<string> StartedScripts { get; } = [];

    public int RemoveAllCredentials()
    {
        AllCredentialRemovals++;
        return 1;
    }

    public void RemoveCredential(AccountKey account) => RemovedCredentials.Add(account);

    public void RemoveNotificationIdentity() => NotificationRemovals++;

    public IReadOnlyList<string> FindShortcuts(string executable) => Shortcuts.Where(File.Exists).ToList();

    public bool StartCleanup(string script)
    {
        StartedScripts.Add(script);
        return true;
    }
}

internal sealed class FakeStartup(bool registered) : IStartupRegistration
{
    public bool IsSupported => true;

    public bool IsRegistered { get; private set; } = registered;

    public bool Set(bool enabled)
    {
        IsRegistered = enabled;
        return true;
    }
}

public sealed class UninstallTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("repowatch-uninstall-").FullName;
    private readonly FakeUninstallPlatform _platform = new();
    private readonly RecordingBrowser _browser = new();

    private string Install => Path.Combine(_root, "Programs", "RepoWatch");

    private string Data => Path.Combine(_root, "data");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // pooled connections keep the database file open
        for (var attempt = 0; attempt < 5 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private (UninstallService Service, AccountKit Kit, FakeStartup Startup) Create(bool release = true, bool startAtLogin = false, bool main = false)
    {
        Directory.CreateDirectory(Install);
        File.WriteAllText(Path.Combine(Install, "RepoWatch.exe"), "exe");
        Directory.CreateDirectory(Data);
        var kit = new AccountKit().Start();
        var paths = new AppPaths(Data, "d.json", "u.json", Path.Combine(Data, "logs"));
        var startup = new FakeStartup(startAtLogin);
        AppVersion.TryParse("0.2.0", out var version);
        var service = new UninstallService(paths, new InstallInfo(version, Install, release), kit.Accounts, kit.Settings, startup, _platform, _browser,
            new GitHubEndpoints(kit.Options.GitHub), NullLogger<UninstallService>.Instance, mainDataDirectory: main ? Data : Path.Combine(_root, "elsewhere"),
            scriptDirectory: _root); // never leaves scripts in the real temp folder
        return (service, kit, startup);
    }

    private void SeedDatabase()
    {
        new LocalDatabase(Path.Combine(Data, "repowatch.db")).Initialize();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Data, "repowatch.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (scope, schema_version, json, updated_at) VALUES ('app', 1, '{}', 'now');
            INSERT INTO settings (scope, schema_version, json, updated_at) VALUES ('account:github.com/4242', 1, '{}', 'now');
            INSERT INTO repository_snapshots VALUES ('github.com/4242', 1, 1, '{"private":"content"}', 'now');
            INSERT INTO http_cache VALUES ('github.com/4242', 'https://api.github.com/x', 'etag', 'body', 'now');
            INSERT INTO notification_history VALUES ('github.com/4242', 'key', 'shown', 'now');
            """;
        command.ExecuteNonQuery();
    }

    private long Count(string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Data, "repowatch.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void A_full_uninstall_script_removes_the_app_its_previous_version_and_the_data_folder_then_itself()
    {
        var (service, _, _) = Create();

        var script = service.BuildScript(keepSettings: false, waitForProcess: 4321);

        Assert.Contains("tasklist /FI \"PID eq 4321\"", script, StringComparison.Ordinal);
        Assert.Contains($"rmdir /s /q \"{Install}\"", script, StringComparison.Ordinal);
        Assert.Contains($"rmdir /s /q \"{Install}.previous\"", script, StringComparison.Ordinal);
        Assert.Contains($"rmdir /s /q \"{Data}\"", script, StringComparison.Ordinal);
        Assert.EndsWith("(goto) 2>nul & del \"%~f0\"" + Environment.NewLine, script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_copy_built_from_source_never_deletes_its_own_folder()
    {
        var (service, _, _) = Create(release: false);

        var script = service.BuildScript(keepSettings: false);

        Assert.DoesNotContain(Install, script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(service.Describe(false), i => !i.Removed && i.Title.StartsWith("Program folder", StringComparison.Ordinal));
    }

    [Fact]
    public void Keeping_settings_removes_logs_diagnostics_and_updates_but_not_the_settings_file()
    {
        var (service, _, _) = Create();

        var script = service.BuildScript(keepSettings: true);

        Assert.Contains($"\"{Path.Combine(Data, "logs")}\"", script, StringComparison.Ordinal);
        Assert.Contains($"\"{Path.Combine(Data, "diagnostics")}\"", script, StringComparison.Ordinal);
        Assert.Contains($"\"{Path.Combine(Data, "updates")}\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain($"rmdir /s /q \"{Data}\" ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("del /f /q \"" + Path.Combine(Data, "repowatch.db") + "\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Percent_signs_in_paths_are_escaped_for_cmd()
    {
        var (service, _, _) = Create();
        var odd = new AppPaths(Path.Combine(_root, "100%data"), "d", "u", "l");
        AppVersion.TryParse("0.2.0", out var version);
        var kit = new AccountKit().Start();
        var other = new UninstallService(odd, new InstallInfo(version, Install, true), kit.Accounts, kit.Settings, new FakeStartup(false), _platform, _browser,
            new GitHubEndpoints(kit.Options.GitHub), NullLogger<UninstallService>.Instance);

        Assert.Contains("100%%data", other.BuildScript(keepSettings: false), StringComparison.Ordinal);
        _ = service;
    }

    [Fact]
    public async Task Uninstalling_a_side_by_side_copy_removes_only_its_own_sign_in_and_leaves_shared_parts()
    {
        var (service, _, startup) = Create(startAtLogin: false, main: false);
        SeedDatabase();

        Assert.Null(await service.UninstallAsync(new UninstallOptions(KeepSettings: false, OpenGitHubAccess: false)));

        Assert.Equal(0, _platform.AllCredentialRemovals);
        Assert.Equal([new AccountKey("github.com", 4242)], _platform.RemovedCredentials);
        Assert.Equal(0, _platform.NotificationRemovals);
        Assert.False(startup.IsRegistered);
        Assert.Empty(_browser.Opened);
        Assert.True(File.Exists(Assert.Single(_platform.StartedScripts)));
    }

    [Fact]
    public async Task Uninstalling_the_main_install_removes_every_sign_in_start_entry_notification_identity_and_shortcut()
    {
        var (service, _, startup) = Create(startAtLogin: true, main: true);
        var shortcut = Path.Combine(_root, "Repo Watch.lnk");
        File.WriteAllText(shortcut, "link");
        _platform.Shortcuts.Add(shortcut);
        var exits = 0;
        service.ExitRequested += (_, _) => exits++;

        await service.UninstallAsync(new UninstallOptions(KeepSettings: false, OpenGitHubAccess: true));

        Assert.Equal(1, _platform.AllCredentialRemovals);
        Assert.Equal(1, _platform.NotificationRemovals);
        Assert.False(startup.IsRegistered);
        Assert.False(File.Exists(shortcut));
        Assert.Equal("https://github.com/settings/apps/authorizations", Assert.Single(_browser.Opened).ToString());
        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task Keeping_settings_empties_every_cache_but_keeps_the_settings()
    {
        var (service, _, _) = Create();
        SeedDatabase();

        await service.UninstallAsync(new UninstallOptions(KeepSettings: true, OpenGitHubAccess: false));

        Assert.Equal(0, Count("repository_snapshots"));
        Assert.Equal(0, Count("http_cache"));
        Assert.Equal(0, Count("notification_history"));
        Assert.Equal(2, Count("settings"));
        Assert.DoesNotContain("private", File.ReadAllText(Path.Combine(Data, "repowatch.db"), System.Text.Encoding.Latin1), StringComparison.Ordinal); // compacted
    }

    [Fact]
    public async Task The_cleanup_script_really_removes_the_folders_and_itself()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (service, _, _) = Create();
        Directory.CreateDirectory(Install + ".previous");
        Directory.CreateDirectory(Path.Combine(Data, "logs"));
        File.WriteAllText(Path.Combine(Data, "logs", "repowatch.log"), "log");
        using var finished = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true })!;
        await finished.WaitForExitAsync(TestContext.Current.CancellationToken);
        var script = Path.Combine(_root, "cleanup.cmd");
        File.WriteAllText(script, service.BuildScript(keepSettings: false, waitForProcess: finished.Id), new System.Text.UTF8Encoding(false));

        using var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", script }, CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = _root })!;
        await run.WaitForExitAsync(TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Install));
        Assert.False(Directory.Exists(Install + ".previous"));
        Assert.False(Directory.Exists(Data));
        Assert.False(File.Exists(script));
    }

    [Fact]
    public void Only_shortcuts_that_start_this_copy_are_recognised()
    {
        var exe = Path.Combine(Install, "RepoWatch.exe");
        var ours = Path.Combine(_root, "ours.lnk");
        var other = Path.Combine(_root, "other.lnk");
        File.WriteAllBytes(ours, [.. new byte[16], .. System.Text.Encoding.Unicode.GetBytes(exe), .. new byte[8]]);
        File.WriteAllBytes(other, [.. new byte[16], .. System.Text.Encoding.Unicode.GetBytes(@"C:\Program Files\Other\other.exe")]);

        Assert.True(ShortcutFiles.PointsTo(ours, exe));
        Assert.False(ShortcutFiles.PointsTo(other, exe));
    }
}
