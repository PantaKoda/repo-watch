using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Desktop.Demo;

namespace RepoWatch.Desktop.Services;

/// <summary>
/// Chooses which monitor the UI observes: the account's monitor (the "base"), or labeled demo data
/// laid over it. Demo data is used only after an explicit request (the --demo argument or the
/// "Explore demo data" action); leaving demo mode returns to the account's monitor.
/// </summary>
public sealed class MonitorHost : IDisposable
{
    private readonly TimeProvider _time;
    private IRepositoryMonitor _base = new StatusOnlyMonitor(ConnectionState.NotSignedIn);
    private DemoRepositoryMonitor? _demo;

    public MonitorHost(TimeProvider time)
    {
        _time = time;
    }

    public IRepositoryMonitor Current => _demo ?? _base;

    public bool IsDemo => _demo is not null;

    /// <summary>Raised on the UI thread when <see cref="Current"/> is replaced.</summary>
    public event EventHandler? CurrentChanged;

    public void EnterDemo()
    {
        if (_demo is null)
        {
            _demo = new DemoRepositoryMonitor(_time);
            CurrentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ExitDemo()
    {
        if (_demo is not null)
        {
            _demo.Dispose();
            _demo = null;
            CurrentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Replaces the account's monitor, disposing the previous one. Call on the UI thread.</summary>
    public void SetBase(IRepositoryMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var previous = _base;
        _base = monitor;
        (previous as IDisposable)?.Dispose();
        if (_demo is null)
        {
            CurrentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _demo?.Dispose();
        (_base as IDisposable)?.Dispose();
    }
}

/// <summary>
/// Reports an account state without monitoring anything: signed out, reconnect required, or
/// signed in before repositories are chosen. Makes no requests.
/// </summary>
public sealed class StatusOnlyMonitor(ConnectionState state) : IRepositoryMonitor
{
    public ConnectionState State { get; } = state;

    public IReadOnlyList<MonitoredRepository> Repositories => [];

    public bool IsRefreshing => false;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public Task RefreshAsync(RepositoryKey? repository = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
