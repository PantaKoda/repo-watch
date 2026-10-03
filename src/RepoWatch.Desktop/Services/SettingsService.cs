using Microsoft.Extensions.Logging;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Settings;

namespace RepoWatch.Desktop.Services;

public sealed class AppSettingsChangedEventArgs(AppSettings previous, AppSettings current) : EventArgs
{
    public AppSettings Previous { get; } = previous;

    public AppSettings Current { get; } = current;
}

/// <summary>
/// Holds the current app settings in memory and saves changes shortly after they happen.
/// If storage is unavailable the app keeps working with in-memory settings and reports why.
/// </summary>
public sealed class SettingsService : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly Func<ISettingsStore> _openStore;
    private readonly ILogger<SettingsService> _logger;
    private readonly Lock _gate = new();
    // Serializes snapshot + write so an older snapshot can never be saved after a newer one.
    private readonly Lock _writeGate = new();
    private readonly Timer _saveTimer;
    private ISettingsStore? _store;
    private bool _dirty;

    public SettingsService(Func<ISettingsStore> openStore, ILogger<SettingsService> logger)
    {
        _openStore = openStore;
        _logger = logger;
        _saveTimer = new Timer(_ => Flush());
    }

    public AppSettings App { get; private set; } = SettingsCodecs.App.CreateDefault();

    /// <summary>A user-facing explanation when settings could not be loaded or will not be saved.</summary>
    public string? Problem { get; private set; }

    /// <summary>Raised on the thread that made the change, with the previous and current settings.</summary>
    public event EventHandler<AppSettingsChangedEventArgs>? AppChanged;

    /// <summary>Raised (on any thread) when <see cref="Problem"/> changes.</summary>
    public event EventHandler? ProblemChanged;

    public void Load()
    {
        try
        {
            _store = _openStore();
            var result = _store.LoadAppSettings();
            App = result.Value;
            SetProblem(result.Status switch
            {
                SettingsLoadStatus.Corrupt => "Saved settings were unreadable and have been reset. A backup was kept.",
                SettingsLoadStatus.Repaired => "Some saved settings were invalid and have been reset to defaults.",
                SettingsLoadStatus.NewerVersion => "Settings were saved by a newer version of Repo Watch. Changes made now will not be saved.",
                _ => null,
            });
        }
        catch (Exception ex)
        {
            // e.g. a database from a newer version, or an unwritable data folder.
            _store = null;
            _logger.LogError(ex, "Settings storage unavailable; continuing with in-memory settings");
            SetProblem($"Settings storage is unavailable; changes will not be saved. {ex.Message}");
        }
    }

    public void UpdateApp(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings previous;
        AppSettings updated;
        lock (_gate)
        {
            previous = App;
            updated = SettingsCodecs.Normalize(change(App));
            if (updated == previous)
            {
                return;
            }

            App = updated;
            _dirty = true;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        AppChanged?.Invoke(this, new AppSettingsChangedEventArgs(previous, updated));
    }

    /// <summary>Writes pending changes now. Called on a timer and at shutdown; safe to call concurrently.</summary>
    public void Flush()
    {
        lock (_writeGate)
        {
            AppSettings snapshot;
            lock (_gate)
            {
                if (!_dirty || _store is null)
                {
                    return;
                }

                _dirty = false;
                snapshot = App;
            }

            try
            {
                if (!_store.SaveAppSettings(snapshot))
                {
                    SetProblem("Settings were saved by a newer version of Repo Watch. Changes made now will not be saved.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving settings failed");
                lock (_gate)
                {
                    _dirty = true; // retried on the next change or at shutdown
                }

                SetProblem($"Saving settings failed: {ex.Message}");
            }
        }
    }

    /// <summary>Per-account settings (watchlist, last known login). Defaults if storage is unavailable.</summary>
    public AccountSettings GetAccount(AccountKey account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_writeGate)
        {
            try
            {
                return _store?.LoadAccountSettings(account).Value ?? SettingsCodecs.Account.CreateDefault();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Loading account settings failed");
                return SettingsCodecs.Account.CreateDefault();
            }
        }
    }

    /// <summary>Changes and immediately saves one account's settings. These change rarely, so no batching.</summary>
    public void UpdateAccount(AccountKey account, Func<AccountSettings, AccountSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_writeGate)
        {
            if (_store is null)
            {
                return;
            }

            try
            {
                var current = _store.LoadAccountSettings(account).Value;
                if (!_store.SaveAccountSettings(account, change(current)))
                {
                    SetProblem("Settings were saved by a newer version of Repo Watch. Changes made now will not be saved.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Saving account settings failed");
                SetProblem($"Saving settings failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        using (var callbacksDone = new ManualResetEvent(false))
        {
            if (_saveTimer.Dispose(callbacksDone))
            {
                callbacksDone.WaitOne(TimeSpan.FromSeconds(5));
            }
        }

        Flush();
    }

    private void SetProblem(string? problem)
    {
        if (problem == Problem)
        {
            return;
        }

        Problem = problem;
        ProblemChanged?.Invoke(this, EventArgs.Empty);
    }
}
