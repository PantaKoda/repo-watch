using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
/// <param name="KeepSettings">Keep only the settings (no sign-in, no cached repository data, no logs).</param>
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

/// <summary>Exactly which files and folders the cleanup script may delete.</summary>
/// <param name="Files">Files to delete (absolute paths).</param>
/// <param name="EmptyFolders">Folders to remove only if empty afterwards, deepest first (never recursive).</param>
/// <param name="OwnedFolders">Folders Repo Watch owns outright (the default data folder), removed with everything in them.</param>
/// <param name="KeptForeign">Files found in Repo Watch's folders that it didn't create; they are left in place.</param>
/// <param name="ProgramFolderKept">Why the program folder is left alone, or null when its files are removed.</param>
public sealed record UninstallPlan(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> EmptyFolders,
    IReadOnlyList<string> OwnedFolders,
    IReadOnlyList<string> KeptForeign,
    string? ProgramFolderKept);

/// <summary>
/// Removes Repo Watch from this PC: its program files, the version kept after an update, its data (settings,
/// repository cache, logs, diagnostics, downloaded updates), stored sign-ins, the start-at-login entry, the
/// notification identity and shortcuts that start it. The user may keep their settings, nothing else.
/// <para>
/// Only what Repo Watch created is deleted. Program files are deleted by the list each release ships in
/// <c>release.json</c>, so files that share the folder survive; a custom data folder (REPOWATCH_DATA_DIR) loses
/// only Repo Watch's own files; folders are removed only once empty. Only the default data folder, which Repo
/// Watch owns, is removed outright.
/// </para>
/// <para>
/// A running program can't delete its own files, so the last step is a script in the temp folder that waits
/// for Repo Watch to exit (system tools by full path, so tools earlier on PATH can't change its meaning),
/// deletes the files, then itself.
/// </para>
/// </summary>
public sealed partial class UninstallService(
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
    private const string PreviousSuffix = ".previous";
    private static readonly string[] DatabaseFiles = ["repowatch.db", "repowatch.db-wal", "repowatch.db-shm"];

    /// <summary>The app must quit now so the cleanup script can delete its files.</summary>
    public event EventHandler? ExitRequested;

    private string Executable => Path.Combine(install.InstallDirectory, InstallInfo.ExecutableName);

    /// <summary>True for the normal data folder (not REPOWATCH_DATA_DIR), whose sign-ins and notification identity are the PC's.</summary>
    public bool IsMainInstall => SamePath(paths.DataDirectory, mainDataDirectory ?? AppPaths.DefaultDataDirectory);

    /// <summary>The program folders: the running one and its partner (after an update, "X" and "X.previous").</summary>
    private IEnumerable<string> ProgramFolders()
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(install.InstallDirectory));
        yield return current;
        yield return current.EndsWith(PreviousSuffix, StringComparison.OrdinalIgnoreCase)
            ? current[..^PreviousSuffix.Length] // running from the backup after a failed update: the half-copied folder too
            : current + PreviousSuffix;
    }

    /// <summary>What uninstalling with <paramref name="keepSettings"/> deletes, built from what is on disk now.</summary>
    public UninstallPlan Plan(bool keepSettings)
    {
        var files = new List<string>();
        var folders = new List<string>();
        var owned = new List<string>();
        var foreign = new List<string>();
        string? programKept = null;

        // Program files: only those a release shipped. Lists from this copy and the partner folder are combined
        // (an older version's files are known by its own list, or by this one when they match).
        var shippedLists = ProgramFolders().Select(InstallInfo.ReadManifestFiles).ToList();
        if (!install.IsPortableRelease)
        {
            programKept = "this copy was built from source, so its folder is left alone";
        }
        else if (shippedLists[0] is null)
        {
            programKept = "its release information has no file list (it was made by an older version), so its folder is left for you to delete";
        }
        else
        {
            var shipped = shippedLists.Where(l => l is not null).SelectMany(l => l!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in ProgramFolders().Where(Directory.Exists))
            {
                AddKnownFiles(folder, shipped, files, folders, foreign);
            }
        }

        // Data.
        if (IsMainInstall && !keepSettings)
        {
            owned.Add(paths.DataDirectory); // %LOCALAPPDATA%\RepoWatch: Repo Watch's own folder
        }
        else
        {
            AddDataFiles(keepSettings, files, folders, foreign);
        }

        return new UninstallPlan(
            files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            folders.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.Count(c => c == Path.DirectorySeparatorChar)).ToList(),
            owned,
            foreign.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            programKept);
    }

    /// <summary>What uninstalling with <paramref name="keepSettings"/> removes and keeps, for the confirmation window.</summary>
    public IReadOnlyList<UninstallItem> Describe(bool keepSettings)
    {
        var plan = Plan(keepSettings);
        var partner = ProgramFolders().Last();
        var items = new List<UninstallItem>();
        items.Add(plan.ProgramFolderKept is { } reason
            ? new("Program folder (kept)", $"{install.InstallDirectory}: {reason}.", Removed: false)
            : new("Program files", install.InstallDirectory + (Directory.Exists(partner) ? $", and the other version in {partner}" : "")));

        items.Add(keepSettings
            ? new("Repository cache, logs, diagnostics and downloaded updates", paths.DataDirectory)
            : new("Settings, repository list, repository cache, logs and diagnostics", paths.DataDirectory + (IsMainInstall ? "" : " (only Repo Watch's own files in it)")));
        if (keepSettings)
        {
            items.Add(new("Settings and repository list (kept)", $"{paths.DatabaseFile} and {paths.UserConfigFile} if present, without sign-in or cached repository data", Removed: false));
        }

        if (plan.KeptForeign.Count > 0)
        {
            items.Add(new($"Other files (kept): {plan.KeptForeign.Count}", "Files in these folders that Repo Watch didn't create, e.g. " + plan.KeptForeign[0], Removed: false));
        }

        items.Add(new("Sign-in", IsMainInstall
            ? "Every Repo Watch entry in Windows Credential Manager"
            : "The sign-in of the accounts this copy used, in Windows Credential Manager. Other copies signed in to the same account are signed out too."));
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

    /// <summary>
    /// Removes everything (as described), starts the cleanup script and asks the app to quit. Returns a message
    /// for the user when something was left in place, or null when all went to plan.
    /// </summary>
    public async Task<string?> UninstallAsync(UninstallOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        logger.LogInformation("Uninstalling (keep settings: {Keep}, main install: {Main}, release copy: {Release})", options.KeepSettings, IsMainInstall, install.IsPortableRelease);
        var notes = new List<string>();

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

        // 4. Keep only the settings, if asked. If the cache can't be emptied safely, the settings go too:
        // private repository content is never left behind.
        settings.UpdateApp(s => s with { ActiveAccount = null, OnboardingCompleted = options.KeepSettings && s.OnboardingCompleted });
        settings.Flush();
        var keepSettings = options.KeepSettings;
        if (keepSettings && !KeepOnlySettings())
        {
            keepSettings = false;
            notes.Add("Your settings couldn't be separated from cached repository data, so they were removed as well.");
        }

        // 5. GitHub keeps Repo Watch's authorization until the user revokes it there.
        if (options.OpenGitHubAccess)
        {
            await browser.OpenAsync(endpoints.AuthorizationManagement).ConfigureAwait(false);
        }

        // 6. Files: deleted by a script once this process has exited.
        var plan = Plan(keepSettings);
        if (plan.ProgramFolderKept is { } kept)
        {
            notes.Add($"The program folder {install.InstallDirectory} was left in place: {kept}.");
        }

        if (plan.KeptForeign.Count > 0)
        {
            notes.Add($"{plan.KeptForeign.Count} file(s) in Repo Watch's folders weren't created by it and were left in place.");
        }

        var directory = scriptDirectory ?? Path.GetTempPath();
        var listBase = Path.Combine(directory, $"repowatch-uninstall-{Guid.NewGuid():N}");
        var script = listBase + ".cmd";
        File.WriteAllText(script, BuildScript(plan, listBase, Environment.ProcessId), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!platform.StartCleanup(script))
        {
            DeleteScript(listBase);
            logger.LogWarning("The uninstall cleanup couldn't be started");
            throw new IOException($"the cleanup couldn't be started. Quit Repo Watch and delete {install.InstallDirectory} and {paths.DataDirectory} yourself");
        }

        logger.LogInformation("Uninstall cleanup started; quitting");
        ExitRequested?.Invoke(this, EventArgs.Empty);
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    /// <summary>
    /// The cleanup script. It writes the file and folder lists next to itself (so no listed path needs cmd
    /// escaping), waits for <paramref name="waitForProcess"/> to exit, deletes the listed files (retrying while
    /// they are still locked), removes the listed folders only if empty, removes owned folders, then deletes the
    /// lists and itself. System tools are called by full path, so other tools on PATH can't change the meaning.
    /// </summary>
    public static string BuildScript(UninstallPlan plan, string listBase, int waitForProcess)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var pid = waitForProcess.ToString(CultureInfo.InvariantCulture);
        var fileList = listBase + ".files.txt";
        var folderList = listBase + ".folders.txt";
        File.WriteAllLines(fileList, plan.Files, new UTF8Encoding(false));
        File.WriteAllLines(folderList, plan.EmptyFolders, new UTF8Encoding(false));

        const string Tasklist = "\"%SystemRoot%\\System32\\tasklist.exe\"";
        const string Find = "\"%SystemRoot%\\System32\\find.exe\"";
        const string Timeout = "\"%SystemRoot%\\System32\\timeout.exe\"";
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("chcp 65001 >nul");
        script.AppendLine("rem Repo Watch uninstall: waits for Repo Watch to exit, deletes its files, then this script.");
        script.AppendLine(":wait");
        script.AppendLine(CultureInfo.InvariantCulture, $"{Tasklist} /FI \"PID eq {pid}\" /NH 2>nul | {Find} \" {pid} \" >nul && ({Timeout} /t 1 /nobreak >nul & goto wait)");
        script.AppendLine("set /a tries=0");
        script.AppendLine(":files");
        script.AppendLine(CultureInfo.InvariantCulture, $"for /f \"usebackq delims=\" %%f in ({Quote(fileList)}) do if exist \"%%f\" del /f /q \"%%f\" 2>nul");
        script.AppendLine(CultureInfo.InvariantCulture, $"for /f \"usebackq delims=\" %%f in ({Quote(fileList)}) do if exist \"%%f\" if %tries% lss 10 (set /a tries+=1 & {Timeout} /t 1 /nobreak >nul & goto files)");
        script.AppendLine(CultureInfo.InvariantCulture, $"for /f \"usebackq delims=\" %%d in ({Quote(folderList)}) do rmdir \"%%d\" 2>nul");
        foreach (var folder in plan.OwnedFolders)
        {
            var quoted = Quote(folder);
            script.AppendLine(CultureInfo.InvariantCulture, $"for /l %%i in (1,1,10) do if exist {quoted} (rmdir /s /q {quoted} 2>nul & if exist {quoted} {Timeout} /t 1 /nobreak >nul)");
        }

        script.AppendLine(CultureInfo.InvariantCulture, $"del /f /q {Quote(fileList)} {Quote(folderList)} 2>nul");
        script.AppendLine("(goto) 2>nul & del \"%~f0\"");
        return script.ToString();
    }

    private static string Quote(string path) => "\"" + Path.TrimEndingDirectorySeparator(path).Replace("%", "%%", StringComparison.Ordinal) + "\"";

    private static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds the shipped files present in <paramref name="folder"/>; anything else found there is reported as foreign.</summary>
    private static void AddKnownFiles(string folder, HashSet<string> shipped, List<string> files, List<string> folders, List<string> foreign)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            (shipped.Contains(Path.GetRelativePath(folder, file)) ? files : foreign).Add(file);
        }

        folders.AddRange(Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories));
        folders.Add(folder);
    }

    /// <summary>Repo Watch's own entries in the data folder; the folders are removed only if nothing else is in them.</summary>
    private void AddDataFiles(bool keepSettings, List<string> files, List<string> folders, List<string> foreign)
    {
        var data = paths.DataDirectory;
        if (!Directory.Exists(data))
        {
            return;
        }

        if (!keepSettings)
        {
            files.AddRange(DatabaseFiles.Select(f => Path.Combine(data, f)).Where(File.Exists));
            if (File.Exists(paths.UserConfigFile))
            {
                files.Add(paths.UserConfigFile);
            }

            folders.Add(data);
        }
        else
        {
            files.AddRange(DatabaseFiles.Skip(1).Select(f => Path.Combine(data, f)).Where(File.Exists)); // emptied by the checkpoint
        }

        // Anything else at the top of a shared data folder isn't Repo Watch's: it stays, and the user is told.
        var ours = new HashSet<string>(DatabaseFiles.Append(Path.GetFileName(paths.UserConfigFile)).Concat(["logs", "diagnostics", "updates"]), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(data).Where(e => !ours.Contains(Path.GetFileName(e))))
        {
            foreign.Add(entry);
        }

        AddMatching(Path.Combine(data, "logs"), name => LogFile().IsMatch(name), files, folders, foreign);
        AddMatching(Path.Combine(data, "diagnostics"), name => DiagnosticsFile().IsMatch(name), files, folders, foreign);
        var updates = Path.Combine(data, "updates");
        if (Directory.Exists(updates))
        {
            foreach (var file in Directory.EnumerateFiles(updates, "*", SearchOption.AllDirectories))
            {
                var top = Path.GetRelativePath(updates, file).Split(Path.DirectorySeparatorChar)[0];
                (UpdateEntry().IsMatch(top) ? files : foreign).Add(file);
            }

            folders.AddRange(Directory.EnumerateDirectories(updates, "*", SearchOption.AllDirectories));
            folders.Add(updates);
        }
    }

    private static void AddMatching(string folder, Func<string, bool> ours, List<string> files, List<string> folders, List<string> foreign)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            (ours(Path.GetFileName(file)) && SamePath(Path.GetDirectoryName(file)!, folder) ? files : foreign).Add(file);
        }

        folders.AddRange(Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories));
        folders.Add(folder);
    }

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

    /// <summary>
    /// Empties the cached repository data and notification history so none of it can be recovered: secure delete
    /// overwrites the freed pages, and the file is compacted. False when that didn't fully succeed.
    /// </summary>
    private bool KeepOnlySettings()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabaseFile, Pooling = false }.ToString());
            connection.Open();
            // One statement per command: a multi-statement command can stop at a failure without reporting it.
            foreach (var statement in new[]
            {
                "PRAGMA busy_timeout = 5000",
                "PRAGMA secure_delete = ON",
                "DELETE FROM repository_snapshots",
                "DELETE FROM http_cache",
                "DELETE FROM notification_history",
                "DELETE FROM settings_backup",
                "PRAGMA wal_checkpoint(TRUNCATE)",
                "VACUUM",
            })
            {
                using var command = connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }

            // Verify rather than assume.
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT (SELECT COUNT(*) FROM repository_snapshots) + (SELECT COUNT(*) FROM http_cache) + (SELECT COUNT(*) FROM notification_history)";
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                logger.LogWarning("Cached data remained after emptying; removing the settings too");
                return false;
            }

            return true;
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Couldn't empty the cache while keeping settings; removing the settings too");
            return false;
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

    private void DeleteScript(string listBase)
    {
        TryDelete(listBase + ".cmd");
        TryDelete(listBase + ".files.txt");
        TryDelete(listBase + ".folders.txt");
    }

    [GeneratedRegex(@"^(repowatch-\d{8}\.log|update\.log)$", RegexOptions.IgnoreCase)]
    private static partial Regex LogFile();

    [GeneratedRegex(@"^repowatch-diagnostics-\d{8}-\d{6}\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticsFile();

    [GeneratedRegex(@"^(RepoWatch-\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?-win-x64\.zip(\.sha256)?|\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?)$")]
    private static partial Regex UpdateEntry();
}
