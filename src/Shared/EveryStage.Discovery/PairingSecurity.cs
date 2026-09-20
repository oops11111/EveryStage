using System.Security.Cryptography;
using System.Text;

namespace EveryStage.Discovery;

public static class PairingSecurity
{
    public const int KeySizeBytes = 32;
    public const int CurrentKeyFormatVersion = 1;

    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySizeBytes));

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

    public bool TryAccept(DiscoveryProtocol.AuthenticatedMessage message, DateTimeOffset now, TimeSpan? allowedClockSkew = null)
    {
        var window = allowedClockSkew ?? DefaultAllowedClockSkew;
        if (message.MessageId == Guid.Empty || message.IssuedAtUtc < now - window || message.IssuedAtUtc > now + window)
            return false;
        lock (_gate)
        {
            foreach (var expired in _seen.Where(x => x.Value < now - window).Select(x => x.Key).ToList())
                _seen.Remove(expired);
            return _seen.TryAdd(message.MessageId, message.IssuedAtUtc);
        }
    }
}
