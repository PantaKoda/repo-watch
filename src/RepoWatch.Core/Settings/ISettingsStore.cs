using RepoWatch.Core.Identity;

namespace RepoWatch.Core.Settings;

/// <summary>
/// Persists settings documents. Implementations must keep accounts isolated, keep secrets out,
/// back up unreadable documents before they can be replaced, and refuse to overwrite a document
/// written by a newer schema version.
/// </summary>
public interface ISettingsStore
{
    SettingsLoadResult<AppSettings> LoadAppSettings();

    /// <returns>False if the stored document is from a newer version and was left untouched.</returns>
    bool SaveAppSettings(AppSettings settings);

    SettingsLoadResult<AccountSettings> LoadAccountSettings(AccountKey account);

    /// <returns>False if the stored document is from a newer version and was left untouched.</returns>
    bool SaveAccountSettings(AccountKey account, AccountSettings settings);
}
