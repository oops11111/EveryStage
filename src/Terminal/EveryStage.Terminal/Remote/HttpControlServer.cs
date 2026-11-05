using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EveryStage.Terminal.Logging;

namespace EveryStage.Terminal.Remote;

/// <summary>
/// HTTP 外控 MVP (API A): a minimal HTTP/1.1 endpoint on 127.0.0.1 only (port 18790), off until 设置 · 网络
/// turns it on. Every request needs <c>Authorization: Bearer &lt;token&gt;</c> (<see cref="HttpControlToken"/>);
/// without it the answer is 401.
/// <list type="bullet">
/// <item><c>GET /status</c> — output state, source, cast switch, what Program shows.</item>
/// <item><c>POST /emergency-stop</c> — 急停 = 停止输出 (Disconnect); there is no 重新投屏 / undo.</item>
/// </list>
/// A plain TcpListener on the loopback address (no http.sys URL reservation, no firewall rule). Each
/// request is written to the audit log (logs\http-control). No HTTPS, no LAN binding in this version.
/// </summary>
internal sealed class HttpControlServer : IDisposable
{
    public const int DefaultPort = 18790;
    private const int MaxHeaderBytes = 8192;
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(3);

    private readonly Func<object> _status;
    private readonly Action _emergencyStop;
    private readonly Func<string> _token;
    private readonly SynchronizationContext? _ui;
    private readonly DailyRollingLogWriter? _audit;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; }
    public bool Running => _listener != null;
    public string? LastError { get; private set; }

    /// <param name="status">Builds the /status body (runs on <paramref name="ui"/> when given).</param>
    /// <param name="emergencyStop">停止输出 (runs on <paramref name="ui"/> when given).</param>
    public HttpControlServer(Func<object> status, Action emergencyStop, Func<string> token, SynchronizationContext? ui,
        int port = DefaultPort, string? logRoot = null)
    {
        _status = status;
        _emergencyStop = emergencyStop;
        _token = token;
        _ui = ui;
        Port = port;
        try { _audit = new DailyRollingLogWriter(Path.Combine(logRoot ?? LogPaths.DefaultRoot, "http-control"), "http"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public bool Start()
    {
        if (_listener != null) return true;
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            _listener = listener;
        }
        catch (SocketException ex)
        {
            LastError = $"端口 {Port} 无法监听（{ex.SocketErrorCode}），可能已被其他程序占用。";
            return false;
        }
        LastError = null;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_listener, _cts.Token);
        _audit?.Write("http_control_started", new { port = Port });
        return true;
    }

    public void Stop()
    {
        if (_listener == null) return;
        _cts?.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _audit?.Write("http_control_stopped", new { port = Port });
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token); }
            catch (Exception) { return; } // stopped
            _ = Task.Run(() => HandleClientAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string? head = await ReadHeadAsync(stream, timeout.Token);
            var (status, body) = head == null ? (400, Error("bad_request")) : await HandleAsync(head);
            byte[] payload = Encoding.UTF8.GetBytes(body);
            string reason = status switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Service Unavailable" };
            string header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\n" +
                            (status == 401 ? "WWW-Authenticate: Bearer\r\n" : "") + "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), timeout.Token);
            await stream.WriteAsync(payload, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxHeaderBytes];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) break;
            length += read;
            string text = Encoding.ASCII.GetString(buffer, 0, length);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
        }
        return null;
    }

    /// <summary>Routes one request head (request line + headers). Public for the self-tests.</summary>
    public async Task<(int Status, string Body)> HandleAsync(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return (400, Error("bad_request"));
        string method = parts[0], path = parts[1].Split('?')[0].TrimEnd('/');
        string? authorization = lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
            .FirstOrDefault(p => p[0].Trim().Equals("Authorization", StringComparison.OrdinalIgnoreCase))?[1].Trim();

        int status;
        string body;
        if (!Authorized(authorization)) (status, body) = (401, Error("unauthorized"));
        else
        {
            (status, body) = (method, path) switch
            {
                ("GET", "/status") => await OnUiAsync(() => (200, JsonSerializer.Serialize(_status()))),
                ("POST", "/emergency-stop") => await OnUiAsync(() =>
                {
                    _emergencyStop();
                    return (200, JsonSerializer.Serialize(new { stopped = true, undo = false }));
                }),
                (_, "/status" or "/emergency-stop") => (405, Error("method_not_allowed")),
                _ => (404, Error("not_found")),
            };
        }
        _audit?.Write("http_request", new { method, path, status });
        return (status, body);
    }

    private bool Authorized(string? authorization)
    {
        if (authorization == null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        byte[] given = Encoding.UTF8.GetBytes(authorization[7..].Trim());
        byte[] expected = Encoding.UTF8.GetBytes(_token());
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }

    private async Task<(int, string)> OnUiAsync(Func<(int, string)> action)
    {
        if (_ui == null) return action();
        var done = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            try { done.TrySetResult(action()); }
            catch (Exception ex) { done.TrySetException(ex); }
        }, null);
        var finished = await Task.WhenAny(done.Task, Task.Delay(UiTimeout));
        return finished == done.Task ? await done.Task : (503, Error("busy"));
    }

    private static string Error(string code) => JsonSerializer.Serialize(new { error = code });

    public void Dispose() => Stop();
}
