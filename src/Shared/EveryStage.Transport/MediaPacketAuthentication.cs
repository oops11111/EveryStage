using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EveryStage.Transport;

/// <summary>Authenticates one media stream with a session-specific key and a bounded replay window.
/// Instances belong to a single sender or a single sequential receive loop.</summary>
public sealed class MediaPacketAuthentication : IDisposable
{
    private const int HeaderSize = 24;
    private const int TagSize = 32;
    private const int ReplayWindow = 1024;
    private readonly byte[] _key;
    private readonly Guid _sessionId;
    private readonly ulong[] _seen = new ulong[ReplayWindow];
    private ulong _highest;
    private ulong _counter;
    private bool _disposed;

    public MediaPacketAuthentication(byte[] key, Guid sessionId)
    {
        if (key.Length != 32) throw new ArgumentException("A 256-bit key is required.", nameof(key));
        if (sessionId == Guid.Empty) throw new ArgumentException("Session identifier is required.", nameof(sessionId));
        _key = key.ToArray();
        _sessionId = sessionId;
    }

    public byte[] Protect(ReadOnlySpan<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_counter == ulong.MaxValue) throw new InvalidOperationException("Media packet counter exhausted.");
        byte[] envelope = new byte[HeaderSize + packet.Length + TagSize];
        _sessionId.TryWriteBytes(envelope);
        BinaryPrimitives.WriteUInt64BigEndian(envelope.AsSpan(16), ++_counter);
        packet.CopyTo(envelope.AsSpan(HeaderSize));
        HMACSHA256.HashData(_key, envelope.AsSpan(0, envelope.Length - TagSize), envelope.AsSpan(envelope.Length - TagSize));
        return envelope;
    }

    public bool TryUnprotect(ReadOnlySpan<byte> envelope, out byte[] packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        packet = Array.Empty<byte>();
        if (envelope.Length < HeaderSize + TagSize || new Guid(envelope[..16]) != _sessionId) return false;
        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(envelope.Slice(16, 8));
        int slot = (int)(counter % ReplayWindow);
        if (counter == 0 || (_highest >= ReplayWindow && counter <= _highest - ReplayWindow) || _seen[slot] == counter) return false;
        Span<byte> tag = stackalloc byte[TagSize];
        HMACSHA256.HashData(_key, envelope[..^TagSize], tag);
        if (!CryptographicOperations.FixedTimeEquals(tag, envelope[^TagSize..])) return false;
        _highest = Math.Max(_highest, counter);
        // Two counters in the live window cannot share a slot. Old slots are overwritten only
        // after authentication, so forged counters cannot evict valid replay records.
        _seen[slot] = counter;
        packet = envelope.Slice(HeaderSize, envelope.Length - HeaderSize - TagSize).ToArray();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
        Array.Clear(_seen);
    }
}
