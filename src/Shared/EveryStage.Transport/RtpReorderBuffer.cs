namespace EveryStage.Transport;

/// <summary>Small bounded jitter buffer for one RTP SSRC. It restores sequence order, handles the
/// 16-bit sequence wrap naturally, and eventually advances across a confirmed gap.</summary>
public sealed class RtpReorderBuffer
{
    public readonly record struct Delivery(RtpPacket Packet, int PacketsLostBefore);

    private readonly int _maxBufferedPackets;
    private readonly TimeSpan _maxHoldTime;
    private readonly Dictionary<ushort, (RtpPacket Packet, DateTimeOffset ArrivedAt)> _pending = new();
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
                _pending.Add(packet.SequenceNumber, (packet, arrivedAt));
                Interlocked.Increment(ref _packetsReordered);
                if (_pending.Count > _maxBufferedPackets)
                    AdvanceToNearest(output);
            }
        }
        else
        {
            Interlocked.Increment(ref _duplicatesOrLate);
        }

        return output;
    }

    private void FlushExpired(DateTimeOffset now, List<Delivery> output)
    {
        if (_expected == null || _pending.Count == 0) return;
        DateTimeOffset oldest = _pending.Values.Min(x => x.ArrivedAt);
        if (now - oldest >= _maxHoldTime) AdvanceToNearest(output);
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
            output.Add(new Delivery(entry.Packet, first ? lostBeforeFirst : 0));
            first = false;
            _expected = unchecked((ushort)(_expected.Value + 1));
        }
    }

    private static int ForwardDistance(ushort from, ushort to) => unchecked((ushort)(to - from));
}
