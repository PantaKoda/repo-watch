#if WINDOWS
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RepoWatch.Core.Platform;
using RepoWatch.Desktop.Platform.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace RepoWatch.Desktop.Platform.Windows;

/// <summary>
/// Windows toast notifications for the unpackaged app. Windows needs an app identity for toasts:
/// Repo Watch registers its AppUserModelID with a display name under HKCU (per user, no admin rights)
/// and uses it for this process. Toasts respect the OS settings (Do not disturb, per-app switches).
/// Clicking a toast while Repo Watch runs opens its GitHub page; nothing takes focus before the click.
/// </summary>
public sealed class WindowsToastSink : INotificationSink
{
    public const string AppUserModelId = "RepoWatch.Desktop";

    private readonly IExternalBrowser _browser;
    private readonly ILogger<WindowsToastSink> _logger;
    private readonly ToastNotifier? _notifier;

    public WindowsToastSink(IExternalBrowser browser, ILogger<WindowsToastSink> logger)
    {
        _browser = browser;
        _logger = logger;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}"))
            {
                key.SetValue("DisplayName", "Repo Watch");
            }

            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            _notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or SecurityException or IOException)
        {
            _logger.LogWarning("Windows notifications are unavailable ({Error})", ex.GetType().Name);
        }
    }

    public NotificationAvailability Availability
    {
        get
        {
            if (_notifier is null)
            {
                return NotificationAvailability.Unsupported;
            }

            try
            {
                return _notifier.Setting switch
                {
                    NotificationSetting.Enabled => NotificationAvailability.Available,
                    NotificationSetting.DisabledByGroupPolicy or NotificationSetting.DisabledByManifest => NotificationAvailability.DisabledByPolicy,
                    _ => NotificationAvailability.DisabledByUser,
                };
            }
            catch (COMException)
            {
                // Setting isn't readable for some unpackaged identities; assume enabled and let Show report refusals.
                return NotificationAvailability.Available;
            }
        }
    }

    public bool Show(DesktopNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (_notifier is null || Availability != NotificationAvailability.Available)
        {
            return false;
        }

        try
        {
            var xml = new XmlDocument();
            var toast = xml.CreateElement("toast");
            xml.AppendChild(toast);
            var visual = xml.CreateElement("visual");
            toast.AppendChild(visual);
            var binding = xml.CreateElement("binding");
            binding.SetAttribute("template", "ToastGeneric");
            visual.AppendChild(binding);
            foreach (var line in new[] { notification.Title, notification.Body })
            {
                var text = xml.CreateElement("text");
                text.InnerText = line; // plain text: repository content is never interpreted as markup
                binding.AppendChild(text);
            }

            var shown = new ToastNotification(xml) { Tag = Shorten(notification.Tag), Group = "repowatch" };
            if (notification.Url is { } url)
            {
                shown.Activated += (_, _) => _ = _browser.OpenAsync(url);
            }

            _notifier.Show(shown);
            return true;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            _logger.LogWarning("Windows refused a notification ({Error})", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Toast tags are limited to 64 characters.</summary>
    private static string Shorten(string tag) =>
        tag.Length <= 64 ? tag : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tag)))[..64];

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
#endif
