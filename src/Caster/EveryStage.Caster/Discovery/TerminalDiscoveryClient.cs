using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using EveryStage.Discovery;

namespace EveryStage.Caster.Discovery;

/// <summary>
/// Caster-side counterpart to Terminal's <c>DiscoveryService</c>: listens for beacon broadcasts to
/// build the "目标终端机列表" (PLANNING.md §12), and sends pairing requests when the user picks one.
///
/// Unlike Terminal, this keeps no persisted trust list of its own in this first pass — the UI (§12)
/// only calls for a live list of what's currently reachable ("已配对直显" suggests Caster should
/// also remember previously-paired terminals and show them even when not currently broadcasting,
/// but that's not implemented yet; see this project's README).
/// </summary>
public sealed class TerminalDiscoveryClient : IDisposable
{
    // A Terminal beacons roughly every 3s (see Terminal's DiscoveryService.BeaconInterval); missing
    // ~3 in a row is a reasonable "probably gone" signal without being trigger-happy about one lost packet.
    private static readonly TimeSpan ExpiryAfter = TimeSpan.FromSeconds(10);

    private readonly UdpClient _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DiscoveredTerminal> _terminals = new();
    private readonly Dictionary<string, TaskCompletionSource<DiscoveryProtocol.PairResponseMessage>> _pendingPairRequests = new();

    private Task? _receiveLoop;

    /// <summary>Raised (from a background task — marshal to the UI thread before touching UI) when
    /// a terminal is newly seen, changes name, or is pruned as expired.</summary>
    public event Action? TerminalListChanged;

    public TerminalDiscoveryClient()
    {
        _socket = new UdpClient();
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.Port));
        _socket.EnableBroadcast = true;
    }

    public void Start() => _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));

    public IReadOnlyList<DiscoveredTerminal> GetTerminals()
    {
        lock (_gate)
        {
            PruneExpired();
            return _terminals.Values.OrderBy(t => t.DeviceName).ToList();
        }
    }

    /// <summary>Sends a pairing request and awaits the Terminal's response. Returns null on timeout
    /// (no distinction from "declined" at the caller level today — see this project's README on why).</summary>
    public async Task<DiscoveryProtocol.PairResponseMessage?> RequestPairingAsync(
        DiscoveredTerminal terminal, DeviceIdentity myIdentity, TimeSpan? timeout = null)
    {
        string requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<DiscoveryProtocol.PairResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _pendingPairRequests[requestId] = tcs;

        try
        {
            var request = new DiscoveryProtocol.PairRequestMessage
            {
                RequestId = requestId,
                DeviceId = myIdentity.DeviceId,
                DeviceName = myIdentity.DeviceName,
            };
            byte[] payload = DiscoveryProtocol.Encode(request);
            await _socket.SendAsync(payload, payload.Length, new IPEndPoint(terminal.Address, DiscoveryProtocol.Port));

            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                return null; // Terminal never answered (offline, request lost, or sitting unanswered on its side).
            }
        }
        finally
        {
            lock (_gate) _pendingPairRequests.Remove(requestId);
        }
    }

    /// <summary>Tells the Terminal a live RTP/H.264 stream is about to start on
    /// <see cref="DiscoveryProtocol.VideoRtpPort"/>, and at what resolution/payload type — see
    /// <see cref="DiscoveryProtocol.CastStartMessage"/> for why this exists instead of the Terminal
    /// inferring a stream's parameters purely from incoming RTP packets. Best-effort, fire-and-forget
    /// like every other send in this class — there's no acknowledgment or retry.</summary>
    public Task SendCastStartAsync(DiscoveredTerminal terminal, DeviceIdentity myIdentity, int width, int height, byte payloadType) =>
        SendControlMessageAsync(terminal, new DiscoveryProtocol.CastStartMessage
        {
            DeviceId = myIdentity.DeviceId,
            Width = width,
            Height = height,
            PayloadType = payloadType,
        });

    /// <summary>Tells the Terminal the live stream has ended — see
    /// <see cref="DiscoveryProtocol.CastStopMessage"/>.</summary>
    public Task SendCastStopAsync(DiscoveredTerminal terminal, DeviceIdentity myIdentity) =>
        SendControlMessageAsync(terminal, new DiscoveryProtocol.CastStopMessage { DeviceId = myIdentity.DeviceId });

    private async Task SendControlMessageAsync(DiscoveredTerminal terminal, DiscoveryProtocol.Message message)
    {
        try
        {
            byte[] payload = DiscoveryProtocol.Encode(message);
            await _socket.SendAsync(payload, payload.Length, new IPEndPoint(terminal.Address, DiscoveryProtocol.Port));
        }
        catch (SocketException) { } // best-effort — same reasoning as every other send in this class.
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // transient network error on one datagram — keep listening.
            }

            HandleDatagram(result.Buffer, result.RemoteEndPoint);
        }
    }

    private void HandleDatagram(byte[] data, IPEndPoint remoteEndPoint)
    {
        DiscoveryProtocol.Message? message;
        try { message = DiscoveryProtocol.Decode(data); }
        catch (JsonException) { return; } // not one of ours — ignore, don't crash the loop.

        switch (message)
        {
            case DiscoveryProtocol.BeaconMessage beacon:
                HandleBeacon(beacon, remoteEndPoint);
                break;
            case DiscoveryProtocol.PairResponseMessage response:
                HandlePairResponse(response);
                break;
        }
    }

    private void HandleBeacon(DiscoveryProtocol.BeaconMessage beacon, IPEndPoint remoteEndPoint)
    {
        bool changed;
        lock (_gate)
        {
            var updated = new DiscoveredTerminal(beacon.DeviceId, beacon.DeviceName, remoteEndPoint.Address, DateTimeOffset.Now);
            changed = !_terminals.TryGetValue(beacon.DeviceId, out var existing)
                || existing.DeviceName != updated.DeviceName
                || !existing.Address.Equals(updated.Address);
            _terminals[beacon.DeviceId] = updated;
        }
        if (changed) TerminalListChanged?.Invoke();
    }

    private void HandlePairResponse(DiscoveryProtocol.PairResponseMessage response)
    {
        TaskCompletionSource<DiscoveryProtocol.PairResponseMessage>? tcs;
        lock (_gate) _pendingPairRequests.TryGetValue(response.RequestId, out tcs);
        tcs?.TrySetResult(response);
    }

    // Called with _gate already held (from GetTerminals()) — deliberately doesn't raise
    // TerminalListChanged itself: it exists to make polling callers (GetTerminals()) see a
    // consistent list on each call, not to push a notification from inside a query method while
    // holding the lock. A caller that wants prompt "a terminal just disappeared" updates should
    // poll GetTerminals() periodically rather than rely on an event for removals.
    private void PruneExpired()
    {
        var cutoff = DateTimeOffset.Now - ExpiryAfter;
        foreach (var id in _terminals.Where(kv => kv.Value.LastSeenAt < cutoff).Select(kv => kv.Key).ToList())
            _terminals.Remove(id);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _receiveLoop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cts.Dispose();
        _socket.Dispose();
    }
}
