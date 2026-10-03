using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Desktop.Demo;

namespace RepoWatch.Desktop.Services;

/// <summary>
/// Chooses which monitor the UI observes. Demo data is used only after an explicit request
/// (the --demo argument or the "Explore demo data" action) and is always labeled.
/// </summary>
public sealed class MonitorHost : IDisposable
{
    private readonly TimeProvider _time;

    public MonitorHost(TimeProvider time)
    {
        _time = time;
        Current = new SignedOutMonitor();
    }

    public IRepositoryMonitor Current { get; private set; }

    public bool IsDemo => Current is DemoRepositoryMonitor;

    /// <summary>Raised on the UI thread when <see cref="Current"/> is replaced.</summary>
    public event EventHandler? CurrentChanged;

    public void EnterDemo()
    {
        if (!IsDemo)
        {
            Use(new DemoRepositoryMonitor(_time));
        }
    }

    public void ExitDemo()
    {
        if (IsDemo)
        {
            Use(new SignedOutMonitor());
        }
    }

    public void Dispose() => (Current as IDisposable)?.Dispose();

    /// <summary>Switches the UI to <paramref name="monitor"/>, disposing the previous one. Call on the UI thread.</summary>
    public void Use(IRepositoryMonitor monitor)
    {
        (Current as IDisposable)?.Dispose();
        Current = monitor;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>No account: nothing is monitored and no requests are made.</summary>
public sealed class SignedOutMonitor : IRepositoryMonitor
{
    public ConnectionState State => ConnectionState.NotSignedIn;

    public IReadOnlyList<MonitoredRepository> Repositories => [];

    public bool IsRefreshing => false;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public Task RefreshAsync(RepositoryKey? repository = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
