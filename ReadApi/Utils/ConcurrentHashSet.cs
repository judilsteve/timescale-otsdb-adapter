using System.Collections;
using System.Collections.Concurrent;

namespace TimescaleOpenTsdbAdapter.Utils;

public class ConcurrentHashSet<T> : IEnumerable<T> where T : notnull
{
    // There is no ConcurrentHashSet in the standard library, so we use a ConcurrentDictionary instead
    // The value here is meaningless; we use a boolean to minimise the space occupied
    private readonly ConcurrentDictionary<T, bool> dict = [];

    public int Count => dict.Count;

    public void Add(T item)
    {
        dict[item] = true;
    }

    public void Remove(T item)
    {
        dict.Remove(item, out _);
    }

    public bool Contains(T item)
    {
        return dict.ContainsKey(item);
    }

    public IEnumerator<T> GetEnumerator()
    {
        return dict.Keys.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return dict.Keys.GetEnumerator();
    }
}
