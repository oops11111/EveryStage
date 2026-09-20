using System.Security.Cryptography;

namespace EveryStage.Discovery;

public static class PairingSecurity
{
    public const int KeySizeBytes = 32;

    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeySizeBytes));

    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        try { return Convert.FromBase64String(key).Length == KeySizeBytes; }
        catch (FormatException) { return false; }
    }
}
