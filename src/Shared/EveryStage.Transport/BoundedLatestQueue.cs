using System.Diagnostics.CodeAnalysis;

namespace EveryStage.Transport;

/// <summary>Transfers ownership on enqueue/dequeue; releases old items when a live consumer lags.</summary>
public sealed class BoundedLatestQueue<T> : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<T> _items = new();
    private readonly int _capacity;
    private readonly Action<T> _release;
    private bool _disposed;

    public BoundedLatestQueue(int capacity, Action<T> release)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentNullException.ThrowIfNull(release);
        _capacity = capacity;
        _release = release;
    }

    public void Enqueue(T item)
    {
        lock (_gate)
        {
            if (_disposed) { _release(item); return; }
            if (_items.Count == _capacity) _release(_items.Dequeue());
            _items.Enqueue(item);
        }
    }

    public bool TryDequeue([MaybeNullWhen(false)] out T item)
    {
        lock (_gate) return _items.TryDequeue(out item);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            while (_items.TryDequeue(out var item)) _release(item);
        }
    }
}
