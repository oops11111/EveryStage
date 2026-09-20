namespace EveryStage.Discovery;

/// <summary>Small protocol-level helpers shared by the two applications and directly testable
/// without binding their fixed discovery port.</summary>
public static class AcknowledgedMessageRetry
{
    public const int DefaultMaxAttempts = 3;
    public static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Sends immediately, then retries after each timeout until acknowledged or the
    /// bounded attempt count is exhausted.</summary>
    public static async Task<bool> SendUntilAcknowledgedAsync(
        Func<Task> sendAsync,
        Task acknowledgement,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? attemptTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        var delay = attemptTimeout ?? DefaultAttemptTimeout;
        if (delay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(attemptTimeout));

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sendAsync();
            if (acknowledgement.IsCompleted) return true;

            var timeoutTask = Task.Delay(delay, cancellationToken);
            if (await Task.WhenAny(acknowledgement, timeoutTask) == acknowledgement)
                return true;
            await timeoutTask;
        }

        return acknowledgement.IsCompleted;
    }
}

/// <summary>Suppresses duplicate and out-of-order status delivery while allowing callers to ACK
/// every datagram before consulting this tracker.</summary>
public sealed class CastStatusSequenceTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid DeviceId, Guid SessionId), long> _latest = new();

    public bool TryAccept(Guid deviceId, Guid sessionId, long sequenceNumber)
    {
        lock (_gate)
        {
            var key = (deviceId, sessionId);
            if (_latest.TryGetValue(key, out long latest) && sequenceNumber <= latest)
                return false;
            _latest[key] = sequenceNumber;
            return true;
        }
    }
}
