using System.Collections.ObjectModel;

namespace RepoWatch.Desktop.Presentation;

/// <summary>
/// Updates a bound collection in place so existing item view models (and their focus and
/// selection) survive refreshes. While the user is interacting, existing items keep their
/// positions and new items are appended; the desired order is applied afterwards with moves.
/// </summary>
public static class CollectionReconciler
{
    /// <returns>True if the collection now matches the desired order.</returns>
    public static bool Reconcile<TItem, TSource, TKey>(
        ObservableCollection<TItem> target,
        IReadOnlyList<TSource> desired,
        Func<TSource, TKey> sourceKey,
        Func<TItem, TKey> itemKey,
        Func<TSource, TItem> create,
        Action<TItem, TSource> update,
        bool allowReorder)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);

        var desiredByKey = new Dictionary<TKey, TSource>();
        foreach (var source in desired)
        {
            desiredByKey.TryAdd(sourceKey(source), source);
        }

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredByKey.ContainsKey(itemKey(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        var existing = target.ToDictionary(itemKey);
        foreach (var (key, source) in desiredByKey)
        {
            if (existing.TryGetValue(key, out var item))
            {
                update(item, source);
            }
        }

        var index = 0;
        foreach (var (key, source) in desiredByKey)
        {
            if (!existing.ContainsKey(key))
            {
                var item = create(source);
                existing[key] = item;
                if (allowReorder)
                {
                    target.Insert(Math.Min(index, target.Count), item);
                }
                else
                {
                    target.Add(item);
                }
            }

            index++;
        }

        if (!allowReorder)
        {
            return target.Select(itemKey).SequenceEqual(desiredByKey.Keys);
        }

        index = 0;
        foreach (var key in desiredByKey.Keys)
        {
            var current = IndexOf(target, itemKey, key);
            if (current != index)
            {
                target.Move(current, index);
            }

            index++;
        }

        return true;
    }

    private static int IndexOf<TItem, TKey>(ObservableCollection<TItem> items, Func<TItem, TKey> itemKey, TKey key) where TKey : notnull
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (EqualityComparer<TKey>.Default.Equals(itemKey(items[i]), key))
            {
                return i;
            }
        }

        return -1;
    }
}
