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
    private readonly DeviceIdentity _identity;
    private readonly PairedTerminalStore _pairedTerminals;
    private readonly ReplayGuard _replayGuard = new();
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
    private readonly CastStatusSequenceTracker _statusSequences = new();

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

    // Bug fixed here: same "step one succeeds and gets kept, step two throws, nothing disposes
    // step one" shape as this project's Terminal-side counterpart, Terminal.Devices.DiscoveryService's
    // own constructor fix (see that project's README) — new UdpClient() above succeeds and
    // allocates a live native socket handle before Bind() ever runs. DiscoveryProtocol.Port is a
    // fixed, well-known port, so Bind() can genuinely throw SocketException (address already in
    // use — a second Caster instance on this machine, a leftover process from a previous crash
    // still holding the port, ReuseAddress does not guarantee a successful bind) — a real,
    // reachable failure mode, not hypothetical. Without this try/catch, that failure would leave
    // the constructor throwing before it ever finishes, so the caller never receives an instance
    // and can never call Dispose() — the UdpClient just constructed above leaks for the rest of the
    // process's lifetime.
    public TerminalDiscoveryClient(DeviceIdentity identity, PairedTerminalStore pairedTerminals)
    {
        _identity = identity;
        _pairedTerminals = pairedTerminals;
        var socket = new UdpClient();
        try
        {
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.Port));
            socket.EnableBroadcast = true;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        _socket = socket;
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

    // Bug fixed here, see this project's README: must be >= Terminal.Devices.DiscoveryService's own
    // PendingRequestTimeout (2 minutes — how long it keeps a PairRequestMessage waiting for someone
    // to actually click through PairingConfirmationDialog) plus a little margin for that Terminal's
    // own pruning only running once per BeaconInterval (3s), not continuously. Kept as an
    // independently-declared constant here rather than shared across projects (same "two small,
    // independent implementations" convention this codebase already uses elsewhere) — MUST be kept
    // in sync by hand with DiscoveryService.PendingRequestTimeout if either one ever changes.
    private static readonly TimeSpan DefaultPairingRequestTimeout = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(15);

    /// <summary>Sends a pairing request and awaits the Terminal's response. Returns null on timeout
    /// (no distinction from "declined" at the caller level today — see this project's README on why).
    /// The default timeout deliberately matches (with margin) how long the Terminal itself is
    /// willing to wait for a human to actually click through its confirmation dialog — see
    /// <see cref="DefaultPairingRequestTimeout"/>'s own comment for why a much shorter one used to
    /// undermine that Terminal-side generosity entirely.</summary>
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

            using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultPairingRequestTimeout);
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
            if (message is DiscoveryProtocol.AuthenticatedMessage authenticated)
            {
                string? key = _pairedTerminals.Find(terminal.DeviceId)?.PairingKey;
                if (!PairingSecurity.IsValidKey(key)) return;
                PairingSecurity.Sign(authenticated, _identity.DeviceId, key!);
            }
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
            catch (ObjectDisposedException)
            {
                // Bug found (self-review audit) and fixed here: this class's own Dispose() calls
                // _cts.Cancel() then waits on _receiveLoop with an unchecked timeout before disposing
                // _socket regardless of whether that wait actually succeeded — see
                // EveryStage.Transport.RtpReceiver.ReceiveLoopAsync's identical catch clause (found in
                // the same audit, which turned up this exact shape in four sibling classes across both
                // this repo's shared library and both its Terminal/Caster sides) for the full
                // reasoning. If ReceiveAsync(token) is ever slower to react to cancellation than
                // Dispose()'s timeout, the socket can be torn down while this exact await is still
                // pending, surfacing as ObjectDisposedException rather than OperationCanceledException.
                // Treated the same as cancellation.
                return;
            }

            HandleDatagram(result.Buffer, result.RemoteEndPoint);
        }
    }

    private void HandleDatagram(byte[] data, IPEndPoint remoteEndPoint)
    {
        DiscoveryProtocol.Message? message;
        try { message = DiscoveryProtocol.Decode(data); }
        catch (JsonException) { return; } // not one of ours — ignore, don't crash the loop.

        try
        {
            switch (message)
            {
                case DiscoveryProtocol.BeaconMessage beacon:
                    HandleBeacon(beacon, remoteEndPoint);
                    break;
                case DiscoveryProtocol.PairResponseMessage response:
                    HandlePairResponse(response);
                    break;
                case DiscoveryProtocol.CastStatusMessage status:
                    HandleCastStatus(status, remoteEndPoint);
                    break;
                case DiscoveryProtocol.PongMessage pong:
                    HandlePong(pong);
                    break;
                case DiscoveryProtocol.PingMessage ping:
                    HandlePing(ping, remoteEndPoint);
                    break;
            }
        }
        catch (Exception)
        {
            // This switch is called directly from ReceiveLoopAsync's while loop with nothing else
            // wrapping it — the try/catch above this one only ever protected Decode() itself, not
            // what happens with whatever Decode successfully returns. Any of these five handlers
            // throwing (CastStatusReceived's only subscriber, LiveCastSession.OnCastStatusReceived,
            // throwing; HandleBeacon/HandlePairResponse's own internal logic; etc.) would otherwise
            // propagate straight out of HandleDatagram and out of the while loop's body, permanently
            // ending this Caster's entire discovery receive loop for the rest of the process's life —
            // the same "one bad datagram kills a whole background loop forever with zero visible
            // symptom" shape as the DiscoveryProtocol.Decode bug this project's README already
            // documents fixing, just one level further down the same call path than that fix reached.
            // Same fix, same reasoning, on this class's own README-documented mirror of Terminal's
            // DiscoveryService.HandleDatagram. One datagram's handler failing is best-effort, like
            // every other discovery-protocol interaction in this repo — it must not cost every
            // SUBSEQUENT datagram (including, critically, this Caster's own live cast's status
            // reports) its chance to be handled too.
        }
    }

    /// <summary>Echoes any ping addressed to this Caster, unconditionally — the mirror-image of
    /// Terminal's own <c>DiscoveryService.HandlePing</c>, needed now that pinging is symmetric
    /// (<c>DiscoveryService.PingAsync</c> lets a Terminal measure RTT to a Caster it's receiving a
    /// cast from, the same way <see cref="PingAsync"/> here already let a Caster measure RTT to a
    /// Terminal). Same "no pairing/trust check" reasoning as the Terminal side's own doc comment —
    /// this protocol has no authentication at all, so gating one message type wouldn't meaningfully
    /// change this Caster's exposure.</summary>
    private void HandlePing(DiscoveryProtocol.PingMessage ping, IPEndPoint remoteEndPoint) =>
        _ = SendRawAsync(new DiscoveryProtocol.PongMessage { RequestId = ping.RequestId }, remoteEndPoint);

    private void HandleCastStatus(DiscoveryProtocol.CastStatusMessage status, IPEndPoint remoteEndPoint)
    {
        string? key = _pairedTerminals.Find(status.DeviceId)?.PairingKey;
        if (!PairingSecurity.Verify(status, key)) return;

        var ack = new DiscoveryProtocol.CastStatusAckMessage
        {
            DeviceId = status.DeviceId,
            SessionId = status.SessionId,
            SequenceNumber = status.SequenceNumber,
        };
        PairingSecurity.Sign(ack, _identity.DeviceId, key!);
        _ = SendRawAsync(ack, remoteEndPoint);

        if (!_replayGuard.TryAccept(status, DateTimeOffset.UtcNow)) return;
        if (_statusSequences.TryAccept(status.DeviceId, status.SessionId, status.SequenceNumber))
            CastStatusReceived?.Invoke(status);
    }

    /// <summary>Sends directly to an already-known <see cref="IPEndPoint"/> (a ping's own sender
    /// address+port) rather than through <see cref="SendControlMessageAsync"/>, which only knows how
    /// to address a <see cref="DiscoveredTerminal"/> (always at <see cref="DiscoveryProtocol.Port"/>)
    /// — a reply must go back to the exact port the request actually arrived from, which happens to
    /// be the same port here but isn't guaranteed to be in general.</summary>
    private async Task SendRawAsync(DiscoveryProtocol.Message message, IPEndPoint destination)
    {
        try
        {
            byte[] payload = DiscoveryProtocol.Encode(message);
            await _socket.SendAsync(payload, payload.Length, destination);
        }
        catch (SocketException) { } // best-effort — same reasoning as every other send in this class.
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
