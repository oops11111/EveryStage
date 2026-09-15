using System.Diagnostics;
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

    // Same correlation-by-RequestId pattern as _pendingPairRequests, for PingAsync/HandlePong — see
    // PingMessage's own doc comment for why this exists alongside CastStatusMessage.SentAtUtc's
    // clock-skew-sensitive latency estimate. The Stopwatch is started right before the ping is sent
    // and read (Elapsed) the moment the matching pong arrives — entirely on this machine's own
    // clock, never compared against the Terminal's.
    private readonly Dictionary<string, (TaskCompletionSource<TimeSpan> Tcs, Stopwatch Stopwatch)> _pendingPings = new();

    private Task? _receiveLoop;

    /// <summary>Raised (from a background task — marshal to the UI thread before touching UI) when
    /// a terminal is newly seen, changes name, or is pruned as expired.</summary>
    public event Action? TerminalListChanged;

    /// <summary>A Terminal sent a periodic cast status report (see
    /// <see cref="DiscoveryProtocol.CastStatusMessage"/>) — raised for every one received, from
    /// whichever terminal, since this class doesn't track "who am I currently casting to" itself;
    /// that filtering is <c>Casting.LiveCastSession</c>'s job. Marshal to the UI thread before
    /// touching UI.</summary>
    public event Action<DiscoveryProtocol.CastStatusMessage>? CastStatusReceived;

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

    /// <summary>Measures real round-trip time to <paramref name="terminal"/> — see
    /// <see cref="DiscoveryProtocol.PingMessage"/>'s own doc comment for why this exists alongside
    /// <see cref="DiscoveryProtocol.CastStatusMessage.SentAtUtc"/>'s clock-skew-sensitive estimate.
    /// Returns null on timeout, same "no distinction from a lost packet" reasoning as
    /// <see cref="RequestPairingAsync"/> — a single missed ping isn't meaningful on its own, callers
    /// expecting to ping periodically (e.g. <c>Casting.LiveCastSession</c>) should just try again
    /// next cycle rather than treat one null as "the Terminal is unreachable".</summary>
    public async Task<TimeSpan?> PingAsync(DiscoveredTerminal terminal, TimeSpan? timeout = null)
    {
        string requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = new Stopwatch();
        lock (_gate) _pendingPings[requestId] = (tcs, stopwatch);

        try
        {
            var ping = new DiscoveryProtocol.PingMessage { RequestId = requestId };
            byte[] payload = DiscoveryProtocol.Encode(ping);
            stopwatch.Start(); // started as close to the actual send as practical, not at method entry.
            await _socket.SendAsync(payload, payload.Length, new IPEndPoint(terminal.Address, DiscoveryProtocol.Port));

            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                return null; // Terminal never answered (offline, this ping or its pong got lost).
            }
        }
        finally
        {
            lock (_gate) _pendingPings.Remove(requestId);
        }
    }

    /// <summary>One audio stream's parameters for <see cref="SendCastStartAsync"/> — null means
    /// "video only", e.g. because <c>AudioCaptureSource</c>/<c>AacAudioEncoder</c> construction
    /// failed. <paramref name="IsAac"/> mirrors <see cref="DiscoveryProtocol.CastStartMessage.AudioIsAac"/>
    /// — see that property's own doc comment.</summary>
    public readonly record struct AudioStreamInfo(int SampleRate, int Channels, byte PayloadType, bool IsAac);

    /// <summary>Tells the Terminal a live RTP/H.264 stream (and, optionally, an accompanying raw-PCM
    /// audio stream) is about to start on <see cref="DiscoveryProtocol.VideoRtpPort"/>/
    /// <see cref="DiscoveryProtocol.AudioRtpPort"/>, and at what resolution/format — see
    /// <see cref="DiscoveryProtocol.CastStartMessage"/> for why this exists instead of the Terminal
    /// inferring a stream's parameters purely from incoming RTP packets. Best-effort, fire-and-forget
    /// like every other send in this class — there's no acknowledgment or retry.</summary>
    public Task SendCastStartAsync(DiscoveredTerminal terminal, DeviceIdentity myIdentity, int width, int height, byte payloadType, AudioStreamInfo? audio) =>
        SendControlMessageAsync(terminal, new DiscoveryProtocol.CastStartMessage
        {
            DeviceId = myIdentity.DeviceId,
            Width = width,
            Height = height,
            PayloadType = payloadType,
            HasAudio = audio.HasValue,
            AudioSampleRate = audio?.SampleRate ?? 0,
            AudioChannels = audio?.Channels ?? 0,
            AudioPayloadType = audio?.PayloadType ?? 0,
            AudioIsAac = audio?.IsAac ?? false,
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
            case DiscoveryProtocol.CastStatusMessage status:
                CastStatusReceived?.Invoke(status);
                break;
            case DiscoveryProtocol.PongMessage pong:
                HandlePong(pong);
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

    private void HandlePong(DiscoveryProtocol.PongMessage pong)
    {
        (TaskCompletionSource<TimeSpan> Tcs, Stopwatch Stopwatch)? entry = null;
        lock (_gate)
        {
            if (_pendingPings.TryGetValue(pong.RequestId, out var found)) entry = found;
        }
        // Elapsed read here, at the moment the pong actually arrives — not inside PingAsync after
        // WaitAsync returns, which would also include whatever delay TaskCompletionSource's
        // continuation scheduling adds on top of the real network round trip.
        if (entry != null) entry.Value.Tcs.TrySetResult(entry.Value.Stopwatch.Elapsed);
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
