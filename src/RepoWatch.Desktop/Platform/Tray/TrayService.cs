using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace RepoWatch.Desktop.Platform.Tray;

/// <summary>
/// Notification-area icon with Show, Settings and Quit. Treated as available only where the
/// platform reliably provides one; elsewhere the widget stays in the taskbar instead.
/// </summary>
public sealed class TrayService(ILogger<TrayService> logger) : IDisposable
{
    private TrayIcon? _icon;

    public bool IsAvailable { get; private set; }

    public void Initialize(Application application, WindowIcon icon, Action toggleWidget, Action showWidget, Action openSettings, Action quit)
    {
        ArgumentNullException.ThrowIfNull(application);

        // Linux tray support depends on the desktop environment and cannot be detected reliably yet.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            logger.LogInformation("No supported tray on this platform; the widget stays in the taskbar");
            return;
        }

        try
        {
            var menu = new NativeMenu();
            menu.Items.Add(Item("Show widget", showWidget));
            menu.Items.Add(Item("Settings…", openSettings));
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(Item("Quit Repo Watch", quit));

            _icon = new TrayIcon { Icon = icon, ToolTipText = "Repo Watch", Menu = menu, IsVisible = true };
            _icon.Clicked += (_, _) => toggleWidget();
            TrayIcon.SetIcons(application, [_icon]);
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Creating the tray icon failed; the widget stays in the taskbar");
            IsAvailable = false;
        }
    }

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.IsVisible = false;
            _icon.Dispose();
        }
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }
}
