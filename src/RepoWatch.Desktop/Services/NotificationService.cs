using Microsoft.Extensions.Logging;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Notifications;
using RepoWatch.Core.State;
using RepoWatch.Desktop.Platform.Notifications;
using RepoWatch.Desktop.Storage;

namespace RepoWatch.Desktop.Services;

/// <summary>
/// Turns repository changes into notifications. Every event is recorded once in the account's history
/// (SQLite), with what happened to it: shown, part of the silent baseline, switched off, or quiet hours.
/// So an event is never announced twice, and nothing suppressed earlier bursts out later (after a
/// restart, after leaving quiet hours, or after switching a notification type on). Demo data never notifies.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly MonitorHost _monitors;
    private readonly SettingsService _settings;
    private readonly AccountService _accounts;
    private readonly RepositoryCache _cache;
    private readonly INotificationSink _sink;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<long, RepositorySnapshot> _previous = [];
    private IRepositoryMonitor? _monitor;
    private (AccountKey Account, AccountCache Cache)? _history;

    public NotificationService(MonitorHost monitors, SettingsService settings, AccountService accounts, RepositoryCache cache,
        INotificationSink sink, TimeProvider time, ILogger<NotificationService> logger)
    {
        _monitors = monitors;
        _settings = settings;
        _accounts = accounts;
        _cache = cache;
        _sink = sink;
        _time = time;
        _logger = logger;
        _monitors.CurrentChanged += OnCurrentChanged;
        Attach();
    }

    public NotificationAvailability Availability => _sink.Availability;

    public void Dispose()
    {
        _monitors.CurrentChanged -= OnCurrentChanged;
        if (_monitor is not null)
        {
            _monitor.Changed -= OnMonitorChanged;
        }
    }

    /// <summary>Shows a sample notification so the user can check the OS lets Repo Watch notify.</summary>
    public bool ShowTest() => _sink.Show(new DesktopNotification("Repo Watch notifications work",
        "You'll be told about CI failures and recoveries, review requests and merges.", null, "test"));

    private void OnCurrentChanged(object? sender, EventArgs e) => Attach();

    private void Attach()
    {
        lock (_gate)
        {
            if (_monitor is not null)
            {
                _monitor.Changed -= OnMonitorChanged;
            }

            // A new monitor (account change, sign-out, removal) starts a fresh, silent baseline, with a
            // history handle for the current sign-in (one taken before a sign-out writes nothing).
            _previous.Clear();
            _history = null;
            _monitor = _monitors.IsDemo ? null : _monitors.Current;
            if (_monitor is not null)
            {
                _monitor.Changed += OnMonitorChanged;
            }
        }

        Evaluate();
    }

    private void OnMonitorChanged(object? sender, EventArgs e) => Evaluate();

    /// <summary>Compares each repository with what was last seen and handles the resulting events.</summary>
    public void Evaluate()
    {
        lock (_gate)
        {
            if (_monitor is not { } monitor || _accounts.Identity is not { } identity)
            {
                return;
            }

            var history = HistoryFor(identity.Account);
            var repositories = monitor.Repositories;
            foreach (var removed in _previous.Keys.Except(repositories.Select(r => r.Key.RepositoryId)).ToList())
            {
                _previous.Remove(removed); // removed repositories never notify
            }

            foreach (var repository in repositories.Where(r => r.Key.Account == identity.Account))
            {
                _previous.TryGetValue(repository.Key.RepositoryId, out var previous);
                _previous[repository.Key.RepositoryId] = repository.Snapshot;
                foreach (var notification in NotificationPolicy.Detect(previous, repository.Snapshot, identity.Login))
                {
                    Handle(history, notification, repository.Watch.NotificationsEnabled);
                }
            }
        }
    }

    private void Handle(AccountCache history, NotificationEvent notification, bool repositoryEnabled)
    {
        var settings = _settings.App.Notifications;
        if (notification.IsBaseline)
        {
            history.TryRecordNotification(notification.Key, "baseline");
            return;
        }

        if (!NotificationPolicy.IsWanted(notification, settings, repositoryEnabled))
        {
            history.TryRecordNotification(notification.Key, "off");
            return;
        }

        if (settings.QuietHours.Contains(TimeOnly.FromDateTime(_time.GetLocalNow().DateTime)))
        {
            history.TryRecordNotification(notification.Key, "quiet");
            return;
        }

        if (!history.TryRecordNotification(notification.Key, "shown"))
        {
            return; // announced (or suppressed) before
        }

        var text = NotificationPolicy.Text(notification, settings.HidePrivateDetails);
        if (!_sink.Show(new DesktopNotification(text.Title, text.Body, notification.Url, notification.Key)))
        {
            _logger.LogInformation("A {Kind} notification was not shown ({Availability})", notification.Kind, _sink.Availability);
        }
    }

    private AccountCache HistoryFor(AccountKey account)
    {
        if (_history is not { } current || current.Account != account)
        {
            _history = (account, _cache.ForAccount(account));
        }

        return _history.Value.Cache;
    }
}
