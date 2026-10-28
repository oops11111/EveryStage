namespace EveryStage.Transport;

/// <summary>Small bounded jitter buffer for one RTP SSRC. It restores sequence order, handles the
/// 16-bit sequence wrap naturally, and eventually advances across a confirmed gap.
///
/// Missing sequence numbers are reported (<see cref="TakeNewlyMissing"/>) the moment a gap is SEEN — a
/// later packet arrives — while the gap is still held open for up to the hold time. Reporting them only
/// when the buffer gave up and advanced (as it used to) made NACK useless: every retransmission arrived
/// behind the play point and was discarded as late (audit A-18).</summary>
public sealed class RtpReorderBuffer
{
    public readonly record struct Delivery(RtpPacket Packet, int PacketsLostBefore,
        IReadOnlyList<ushort>? MissingSequenceNumbers = null);

    // NACK messages carry at most this many sequence numbers; a larger gap is beyond recovery anyway.
    private const int MaxNackPerGap = 64;

    private readonly int _maxBufferedPackets;
    private readonly TimeSpan _maxHoldTime;
    private readonly Dictionary<ushort, (RtpPacket Packet, DateTimeOffset ArrivedAt)> _pending = new();
    private readonly HashSet<ushort> _requested = new();
    private readonly List<ushort> _newlyMissing = new();
    private ushort? _expected;
    private long _packetsLost;
    private long _packetsReordered;
    private long _duplicatesOrLate;
    private long _gapEvents;

    public long PacketsLost => Interlocked.Read(ref _packetsLost);
    public long PacketsReordered => Interlocked.Read(ref _packetsReordered);
    public long DuplicatesOrLate => Interlocked.Read(ref _duplicatesOrLate);
    public long GapEvents => Interlocked.Read(ref _gapEvents);

    public RtpReorderBuffer(int maxBufferedPackets = 32, TimeSpan? maxHoldTime = null)
    {
        if (maxBufferedPackets < 1 || maxBufferedPackets >= short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxBufferedPackets));
        _maxBufferedPackets = maxBufferedPackets;
        _maxHoldTime = maxHoldTime ?? TimeSpan.FromMilliseconds(60);
        if (_maxHoldTime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxHoldTime));
    }

    public IReadOnlyList<Delivery> Add(RtpPacket packet, DateTimeOffset arrivedAt)
    {
        var output = new List<Delivery>();
        FlushExpired(arrivedAt, output);
        _expected ??= packet.SequenceNumber;

        int distance = ForwardDistance(_expected.Value, packet.SequenceNumber);
        if (distance == 0)
        {
            // Arriving after later packets did means it came out of order (or was retransmitted).
            if (_pending.Count > 0) Interlocked.Increment(ref _packetsReordered);
            _pending[packet.SequenceNumber] = (packet, arrivedAt);
            Drain(output, 0);
        }
        else if (distance < short.MaxValue)
        {
            if (_pending.ContainsKey(packet.SequenceNumber))
            {
                Interlocked.Increment(ref _duplicatesOrLate);
            }
            else
            {
                // Filling a hole that later packets had already passed is reordering; a packet that
                // merely arrives after a gap is not — counting those made the counter measure loss
                // (audit C-17).
                if (_requested.Contains(packet.SequenceNumber)) Interlocked.Increment(ref _packetsReordered);
                _pending.Add(packet.SequenceNumber, (packet, arrivedAt));
                ReportGapBefore(packet.SequenceNumber);
                if (_pending.Count > _maxBufferedPackets)
                    AdvanceToNearest(output);
            }
        }
        else
        {
            Interlocked.Increment(ref _duplicatesOrLate);
        }

        PruneRequested();
        return output;
    }

    /// <summary>Sequence numbers newly found missing (each reported once), to be NACKed now while the
    /// gap is still being waited on.</summary>
    public IReadOnlyList<ushort> TakeNewlyMissing()
    {
        if (_newlyMissing.Count == 0) return Array.Empty<ushort>();
        var missing = _newlyMissing.ToArray();
        _newlyMissing.Clear();
        return missing;
    }

    public IReadOnlyList<Delivery> FlushExpired(DateTimeOffset now)
    {
        var output = new List<Delivery>();
        FlushExpired(now, output);
        PruneRequested();
        return output;
    }

    private void ReportGapBefore(ushort arrived)
    {
        int gap = ForwardDistance(_expected!.Value, arrived);
        int reported = 0;
        for (int offset = 0; offset < gap && reported < MaxNackPerGap; offset++)
        {
            ushort sequence = unchecked((ushort)(_expected.Value + offset));
            if (_pending.ContainsKey(sequence) || !_requested.Add(sequence)) continue;
            _newlyMissing.Add(sequence);
            reported++;
        }
    }

    // Requests behind the play point can no longer be filled.
    private void PruneRequested()
    {
        if (_expected == null || _requested.Count == 0) return;
        _requested.RemoveWhere(sequence => ForwardDistance(_expected.Value, sequence) >= short.MaxValue);
    }

    private void FlushExpired(DateTimeOffset now, List<Delivery> output)
    {
        while (_expected != null && _pending.Count > 0)
        {
            DateTimeOffset oldest = _pending.Values.Min(x => x.ArrivedAt);
            if (now - oldest < _maxHoldTime) break;
            AdvanceToNearest(output);
        }
    }

    private void AdvanceToNearest(List<Delivery> output)
    {
        if (_expected == null || _pending.Count == 0) return;
        ushort next = _pending.Keys.MinBy(sequence => ForwardDistance(_expected.Value, sequence));
        int lost = ForwardDistance(_expected.Value, next);
        if (lost > 0)
        {
            Interlocked.Add(ref _packetsLost, lost);
            Interlocked.Increment(ref _gapEvents);
            _expected = next;
        }
        Drain(output, lost);
    }

    private void Drain(List<Delivery> output, int lostBeforeFirst)
    {
        bool first = true;
        while (_expected.HasValue && _pending.Remove(_expected.Value, out var entry))
        {
            IReadOnlyList<ushort>? missing = first && lostBeforeFirst > 0
                ? Enumerable.Range(0, lostBeforeFirst)
                    .Select(offset => unchecked((ushort)(_expected!.Value - lostBeforeFirst + offset)))
                    .ToArray()
                : null;
            output.Add(new Delivery(entry.Packet, first ? lostBeforeFirst : 0, missing));
            first = false;
            _expected = unchecked((ushort)(_expected.Value + 1));
        }
    }

    private static int ForwardDistance(ushort from, ushort to) => unchecked((ushort)(to - from));
}
