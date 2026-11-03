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
            Monitor.Pulse(_gate);
        }
    }

    public bool TryDequeue([MaybeNullWhen(false)] out T item)
    {
        lock (_gate) return _items.TryDequeue(out item);
    }

    /// <summary>Waits for a frame without polling. Cancellation or disposal wakes the consumer;
    /// queued frames remain owned by the queue until dequeued or disposed.</summary>
    public bool WaitDequeue(CancellationToken cancellationToken, [MaybeNullWhen(false)] out T item)
    {
        using var registration = cancellationToken.Register(() =>
        {
            lock (_gate) Monitor.PulseAll(_gate);
        });
        lock (_gate)
        {
            while (!_disposed && !cancellationToken.IsCancellationRequested && _items.Count == 0)
                Monitor.Wait(_gate);
            if (!_disposed && !cancellationToken.IsCancellationRequested)
                return _items.TryDequeue(out item);
            item = default;
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            while (_items.TryDequeue(out var item)) _release(item);
            Monitor.PulseAll(_gate);
        }
    }
}
