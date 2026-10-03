using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace RepoWatch.Desktop.Platform.Windows;

/// <summary>
/// The system-wide Show/Hide shortcut (Ctrl+Alt+R) on Windows, via RegisterHotKey on the widget's window
/// (which keeps its handle while hidden). Registration fails when another app owns the combination; the
/// tray icon and the taskbar stay as alternatives, and Settings says which applies.
/// </summary>
public sealed class GlobalShortcut : IDisposable
{
    public const string Description = "Ctrl+Alt+R";
    private const int HotkeyId = 0x5257; // "RW"
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyR = 0x52;
    private const uint WmHotkey = 0x0312;

    private readonly Window _window;
    private readonly Action _pressed;
    private IntPtr _handle;
    private bool _hooked;

    public GlobalShortcut(Window window, Action pressed)
    {
        _window = window;
        _pressed = pressed;
    }

    public bool IsRegistered { get; private set; }

    /// <summary>Registers or unregisters the shortcut. Returns whether it is now registered as asked.</summary>
    public bool Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return !enabled;
        }

        if (_handle == IntPtr.Zero && _window.TryGetPlatformHandle() is { } platform)
        {
            _handle = platform.Handle;
        }

        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        if (!_hooked)
        {
            Win32Properties.AddWndProcHookCallback(_window, WndProc);
            _hooked = true;
        }

        if (enabled && !IsRegistered)
        {
            IsRegistered = RegisterHotKey(_handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VirtualKeyR);
        }
        else if (!enabled && IsRegistered)
        {
            UnregisterHotKey(_handle, HotkeyId);
            IsRegistered = false;
        }

        return IsRegistered == enabled;
    }

    public void Dispose()
    {
        if (IsRegistered)
        {
            UnregisterHotKey(_handle, HotkeyId);
            IsRegistered = false;
        }

        if (_hooked && OperatingSystem.IsWindows())
        {
            Win32Properties.RemoveWndProcHookCallback(_window, WndProc);
            _hooked = false;
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == HotkeyId)
        {
            handled = true;
            _pressed();
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
