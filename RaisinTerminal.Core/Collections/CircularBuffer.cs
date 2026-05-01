namespace RaisinTerminal.Core.Collections;

/// <summary>
/// Fixed-capacity circular buffer. When full, adding a new item overwrites the oldest.
/// Items are indexed 0 (oldest) through Count-1 (newest).
/// </summary>
public class CircularBuffer<T>
{
    private readonly T[] _items;
    private int _head; // index of the next write position
    private int _count;

    public CircularBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count => _count;

    /// <summary>
    /// Adds an item. If the buffer is full, the oldest item is overwritten.
    /// Returns true if an item was evicted.
    /// </summary>
    public bool Add(T item)
    {
        bool evicted = _count == _items.Length;
        _items[_head] = item;
        _head = (_head + 1) % _items.Length;
        if (!evicted) _count++;
        return evicted;
    }

    /// <summary>
    /// Gets the item at the given logical index (0 = oldest, Count-1 = newest).
    /// </summary>
    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
                throw new ArgumentOutOfRangeException(nameof(index));
            int start = (_head - _count + _items.Length) % _items.Length;
            return _items[(start + index) % _items.Length];
        }
    }

    /// <summary>
    /// Removes the N most recently added items (from the newest end).
    /// </summary>
    public void RemoveNewest(int n)
    {
        n = Math.Min(n, _count);
        _head = (_head - n + _items.Length) % _items.Length;
        _count -= n;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        Array.Clear(_items);
    }
}
