using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace RepoWatch.Desktop.Platform.Startup;

/// <summary>Command-line arguments the OS passes when it starts Repo Watch.</summary>
public static class StartupArguments
{
    /// <summary>Started by the OS at sign-in (honors "Start minimized").</summary>
    public const string AtLogin = "--startup";
}

/// <summary>Registers Repo Watch to start when the user signs in to the OS. Opt-in; off unless the user turns it on.</summary>
public interface IStartupRegistration
{
    bool IsSupported { get; }

    /// <summary>True when the OS will start this executable at sign-in.</summary>
    bool IsRegistered { get; }

    /// <returns>False if the OS refused the change.</returns>
    bool Set(bool enabled);
}

/// <summary>Platforms without startup support yet (macOS/Linux arrive in Stage 12).</summary>
public sealed class UnsupportedStartupRegistration : IStartupRegistration
{
    public bool IsSupported => false;

    public bool IsRegistered => false;

    public bool Set(bool enabled) => !enabled;
}

/// <summary>
/// The per-user Run key (HKCU\Software\Microsoft\Windows\CurrentVersion\Run), which needs no admin
/// rights and appears in Task Manager's Startup apps, where the user can also turn it off. The entry
/// starts this executable with <see cref="StartupArguments.AtLogin"/>.
/// </summary>
[SupportedOSPlatform("windows")]
/// <param name="runKey">Registry key under HKCU; tests point it elsewhere so they never touch the real Run key.</param>
public sealed class WindowsStartupRegistration(string executablePath, string valueName = "RepoWatch", string runKey = WindowsStartupRegistration.RunKey)
    : IStartupRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsSupported => true;

    public string Command => $"\"{executablePath}\" {StartupArguments.AtLogin}";

    public bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey);
            return string.Equals(key?.GetValue(valueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(runKey);
            if (enabled)
            {
                key.SetValue(valueName, Command); // also repairs an entry pointing at an old location
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }
}
