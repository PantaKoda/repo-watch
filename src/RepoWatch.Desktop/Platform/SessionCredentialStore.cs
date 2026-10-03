using System.Collections.Concurrent;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Identity;

namespace RepoWatch.Desktop.Platform;

/// <summary>
/// Session-only mode for platforms without a supported secure store: tokens live in memory and
/// are gone when the app exits. Never falls back to plaintext on disk.
/// </summary>
public sealed class SessionCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<AccountKey, StoredCredential> _credentials = new();

    public bool IsPersistent => false;

    public string Description => "this session only";

    public Task<StoredCredential?> ReadAsync(AccountKey account, CancellationToken cancellationToken = default) =>
        Task.FromResult(_credentials.TryGetValue(account, out var credential) ? credential : null);

    public Task WriteAsync(AccountKey account, StoredCredential credential, CancellationToken cancellationToken = default)
    {
        _credentials[account] = credential;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(AccountKey account, CancellationToken cancellationToken = default)
    {
        _credentials.TryRemove(account, out _);
        return Task.CompletedTask;
    }
}
