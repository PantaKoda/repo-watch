using Avalonia;
using Avalonia.Controls;

namespace RepoWatch.Desktop.Platform.Windowing;

/// <summary>
/// Raises <see cref="Changed"/> when something that affects the widget's material or motion may have
/// changed underneath the app: the achieved transparency level, high contrast, and on Windows the
/// system settings, theme, composition, power and display messages (transparency effects, animation
/// setting, battery saver, remote-session connects). Listeners re-read the state; this never decides.
/// </summary>
public sealed class SystemVisualsWatcher : IDisposable
{
    private const uint WmDisplayChange = 0x007E;
    private const uint WmSettingChange = 0x001A;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmThemeChanged = 0x031A;
    private const uint WmDwmCompositionChanged = 0x031E;

    private readonly Window _window;
    private readonly Avalonia.Platform.IPlatformSettings? _platformSettings;

    public SystemVisualsWatcher(Window window)
    {
        _window = window;
        _window.PropertyChanged += OnWindowPropertyChanged;
        _platformSettings = Application.Current?.PlatformSettings;
        if (_platformSettings is not null)
        {
            _platformSettings.ColorValuesChanged += OnColorValuesChanged;
        }

        if (OperatingSystem.IsWindows())
        {
            Win32Properties.AddWndProcHookCallback(window, WndProc);
        }
    }

    public event EventHandler? Changed;

    public void Dispose()
    {
        _window.PropertyChanged -= OnWindowPropertyChanged;
        if (_platformSettings is not null)
        {
            _platformSettings.ColorValuesChanged -= OnColorValuesChanged;
        }

        if (OperatingSystem.IsWindows())
        {
            Win32Properties.RemoveWndProcHookCallback(_window, WndProc);
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ActualTransparencyLevelProperty
            || e.Property == Visual.IsVisibleProperty
            || e.Property == Window.WindowStateProperty)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnColorValuesChanged(object? sender, Avalonia.Platform.PlatformColorValues e) => Changed?.Invoke(this, EventArgs.Empty);

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmSettingChange or WmThemeChanged or WmDwmCompositionChanged or WmPowerBroadcast or WmDisplayChange)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }
}
