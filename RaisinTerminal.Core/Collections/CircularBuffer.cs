namespace RaisinTerminal.Core.Collections;

/// <summary>
/// Fixed-capacity circular buffer. When full, adding a new item overwrites the oldest.
/// Items are indexed 0 (oldest) through Count-1 (newest).
/// </summary>
public class CircularBuffer<T>
{
    private readonly T[] _items;
    private int _head; // index of the next write position
    private bool _full;

    public CircularBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count => _full ? _items.Length : _head;

    /// <summary>
    /// Adds an item. If the buffer is full, the oldest item is overwritten.
    /// Returns true if an item was evicted.
    /// </summary>
    public bool Add(T item)
    {
        bool evicted = _full;
        _items[_head] = item;
        _head = (_head + 1) % _items.Length;
        if (!_full && _head == 0)
            _full = true;
        else if (evicted)
            _full = true;
        return evicted;
    }

    /// <summary>
    /// Gets the item at the given logical index (0 = oldest, Count-1 = newest).
    /// </summary>
    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            int realIndex = _full
                ? (_head + index) % _items.Length
                : index;
            return _items[realIndex];
        }
    }

    public void Clear()
    {
        _head = 0;
        _full = false;
        Array.Clear(_items);
    }
}
