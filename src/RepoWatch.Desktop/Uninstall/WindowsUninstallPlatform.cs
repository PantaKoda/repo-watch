using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RepoWatch.Core.Identity;
using RepoWatch.Desktop.Platform.Windows;

namespace RepoWatch.Desktop.Uninstall;

/// <summary>Uninstall steps on Windows: Credential Manager, notification registration, shortcuts and the cleanup script.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUninstallPlatform(ILogger<WindowsUninstallPlatform> logger) : IUninstallPlatform
{
    private const string AppUserModelId = "RepoWatch.Desktop";

    public int RemoveAllCredentials() => new WindowsCredentialStore().DeleteAll();

    public void RemoveCredential(AccountKey account)
    {
        try
        {
            new WindowsCredentialStore().DeleteAsync(account).GetAwaiter().GetResult();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            logger.LogWarning("Couldn't remove the sign-in for {Account}: {Error}", account, ex.Message);
        }
    }

    public void RemoveNotificationIdentity()
    {
#if WINDOWS
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            {
                Windows.UI.Notifications.ToastNotificationManager.History.Clear(AppUserModelId); // leaves nothing in the action center
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Clearing notification history failed");
        }
#endif

        // The app's notification identity, and the per-app settings Windows creates once it has shown a notification.
        DeleteKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
        DeleteKey($@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{AppUserModelId}");
    }

    public IReadOnlyList<string> FindShortcuts(string executable)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        var found = new List<string>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(folder, "*.lnk", folder.EndsWith("Programs", StringComparison.OrdinalIgnoreCase) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            found.AddRange(links.Where(link => ShortcutFiles.PointsTo(link, executable)));
        }

        return found;
    }

    public bool StartCleanup(string script)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(), // never inside a folder the script deletes
            };
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(script);
            using var process = System.Diagnostics.Process.Start(info);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Starting the uninstall cleanup failed");
            return false;
        }
    }

    private void DeleteKey(string key)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            logger.LogWarning("Couldn't remove HKCU\\{Key}: {Error}", key, ex.Message);
        }
    }
}

/// <summary>Reads shortcut files without the Windows shell, so it can be tested anywhere.</summary>
public static class ShortcutFiles
{
    /// <summary>
    /// A shortcut (.lnk) stores its target path; it starts this copy when the path appears in the file, as
    /// UTF-16 or as ANSI. Shortcuts to anything else are never touched.
    /// </summary>
    public static bool PointsTo(string link, string executable)
    {
        try
        {
            var bytes = File.ReadAllBytes(link);
            if (bytes.Length > 1_000_000)
            {
                return false;
            }

            return Encoding.Unicode.GetString(bytes).Contains(executable, StringComparison.OrdinalIgnoreCase)
                || Encoding.Latin1.GetString(bytes).Contains(executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Where Repo Watch has no OS integration to undo: nothing but the folders.</summary>
public sealed class BasicUninstallPlatform : IUninstallPlatform
{
    public int RemoveAllCredentials() => 0;

    public void RemoveCredential(AccountKey account)
    {
    }

    public void RemoveNotificationIdentity()
    {
    }

    public IReadOnlyList<string> FindShortcuts(string executable) => [];

    public bool StartCleanup(string script) => false;
}
