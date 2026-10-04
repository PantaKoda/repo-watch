using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Updates;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Platform.Startup;
using RepoWatch.Desktop.Storage;
using RepoWatch.Desktop.Uninstall;
using RepoWatch.Desktop.Updates;
using RepoWatch.Desktop.ViewModels;
using RepoWatch.GitHub;

namespace RepoWatch.Desktop.Tests.Uninstall;

internal sealed class FakeUninstallPlatform : IUninstallPlatform
{
    public int AllCredentialRemovals { get; private set; }

    public List<AccountKey> RemovedCredentials { get; } = [];

    public int NotificationRemovals { get; private set; }

    public List<string> Shortcuts { get; } = [];

    public List<string> StartedScripts { get; } = [];

    public bool CleanupStarts { get; set; } = true;

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
        return CleanupStarts;
    }
}

internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add($"{logLevel}: {formatter(state, exception)} {exception?.GetType().Name} {exception?.Message}");
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
    private static readonly string[] Shipped = ["RepoWatch.exe", "RepoWatch.dll", "runtimes/win-x64/native.dll", "release.json"];

    private readonly string _root = Directory.CreateTempSubdirectory("repowatch-uninstall-").FullName;
    private readonly FakeUninstallPlatform _platform = new();
    private readonly RecordingBrowser _browser = new();

    public UninstallTests() => Install = Path.Combine(_root, "Programs", "RepoWatch");

    /// <summary>The program folder; tests may point it at a shared folder such as "Tools".</summary>
    private string Install { get; set; }

    private string Data { get; set; } = "";

    private ListLogger<UninstallService> Log { get; } = new();

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

    /// <summary>Lays out a release folder: the shipped files, and a manifest listing them (unless <paramref name="withList"/> is false).</summary>
    private void LayOutRelease(string folder, bool withList = true)
    {
        foreach (var file in Shipped)
        {
            var path = Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "shipped");
        }

        var list = withList ? ", \"files\": [" + string.Join(",", Shipped.Select(f => $"\"{f}\"")) + "]" : "";
        File.WriteAllText(Path.Combine(folder, "release.json"), "{\"version\":\"0.2.0\"" + list + "}");
    }

    private (UninstallService Service, AccountKit Kit, FakeStartup Startup) Create(bool release = true, bool startAtLogin = false, bool main = false, bool withList = true, string data = "data")
    {
        LayOutRelease(Install, withList);
        Data = Path.Combine(_root, data);
        Directory.CreateDirectory(Data);
        var kit = new AccountKit().Start();
        var paths = new AppPaths(Data, "d.json", Path.Combine(Data, "repowatch.config.json"), Path.Combine(Data, "logs"));
        var startup = new FakeStartup(startAtLogin);
        AppVersion.TryParse("0.2.0", out var version);
        var service = new UninstallService(paths, new InstallInfo(version, Install, release), kit.Accounts, kit.Settings, startup, _platform, _browser,
            new GitHubEndpoints(kit.Options.GitHub), Log,
            mainDataDirectory: main ? Data : Path.Combine(_root, "elsewhere"),
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

    private async Task RunScript(string script, bool unixToolsFirst = false)
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", script }, CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = _root };
        if (unixToolsFirst)
        {
            info.Environment["PATH"] = @"C:\Program Files\Git\usr\bin;" + Environment.GetEnvironmentVariable("PATH");
        }

        using var run = System.Diagnostics.Process.Start(info)!;
        await run.WaitForExitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> ExitedProcess()
    {
        using var finished = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true })!;
        await finished.WaitForExitAsync(TestContext.Current.CancellationToken);
        return finished.Id;
    }

    [Fact]
    public void Only_the_files_a_release_shipped_are_deleted_from_a_shared_folder()
    {
        Install = Path.Combine(_root, "Tools"); // files copied flat into a folder that holds other things
        var (service, _, _) = Create();
        File.WriteAllText(Path.Combine(Install, "my-notes.txt"), "not Repo Watch's");

        var plan = service.Plan(keepSettings: false);

        Assert.Equal(Shipped.Length, plan.Files.Count(f => f.StartsWith(Install, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(Path.Combine(Install, "my-notes.txt"), plan.Files);
        Assert.Contains(Path.Combine(Install, "my-notes.txt"), plan.KeptForeign);
        Assert.DoesNotContain(Install, plan.OwnedFolders); // only removed if empty, never recursively
        Assert.Contains(Install, plan.EmptyFolders);
    }

    [Fact]
    public void A_release_without_a_file_list_keeps_its_folder()
    {
        var (service, _, _) = Create(withList: false);

        var plan = service.Plan(keepSettings: false);

        Assert.NotNull(plan.ProgramFolderKept);
        Assert.DoesNotContain(plan.Files, f => f.StartsWith(Install, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_copy_built_from_source_never_deletes_its_own_folder()
    {
        var (service, _, _) = Create(release: false);

        var plan = service.Plan(keepSettings: false);

        Assert.Contains("built from source", plan.ProgramFolderKept, StringComparison.Ordinal);
        Assert.DoesNotContain(plan.Files.Concat(plan.EmptyFolders).Concat(plan.OwnedFolders), p => p.StartsWith(Install, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_custom_data_folder_loses_only_Repo_Watchs_files()
    {
        var (service, _, _) = Create(data: "portable");
        SeedDatabase();
        Directory.CreateDirectory(Path.Combine(Data, "logs"));
        File.WriteAllText(Path.Combine(Data, "logs", "repowatch-20261004.log"), "ours");
        File.WriteAllText(Path.Combine(Data, "logs", "other-app.log"), "theirs");
        File.WriteAllText(Path.Combine(Data, "photos.zip"), "theirs");

        var plan = service.Plan(keepSettings: false);

        Assert.Empty(plan.OwnedFolders);
        Assert.Contains(Path.Combine(Data, "repowatch.db"), plan.Files);
        Assert.Contains(Path.Combine(Data, "logs", "repowatch-20261004.log"), plan.Files);
        Assert.Contains(Path.Combine(Data, "logs", "other-app.log"), plan.KeptForeign);
        Assert.DoesNotContain(Path.Combine(Data, "photos.zip"), plan.Files);
        Assert.Contains(Path.Combine(Data, "photos.zip"), plan.KeptForeign); // counted in "Other files (kept)"
        Assert.Contains(Data, plan.EmptyFolders);
    }

    [Fact]
    public void The_default_data_folder_is_Repo_Watchs_own_and_is_removed_whole()
    {
        var (service, _, _) = Create(main: true);

        Assert.Equal([Data], service.Plan(keepSettings: false).OwnedFolders);
        Assert.Empty(service.Plan(keepSettings: true).OwnedFolders); // keeping settings: never the whole folder
    }

    [Fact]
    public void Running_from_the_backup_after_a_failed_update_also_cleans_the_half_copied_folder()
    {
        var half = Install;
        Directory.CreateDirectory(half);
        File.WriteAllText(Path.Combine(half, "RepoWatch.dll"), "half-copied");
        Install = half + ".previous";
        var (service, _, _) = Create();

        var plan = service.Plan(keepSettings: false);

        Assert.Contains(Path.Combine(half, "RepoWatch.dll"), plan.Files);
        Assert.Contains(half, plan.EmptyFolders);
    }

    [Fact]
    public void The_script_calls_system_tools_by_full_path_and_escapes_percent_signs()
    {
        var plan = new UninstallPlan([], [], [Path.Combine(_root, "100%data")], [], null);

        var script = UninstallService.BuildScript(plan, Path.Combine(_root, "s"), 4321);

        Assert.Contains("\"%SystemRoot%\\System32\\tasklist.exe\" /FI \"PID eq 4321\"", script, StringComparison.Ordinal);
        Assert.Contains("\"%SystemRoot%\\System32\\find.exe\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain(" timeout /t", script, StringComparison.Ordinal);
        Assert.Contains("100%%data", script, StringComparison.Ordinal);
        Assert.EndsWith("(goto) 2>nul & del \"%~f0\"" + Environment.NewLine, script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_real_script_removes_exactly_the_planned_files_even_with_Unix_tools_first_on_PATH()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Install = Path.Combine(_root, "Tools");
        var (service, _, _) = Create(data: "portable");
        Directory.CreateDirectory(Install + ".previous");
        File.WriteAllText(Path.Combine(Install + ".previous", "RepoWatch.exe"), "old");
        File.WriteAllText(Path.Combine(Install, "my-notes.txt"), "keep me");
        File.WriteAllText(Path.Combine(Data, "repowatch.db"), "db");
        File.WriteAllText(Path.Combine(Data, "photos.zip"), "keep me");
        var plan = service.Plan(keepSettings: false);
        var listBase = Path.Combine(_root, "cleanup");
        File.WriteAllText(listBase + ".cmd", UninstallService.BuildScript(plan, listBase, await ExitedProcess()), new System.Text.UTF8Encoding(false));

        await RunScript(listBase + ".cmd", unixToolsFirst: true);

        Assert.All(Shipped, f => Assert.False(File.Exists(Path.Combine(Install, f.Replace('/', Path.DirectorySeparatorChar)))));
        Assert.False(Directory.Exists(Path.Combine(Install, "runtimes"))); // emptied folders are removed
        Assert.True(File.Exists(Path.Combine(Install, "my-notes.txt"))); // the shared folder and its other files stay
        Assert.False(Directory.Exists(Install + ".previous"));
        Assert.False(File.Exists(Path.Combine(Data, "repowatch.db")));
        Assert.True(File.Exists(Path.Combine(Data, "photos.zip")));
        Assert.False(File.Exists(listBase + ".cmd"));
        Assert.False(File.Exists(listBase + ".files.txt"));
        Assert.False(File.Exists(listBase + ".folders.txt"));
    }

    [Fact]
    public async Task The_script_waits_for_a_running_process_even_with_Unix_tools_first_on_PATH()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var file = Path.Combine(_root, "in-use.txt");
        File.WriteAllText(file, "x");
        using var running = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul") { CreateNoWindow = true })!;
        var listBase = Path.Combine(_root, "wait");
        File.WriteAllText(listBase + ".cmd", UninstallService.BuildScript(new UninstallPlan([file], [], [], [], null), listBase, running.Id), new System.Text.UTF8Encoding(false));

        var script = RunScript(listBase + ".cmd", unixToolsFirst: true);
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(file)); // still waiting: GNU find on PATH didn't end the wait early

        await script;
        Assert.True(running.HasExited);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Uninstalling_a_side_by_side_copy_removes_only_its_own_accounts_sign_in_and_leaves_shared_parts()
    {
        var (service, _, startup) = Create(main: false);
        SeedDatabase();

        Assert.Null(await service.UninstallAsync(new UninstallOptions(KeepSettings: false, OpenGitHubAccess: false)));

        Assert.Equal(0, _platform.AllCredentialRemovals);
        Assert.Equal([new AccountKey("github.com", 4242)], _platform.RemovedCredentials);
        Assert.Equal(0, _platform.NotificationRemovals);
        Assert.False(startup.IsRegistered);
        Assert.Empty(_browser.Opened);
        Assert.True(File.Exists(Assert.Single(_platform.StartedScripts)));
        Assert.Contains(service.Describe(false), i => i.Title == "Sign-in" && i.Detail.Contains("signed out too", StringComparison.Ordinal));
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
        Assert.DoesNotContain("private", File.ReadAllText(Path.Combine(Data, "repowatch.db"), System.Text.Encoding.Latin1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task If_the_cache_cant_be_emptied_the_settings_are_removed_too_and_the_user_is_told()
    {
        var (service, _, _) = Create();
        File.WriteAllText(Path.Combine(Data, "repowatch.db"), "this is not a database");

        var note = await service.UninstallAsync(new UninstallOptions(KeepSettings: true, OpenGitHubAccess: false));

        Assert.True(note?.Contains("removed as well", StringComparison.Ordinal) == true, string.Join(" | ", Log.Lines));
        var script = Assert.Single(_platform.StartedScripts);
        var files = File.ReadAllText(Path.ChangeExtension(script, null) + ".files.txt");
        Assert.Contains(Path.Combine(Data, "repowatch.db"), files, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_uninstall_is_reported_in_the_window_and_can_be_retried()
    {
        var (service, _, _) = Create();
        _platform.CleanupStarts = false;
        var viewModel = new UninstallViewModel(service);

        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.StartsWith("Uninstalling didn't finish", viewModel.Message, StringComparison.Ordinal);
        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.UninstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_successful_uninstall_keeps_the_button_disabled_until_the_app_quits()
    {
        var (service, _, _) = Create();
        var viewModel = new UninstallViewModel(service);

        await viewModel.UninstallCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsRunning);
        Assert.False(viewModel.UninstallCommand.CanExecute(null)); // no second cleanup
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

    [Fact]
    public void The_manifest_file_list_cannot_point_outside_the_folder()
    {
        var folder = Path.Combine(_root, "m");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "release.json"), """{"version":"0.2.0","files":["RepoWatch.exe","../../Windows/explorer.exe","C:/boot.ini"]}""");

        Assert.Equal(["RepoWatch.exe"], InstallInfo.ReadManifestFiles(folder));
    }
}
