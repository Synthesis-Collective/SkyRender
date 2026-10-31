using System.Collections.Generic;

namespace SkyRender;

/// <summary>
/// Byte-bounded LRU of BSA file contents served in memory by <see cref="GameAssetResolver"/>.
/// One texture load reads its file two to four times (alpha probe, compressed-DDS inspect and read,
/// Pfim decode), so keeping recent files avoids decompressing them from the archive each time.
/// An evicted file is read from the archive again on its next use; a file larger than the whole
/// budget is returned but not kept. Arrays handed out are shared and must not be modified.
/// </summary>
internal sealed class BsaMemoryStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Bytes)>> _map =
        new(System.StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, byte[] Bytes)> _lru = new();
    private long _bytes;

    public BsaMemoryStore(long budgetBytes) => BudgetBytes = budgetBytes;

    public long BudgetBytes { get; }

    public long ResidentBytes { get { lock (_lock) return _bytes; } }

    public bool TryGet(string key, out byte[] bytes)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                bytes = node.Value.Bytes;
                return true;
            }
        }
        bytes = System.Array.Empty<byte>();
        return false;
    }

    public void Put(string key, byte[] bytes)
    {
        if (bytes.LongLength > BudgetBytes) return;
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _bytes -= existing.Value.Bytes.LongLength;
                _lru.Remove(existing);
            }
            var node = _lru.AddFirst((key, bytes));
            _map[key] = node;
            _bytes += bytes.LongLength;
            while (_bytes > BudgetBytes && _lru.Last is { } victim)
            {
                _bytes -= victim.Value.Bytes.LongLength;
                _map.Remove(victim.Value.Key);
                _lru.RemoveLast();
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }
}
