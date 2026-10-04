using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Platform;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Platform.Startup;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.Updates;
using RepoWatch.GitHub;

namespace RepoWatch.Desktop.Uninstall;

/// <summary>What the user chose in the uninstall window.</summary>
/// <param name="KeepSettings">Keep only the settings and repository list (no sign-in, no cached repository data, no logs).</param>
/// <param name="OpenGitHubAccess">Open GitHub afterwards, where the user can revoke Repo Watch's access to their account.</param>
public sealed record UninstallOptions(bool KeepSettings, bool OpenGitHubAccess);

/// <summary>One thing the uninstall removes (or keeps), as shown to the user.</summary>
public sealed record UninstallItem(string Title, string Detail, bool Removed = true);

/// <summary>
/// Operating-system parts of an uninstall, kept behind an interface so the plan can be tested without
/// touching the real Credential Manager, registry or Start menu.
/// </summary>
public interface IUninstallPlatform
{
    /// <summary>Removes every stored Repo Watch sign-in (all accounts). Returns how many.</summary>
    int RemoveAllCredentials();

    /// <summary>Removes the sign-in of one account.</summary>
    void RemoveCredential(AccountKey account);

    /// <summary>Clears Repo Watch's notifications from the action center and its notification identity and settings.</summary>
    void RemoveNotificationIdentity();

    /// <summary>Desktop and Start menu shortcuts that start <paramref name="executable"/>.</summary>
    IReadOnlyList<string> FindShortcuts(string executable);

    /// <summary>Starts the cleanup script (hidden), which finishes after this process exits.</summary>
    bool StartCleanup(string script);
}

/// <summary>
/// Removes Repo Watch from this PC. Everything the app created is removed: the program folder (and the
/// folder kept from the last update), the data folder (settings, repository cache, logs, diagnostics,
/// downloaded updates), stored sign-ins, the start-at-login entry, the notification identity, and
/// shortcuts that start it. The user may keep their settings and repository list, nothing else.
/// <para>
/// A running program can't delete its own folder, so the last step is a small script in the temp folder
/// that waits for Repo Watch to exit, deletes the folders and then itself.
/// </para>
/// <para>
/// Safety: a copy built from source never deletes its program folder; shared parts (all sign-ins, the
/// notification identity) are removed only for the normal data folder, so a side-by-side copy with its
/// own data folder can't sign the main install out; the start-at-login entry is removed only if it
/// starts this copy.
/// </para>
/// </summary>
public sealed class UninstallService(
    AppPaths paths,
    InstallInfo install,
    AccountService accounts,
    SettingsService settings,
    IStartupRegistration startup,
    IUninstallPlatform platform,
    IExternalBrowser browser,
    GitHubEndpoints endpoints,
    ILogger<UninstallService> logger,
    string? mainDataDirectory = null,
    string? scriptDirectory = null)
{
    /// <summary>The app must quit now so the cleanup script can delete its folders.</summary>
    public event EventHandler? ExitRequested;

    private string Executable => Path.Combine(install.InstallDirectory, InstallInfo.ExecutableName);

    private string PreviousDirectory => install.InstallDirectory + ".previous";

    /// <summary>True for the normal data folder (not REPOWATCH_DATA_DIR), whose sign-ins and notification identity are the PC's.</summary>
    public bool IsMainInstall => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.DataDirectory)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(mainDataDirectory ?? AppPaths.DefaultDataDirectory)),
        StringComparison.OrdinalIgnoreCase);

    /// <summary>What uninstalling with <paramref name="keepSettings"/> removes and keeps, for the confirmation window.</summary>
    public IReadOnlyList<UninstallItem> Describe(bool keepSettings)
    {
        var items = new List<UninstallItem>();
        items.Add(install.IsPortableRelease
            ? new("Program folder", install.InstallDirectory)
            : new("Program folder (kept)", $"{install.InstallDirectory}: this copy was built from source, so its folder is left alone.", Removed: false));
        if (install.IsPortableRelease && Directory.Exists(PreviousDirectory))
        {
            items.Add(new("Previous version kept after an update", PreviousDirectory));
        }

        items.Add(keepSettings
            ? new("Repository cache, logs, diagnostics and downloaded updates", paths.DataDirectory)
            : new("Settings, repository list, repository cache, logs and diagnostics", paths.DataDirectory));
        if (keepSettings)
        {
            items.Add(new("Settings and repository list (kept)", Path.Combine(paths.DataDirectory, "repowatch.db") + ", without sign-in or cached repository data", Removed: false));
        }

        items.Add(new("Sign-in", IsMainInstall ? "Every Repo Watch entry in Windows Credential Manager" : "This copy's account in Windows Credential Manager"));
        if (startup.IsRegistered)
        {
            items.Add(new("Start at login", "The entry in Windows' startup apps"));
        }

        if (IsMainInstall)
        {
            items.Add(new("Notifications", "Repo Watch's notifications in the action center and its notification settings"));
        }

        foreach (var shortcut in platform.FindShortcuts(Executable))
        {
            items.Add(new("Shortcut", shortcut));
        }

        return items;
    }

    /// <summary>Removes everything (as described), starts the cleanup script and asks the app to quit.</summary>
    public async Task<string?> UninstallAsync(UninstallOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        logger.LogInformation("Uninstalling (keep settings: {Keep}, main install: {Main}, release copy: {Release})", options.KeepSettings, IsMainInstall, install.IsPortableRelease);

        // 1. Stop GitHub activity and forget the session; this also clears the active account's cache.
        var known = KnownAccounts();
        await accounts.SignOutAsync().ConfigureAwait(false);

        // 2. Stored sign-ins.
        if (IsMainInstall)
        {
            platform.RemoveAllCredentials();
        }
        else
        {
            foreach (var account in known)
            {
                platform.RemoveCredential(account);
            }
        }

        // 3. Start at login (only if it starts this copy), notifications, shortcuts.
        if (startup.IsRegistered)
        {
            startup.Set(false);
        }

        if (IsMainInstall)
        {
            platform.RemoveNotificationIdentity();
        }

        foreach (var shortcut in platform.FindShortcuts(Executable))
        {
            TryDelete(shortcut);
        }

        // 4. Keep only the settings, if asked: no cached repository content, no account sign-in.
        settings.UpdateApp(s => s with { ActiveAccount = null, OnboardingCompleted = options.KeepSettings && s.OnboardingCompleted });
        settings.Flush();
        if (options.KeepSettings)
        {
            KeepOnlySettings();
        }

        // 5. GitHub keeps Repo Watch's authorization until the user revokes it there.
        if (options.OpenGitHubAccess)
        {
            await browser.OpenAsync(endpoints.AuthorizationManagement).ConfigureAwait(false);
        }

        // 6. Folders: deleted by a script once this process has exited.
        var script = Path.Combine(scriptDirectory ?? Path.GetTempPath(), $"repowatch-uninstall-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script, BuildScript(options.KeepSettings), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!platform.StartCleanup(script))
        {
            TryDelete(script);
            logger.LogWarning("The uninstall cleanup couldn't be started");
            return $"Repo Watch's sign-in, settings entries and shortcuts were removed, but its folders couldn't be scheduled for deletion. Quit Repo Watch and delete {install.InstallDirectory} and {paths.DataDirectory} yourself.";
        }

        logger.LogInformation("Uninstall cleanup started; quitting");
        ExitRequested?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>
    /// The cleanup script: wait for this process to exit, delete the folders (retrying while files are
    /// briefly locked), then delete the script itself. Paths are quoted and '%' is escaped for cmd.
    /// </summary>
    public string BuildScript(bool keepSettings, int? waitForProcess = null)
    {
        var pid = (waitForProcess ?? Environment.ProcessId).ToString(CultureInfo.InvariantCulture);
        var folders = new List<string>();
        if (install.IsPortableRelease)
        {
            folders.Add(install.InstallDirectory);
            folders.Add(PreviousDirectory);
        }

        if (keepSettings)
        {
            folders.AddRange(new[] { "logs", "diagnostics", "updates" }.Select(f => Path.Combine(paths.DataDirectory, f)));
        }
        else
        {
            folders.Add(paths.DataDirectory);
        }

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("chcp 65001 >nul");
        script.AppendLine("rem Repo Watch uninstall: waits for Repo Watch to exit, removes its folders, then deletes this script.");
        script.AppendLine(":wait");
        script.AppendLine(CultureInfo.InvariantCulture, $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)");
        foreach (var folder in folders)
        {
            var quoted = Quote(folder);
            script.AppendLine(CultureInfo.InvariantCulture, $"for /l %%i in (1,1,10) do if exist {quoted} (rmdir /s /q {quoted} 2>nul & if exist {quoted} timeout /t 1 /nobreak >nul)");
        }

        if (keepSettings)
        {
            foreach (var file in new[] { "repowatch.db-wal", "repowatch.db-shm" })
            {
                script.AppendLine(CultureInfo.InvariantCulture, $"if exist {Quote(Path.Combine(paths.DataDirectory, file))} del /f /q {Quote(Path.Combine(paths.DataDirectory, file))}");
            }
        }

        script.AppendLine("(goto) 2>nul & del \"%~f0\"");
        return script.ToString();
    }

    private static string Quote(string path) => "\"" + Path.TrimEndingDirectorySeparator(path).Replace("%", "%%", StringComparison.Ordinal) + "\"";

    /// <summary>Accounts with settings in this data folder (their sign-ins belong to this copy).</summary>
    private List<AccountKey> KnownAccounts()
    {
        var accountsFound = new List<AccountKey>();
        if (settings.App.ActiveAccount is { } active)
        {
            accountsFound.Add(active);
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabaseFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT scope FROM settings WHERE scope LIKE 'account:%'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var parts = reader.GetString(0)["account:".Length..].Split('/');
                if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                {
                    try
                    {
                        accountsFound.Add(new AccountKey(parts[0], id));
                    }
                    catch (ArgumentException)
                    {
                        // Not an account key written by this app.
                    }
                }
            }
        }
        catch (SqliteException ex)
        {
            logger.LogDebug(ex, "No account list in the data folder");
        }

        return accountsFound.Distinct().ToList();
    }

    /// <summary>Empties the cached repository data and notification history and compacts the file, so none of it can be recovered.</summary>
    private void KeepOnlySettings()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabaseFile, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM repository_snapshots;
                DELETE FROM http_cache;
                DELETE FROM notification_history;
                DELETE FROM settings_backup;
                PRAGMA wal_checkpoint(TRUNCATE);
                VACUUM;
                """;
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Couldn't empty the cache while keeping settings");
        }
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't delete {File}: {Error}", file, ex.Message);
        }
    }
}
