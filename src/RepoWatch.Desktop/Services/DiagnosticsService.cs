using System.Globalization;
using System.IO.Compression;
using System.Text;
using RepoWatch.Core.Monitoring;
using RepoWatch.Desktop.Infrastructure;

namespace RepoWatch.Desktop.Services;

/// <summary>
/// Writes a redacted diagnostics archive for bug reports: versions, OS, connection and account state,
/// counts, and the rotated log files. Excluded: tokens and device codes (never stored; redacted again
/// here), settings documents, cached repository data, issue and pull request titles and bodies, and
/// repository names (counts only).
/// </summary>
public sealed class DiagnosticsService(AppPaths paths, AccountService accounts, MonitorHost monitors, NotificationService notifications,
    PollingConditions conditions, TimeProvider time)
{
    public string Export()
    {
        var now = time.GetLocalNow();
        var directory = Path.Combine(paths.DataDirectory, "diagnostics");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"repowatch-diagnostics-{now:yyyyMMdd-HHmmss}.zip");
        using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
        Write(archive, "summary.txt", Summary(now));

        if (Directory.Exists(paths.LogDirectory))
        {
            foreach (var log in Directory.GetFiles(paths.LogDirectory, "*.log").OrderBy(f => f, StringComparer.Ordinal))
            {
                string text;
                try
                {
                    using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    text = reader.ReadToEnd();
                }
                catch (IOException)
                {
                    continue;
                }

                Write(archive, "logs/" + Path.GetFileName(log), Redactor.Redact(text));
            }
        }

        return file;
    }

    private string Summary(DateTimeOffset now)
    {
        var monitor = monitors.Current;
        var repositories = monitor.Repositories;
        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Repo Watch {AppInfo.Version}")
            .AppendLine(CultureInfo.InvariantCulture, $"Created: {now:O}")
            .AppendLine(CultureInfo.InvariantCulture, $"OS: {Environment.OSVersion}; .NET {Environment.Version}; 64-bit process: {Environment.Is64BitProcess}")
            .AppendLine(CultureInfo.InvariantCulture, $"Account: {accounts.State}; credential storage: {accounts.CredentialStorage}; persistent: {accounts.CredentialsArePersistent}")
            .AppendLine(CultureInfo.InvariantCulture, $"Connection: {monitor.State}; demo: {monitors.IsDemo}; refreshing: {monitor.IsRefreshing}")
            .AppendLine(CultureInfo.InvariantCulture, $"Monitoring paused: {conditions.IsPaused}; widget visible: {conditions.IsWidgetVisible}; on battery: {conditions.IsOnBattery}")
            .AppendLine(CultureInfo.InvariantCulture, $"Notifications: {notifications.Availability}")
            .AppendLine(CultureInfo.InvariantCulture, $"Watched repositories: {repositories.Count}");
        foreach (var group in repositories.GroupBy(r => AttentionPolicy.Evaluate(r.Snapshot)).OrderBy(g => g.Key))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {group.Key}: {group.Count()}");
        }

        return Redactor.Redact(text.ToString());
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
