using RepoWatch.Desktop.Platform.Notifications;
using RepoWatch.Desktop.Platform.Startup;

namespace RepoWatch.Desktop.Services;

/// <summary>
/// What the OS integration can do on this machine, for Settings: startup registration, notifications,
/// the Show/Hide shortcut and diagnostics. Each part reports when it is unavailable instead of failing.
/// </summary>
public sealed class DesktopIntegration(IStartupRegistration startup, NotificationService? notifications = null, DiagnosticsService? diagnostics = null)
{
    public IStartupRegistration Startup { get; } = startup;

    public NotificationAvailability Notifications => notifications?.Availability ?? NotificationAvailability.Unsupported;

    /// <summary>Set by the shell after registering the shortcut: what the user can expect.</summary>
    public string ShortcutStatus { get; private set; } = "The Show/Hide shortcut isn't available on this system; use the tray icon.";

    public bool CanExportDiagnostics => diagnostics is not null;

    /// <summary>Raised (any thread) when a status changes.</summary>
    public event EventHandler? Changed;

    public void SetShortcutStatus(string status)
    {
        ShortcutStatus = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool ShowTestNotification() => notifications?.ShowTest() ?? false;

    /// <returns>The archive path, or null if diagnostics aren't available.</returns>
    public string? ExportDiagnostics() => diagnostics?.Export();
}
