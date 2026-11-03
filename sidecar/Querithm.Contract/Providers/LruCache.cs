namespace Querithm.Contract.Providers;

/// <summary>
/// A cache that keeps at most <see cref="Capacity"/> entries and drops the least recently used one first (M12:
/// per-document language caches). Thread-safe; every operation but <see cref="RemoveWhere"/> is O(1) under one short
/// lock, and the values are built outside it by the caller.
/// </summary>
public sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    readonly object _gate = new();
    readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();
    readonly LinkedList<(TKey Key, TValue Value)> _order = new();

    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public int Count { get { lock (_gate) return _map.Count; } }

    /// <summary>Whether the key is cached, without making it the most recently used.</summary>
    public bool Contains(TKey key)
    {
        lock (_gate) return _map.ContainsKey(key);
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                node.Value = (key, value);
                _order.AddFirst(node);
                return;
            }
            _map[key] = _order.AddFirst((key, value));
            while (_map.Count > Capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public bool Remove(TKey key)
    {
        lock (_gate)
        {
            if (!_map.Remove(key, out var node)) return false;
            _order.Remove(node);
            return true;
        }
    }

    /// <summary>Removes every entry <paramref name="match"/> accepts (a document cached under several keys).</summary>
    public int RemoveWhere(Func<TKey, TValue, bool> match)
    {
        lock (_gate)
        {
            var keys = _map.Where(kv => match(kv.Key, kv.Value.Value.Value)).Select(kv => kv.Key).ToList();
            foreach (var k in keys) Remove(k);
            return keys.Count;
        }
    }
}
