using System.Security.Cryptography;

namespace EveryStage.Terminal.Remote;

/// <summary>
/// The Bearer token for HTTP 外控, kept in the restricted secure folder (not in settings.json, which local
/// Users may modify). Created on first use; 设置 · 网络 can copy or reset it (a reset invalidates the old
/// token for the very next request).
/// </summary>
public static class HttpControlToken
{
    private static readonly object Gate = new();
    private static string? _cached;

    public static string Path { get; set; } = EveryStage.Discovery.SecureDataPaths.Resolve("http-control-token.txt");

    public static string Current()
    {
        lock (Gate)
        {
            if (_cached != null) return _cached;
            try
            {
                if (File.Exists(Path) && File.ReadAllText(Path).Trim() is { Length: >= 32 } stored) return _cached = stored;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return ResetLocked();
        }
    }

    public static string Reset()
    {
        lock (Gate) return ResetLocked();
    }

    private static string ResetLocked()
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* works for this run only */ }
        return _cached = token;
    }
}
