using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using RepoWatch.Core.Settings;

namespace RepoWatch.Desktop.Services;

/// <summary>
/// What the scheduler should take into account besides GitHub's own answers: the user's Pause
/// monitoring switch, whether the widget is on screen, whether the computer runs on battery, and
/// moments when data is likely stale (wake from sleep, network back). Raised events may come from
/// any thread.
/// </summary>
public sealed class PollingConditions : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>A gap this much longer than the check interval means the computer was asleep.</summary>
    private static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(90);

    private readonly SettingsService _settings;
    private readonly TimeProvider _time;
    private readonly Func<bool> _onBattery;
    private readonly ITimer? _timer;
    private DateTimeOffset _lastCheck;
    private bool _widgetVisible = true;
    private bool _batteryPower;

    /// <param name="onBattery">Battery check; defaults to the platform's power status where available.</param>
    /// <param name="watchSystem">False in tests: no timer and no network notifications.</param>
    public PollingConditions(SettingsService settings, TimeProvider time, Func<bool>? onBattery = null, bool watchSystem = true)
    {
        _settings = settings;
        _time = time;
        _onBattery = onBattery ?? PowerStatus.IsOnBattery;
        _batteryPower = SafeBattery();
        _lastCheck = time.GetUtcNow();
        _settings.AppChanged += OnSettingsChanged;
        if (watchSystem)
        {
            _timer = time.CreateTimer(_ => Check(), null, CheckInterval, CheckInterval);
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        }
    }

    public bool IsPaused => _settings.App.MonitoringPaused;

    public bool IsWidgetVisible => _widgetVisible;

    public bool IsOnBattery => _batteryPower;

    /// <summary>Pause switched, visibility or power changed: intervals or the paused state may differ.</summary>
    public event EventHandler? Changed;

    /// <summary>The computer woke up or the network came back: refresh soon and forget earlier failures.</summary>
    public event EventHandler? Resumed;

    /// <summary>The user brought the widget to the front: data older than a few seconds should refresh now.</summary>
    public event EventHandler? WidgetActivated;

    public void NotifyWidgetActivated() => WidgetActivated?.Invoke(this, EventArgs.Empty);

    public void SetPaused(bool paused) => _settings.UpdateApp(s => s with { MonitoringPaused = paused });

    public void SetWidgetVisible(bool visible)
    {
        if (_widgetVisible != visible)
        {
            _widgetVisible = visible;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Detects a sleep gap and power changes. Called by the timer; public for tests.</summary>
    public void Check()
    {
        var now = _time.GetUtcNow();
        var gap = now - _lastCheck;
        _lastCheck = now;
        if (gap > CheckInterval + SleepGap)
        {
            Resumed?.Invoke(this, EventArgs.Empty);
        }

        var battery = SafeBattery();
        if (battery != _batteryPower)
        {
            _batteryPower = battery;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised by the network watcher; public for tests.</summary>
    public void NotifyNetworkAvailable() => Resumed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _settings.AppChanged -= OnSettingsChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _timer?.Dispose();
    }

    private void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
    {
        if (e.Previous.MonitoringPaused != e.Current.MonitoringPaused)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            NotifyNetworkAvailable();
        }
    }

    private bool SafeBattery()
    {
        try
        {
            return _onBattery();
        }
        catch (Exception ex) when (ex is ExternalException or EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }
}

/// <summary>Platform power status. Where it can't be read, the computer is treated as plugged in.</summary>
internal static class PowerStatus
{
    public static bool IsOnBattery() => OperatingSystem.IsWindows() && GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
