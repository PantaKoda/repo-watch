using Microsoft.Extensions.Logging;
using RepoWatch.Core.Settings;

namespace RepoWatch.Desktop.Services;

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

    /// <summary>Raised on the thread that made the change.</summary>
    public event EventHandler? AppChanged;

    public void Load()
    {
        try
        {
            _store = _openStore();
            var result = _store.LoadAppSettings();
            App = result.Value;
            Problem = result.Status switch
            {
                SettingsLoadStatus.Corrupt => "Saved settings were unreadable and have been reset. A backup was kept.",
                SettingsLoadStatus.Repaired => "Some saved settings were invalid and have been reset to defaults.",
                SettingsLoadStatus.NewerVersion => "Settings were saved by a newer version of Repo Watch. Changes made now will not be saved.",
                _ => null,
            };
        }
        catch (Exception ex)
        {
            // e.g. a database from a newer version, or an unwritable data folder.
            _store = null;
            Problem = $"Settings storage is unavailable; changes will not be saved. {ex.Message}";
            _logger.LogError(ex, "Settings storage unavailable; continuing with in-memory settings");
        }
    }

    public void UpdateApp(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var updated = SettingsCodecs.Normalize(change(App));
            if (updated == App)
            {
                return;
            }

            App = updated;
            _dirty = true;
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        AppChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes pending changes now. Called on a timer and at shutdown.</summary>
    public void Flush()
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
                Problem ??= "Settings were saved by a newer version of Repo Watch. Changes made now will not be saved.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving settings failed");
            Problem = $"Saving settings failed: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }
}
