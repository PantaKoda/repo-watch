using RepoWatch.Core.Settings;

namespace RepoWatch.Core.Access;

/// <summary>
/// Pure edits to a watchlist. The list order is the manual order; entries are identified by
/// repository ID, so renames and transfers never create duplicates.
/// </summary>
public static class Watchlist
{
    /// <summary>Appends repositories not already watched, keeping the existing order.</summary>
    public static IReadOnlyList<WatchedRepository> Add(IReadOnlyList<WatchedRepository> watchlist, IEnumerable<AccessibleRepository> repositories)
    {
        ArgumentNullException.ThrowIfNull(watchlist);
        var watched = watchlist.Select(w => w.RepositoryId).ToHashSet();
        var added = repositories
            .Where(r => watched.Add(r.Id))
            .Select(r => new WatchedRepository { RepositoryId = r.Id, Owner = r.Owner, Name = r.Name });
        return watchlist.Concat(added).ToList();
    }

    public static IReadOnlyList<WatchedRepository> Remove(IReadOnlyList<WatchedRepository> watchlist, IEnumerable<long> repositoryIds)
    {
        ArgumentNullException.ThrowIfNull(watchlist);
        var removed = repositoryIds.ToHashSet();
        return watchlist.Where(w => !removed.Contains(w.RepositoryId)).ToList();
    }

    /// <summary>Moves one entry by <paramref name="offset"/> positions, clamped to the list.</summary>
    public static IReadOnlyList<WatchedRepository> Move(IReadOnlyList<WatchedRepository> watchlist, long repositoryId, int offset)
    {
        ArgumentNullException.ThrowIfNull(watchlist);
        var list = watchlist.ToList();
        var index = list.FindIndex(w => w.RepositoryId == repositoryId);
        if (index < 0)
        {
            return watchlist;
        }

        var target = Math.Clamp(index + offset, 0, list.Count - 1);
        var item = list[index];
        list.RemoveAt(index);
        list.Insert(target, item);
        return list;
    }

    public static IReadOnlyList<WatchedRepository> Update(IReadOnlyList<WatchedRepository> watchlist, long repositoryId, Func<WatchedRepository, WatchedRepository> change)
    {
        ArgumentNullException.ThrowIfNull(watchlist);
        return watchlist.Select(w => w.RepositoryId == repositoryId ? change(w) : w).ToList();
    }

    /// <summary>Refreshes last-known owner/name from the catalog after renames or transfers (matched by ID).</summary>
    public static IReadOnlyList<WatchedRepository> RefreshNames(IReadOnlyList<WatchedRepository> watchlist, AccessCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(watchlist);
        ArgumentNullException.ThrowIfNull(catalog);
        var byId = catalog.Repositories.ToDictionary(r => r.Id);
        return watchlist.Select(w => byId.TryGetValue(w.RepositoryId, out var r) && (r.Owner != w.Owner || r.Name != w.Name)
            ? w with { Owner = r.Owner, Name = r.Name }
            : w).ToList();
    }
}

/// <summary>Picker filters. Pure so the "applies to N shown repositories" wording can be tested.</summary>
public sealed record PickerFilter(string? Search = null, string? Owner = null, bool SelectedOnly = false)
{
    public bool Matches(AccessibleRepository repository, bool isSelected)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (SelectedOnly && !isSelected)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Owner) && !string.Equals(repository.Owner, Owner, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Search))
        {
            return true;
        }

        var terms = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(t => repository.FullName.Contains(t, StringComparison.OrdinalIgnoreCase)
            || (repository.Description?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    public bool IsFiltered => !string.IsNullOrWhiteSpace(Search) || !string.IsNullOrWhiteSpace(Owner) || SelectedOnly;
}
