using System.Security.Cryptography;
using System.Text;

namespace EveryStage.Discovery;

public static class PairingSecurity
{
    public const int KeySizeBytes = 32;
    public const int CurrentKeyFormatVersion = 1;

    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySizeBytes));

    public static byte[] DeriveMediaKey(string pairingKey, Guid sessionId, string stream)
    {
        if (!IsValidKey(pairingKey) || sessionId == Guid.Empty) throw new ArgumentException("Valid pairing and session required.");
        return HMACSHA256.HashData(Convert.FromBase64String(pairingKey),
            Encoding.UTF8.GetBytes($"EveryStage/media/v1/{sessionId:D}/{stream}"));
    }

    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        try { return Convert.FromBase64String(key).Length == KeySizeBytes; }
        catch (FormatException) { return false; }
    }

    public static bool IsSupportedKey(string? key, int keyFormatVersion) =>
        keyFormatVersion == CurrentKeyFormatVersion && IsValidKey(key);

    public static void Sign(DiscoveryProtocol.AuthenticatedMessage message, Guid senderDeviceId, string key)
    {
        if (!IsValidKey(key)) throw new ArgumentException("A valid 256-bit pairing key is required.", nameof(key));
        message.SenderDeviceId = senderDeviceId;
        message.MessageId = Guid.NewGuid();
        message.IssuedAtUtc = DateTimeOffset.UtcNow;
        message.AuthenticationTag = null;
        using var hmac = new HMACSHA256(Convert.FromBase64String(key));
        message.AuthenticationTag = Convert.ToBase64String(hmac.ComputeHash(DiscoveryProtocol.Encode(message)));
    }

    public static bool Verify(DiscoveryProtocol.AuthenticatedMessage message, string? key)
    {
        if (!IsValidKey(key) || string.IsNullOrWhiteSpace(message.AuthenticationTag)) return false;
        byte[] supplied;
        try { supplied = Convert.FromBase64String(message.AuthenticationTag); }
        catch (FormatException) { return false; }

        string tag = message.AuthenticationTag;
        message.AuthenticationTag = null;
        byte[] expected;
        try
        {
            using var hmac = new HMACSHA256(Convert.FromBase64String(key!));
            expected = hmac.ComputeHash(DiscoveryProtocol.Encode(message));
        }
        finally { message.AuthenticationTag = tag; }
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}

public sealed class ReplayGuard
{
    public static readonly TimeSpan DefaultAllowedClockSkew = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _seen = new();

    /// <summary>The freshness half of <see cref="TryAccept"/>, on its own: the message carries a
    /// usable identifier and its issue time is inside the allowed clock skew. Split out because an
    /// idempotent-retry path legitimately needs to accept a MessageId it has already seen (that is
    /// what makes it a retry) while still refusing one issued hours ago - skipping de-duplication
    /// must not also mean skipping the only bound this protocol places on how long a captured
    /// packet stays replayable.</summary>
    public static bool IsFresh(DiscoveryProtocol.AuthenticatedMessage message, DateTimeOffset now, TimeSpan? allowedClockSkew = null)
    {
        var window = allowedClockSkew ?? DefaultAllowedClockSkew;
        return message.MessageId != Guid.Empty
            && message.IssuedAtUtc >= now - window
            && message.IssuedAtUtc <= now + window;
    }
    public bool TryAccept(DiscoveryProtocol.AuthenticatedMessage message, DateTimeOffset now, TimeSpan? allowedClockSkew = null)
    {
        if (!IsFresh(message, now, allowedClockSkew)) return false;
        var window = allowedClockSkew ?? DefaultAllowedClockSkew;
        lock (_gate)
        {
            foreach (var expired in _seen.Where(x => x.Value < now - window).Select(x => x.Key).ToList())
                _seen.Remove(expired);
            return _seen.TryAdd(message.MessageId, message.IssuedAtUtc);
        }
    }
}
