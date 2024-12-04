namespace TimescaleOpenTsdbAdapter.Utils;

using LruCacheNet;

public interface ITtlLruCache<TKey, TValue>
{
    bool TryGetValue(TKey key, out TValue value);
    void AddOrRevalidate(TKey key, TValue value, DateTime currentAsAt);

    int Count { get; }
    int Capacity { get; }
}

public class NoTtlLruCache<TKey, TValue>(int capacity) : ITtlLruCache<TKey, TValue>
{
    private readonly LruCache<TKey, TValue> lruCache = new(capacity);

    public bool TryGetValue(TKey key, out TValue value)
    {
        return lruCache.TryGetValue(key, out value);
    }

    public void AddOrRevalidate(TKey key, TValue value, DateTime _)
    {
        lruCache[key] = value;
    }

    public int Count => lruCache.Count;
    public int Capacity => lruCache.Capacity;
}

public class TtlLruCache<TKey, TValue>(int capacity, TimeSpan valueTtl)
    : ITtlLruCache<TKey, TValue> {
    private readonly TimeSpan valueTtl = valueTtl;
    private readonly LruCache<TKey, (TValue value, DateTime lastValidated)> lruCache = new(capacity);

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (lruCache.TryGetValue(key, out var tuple))
        {
            (value, var lastValidated) = tuple;
            var stale = DateTime.UtcNow - lastValidated > valueTtl;
            if (stale) lruCache.Remove(key);
            return !stale;
        }
        value = default!;
        return false;
    }

    public void AddOrRevalidate(TKey key, TValue value, DateTime currentAsAt)
    {
        lruCache[key] = (value, currentAsAt);
    }

    public int Count => lruCache.Count;
    public int Capacity => lruCache.Capacity;
}
