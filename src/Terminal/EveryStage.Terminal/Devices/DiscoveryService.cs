using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EveryStage.Discovery;
using EveryStage.Terminal.Logging;

namespace EveryStage.Terminal.Devices;

/// <summary>
/// Terminal-side LAN discovery/pairing (PLANNING.md §7) over the draft UDP protocol in
/// <see cref="DiscoveryProtocol"/>. Broadcasts a presence beacon so Caster-side discovery UI can
/// find this Terminal, and listens for pairing requests. Pairing always requires a human decision:
/// DeviceId alone is public and spoofable, so it must never authorize issuing or rotating a key.
///
/// Runs its send/receive loops as background <see cref="Task"/>s, following the same
/// background-thread pattern <c>VideoContentController</c> uses for its own reasons (the UI thread
/// must not block on network I/O).
/// </summary>
public sealed class DiscoveryService : IDeviceCastTransport, IDisposable
{
    private static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PendingRequestTimeout = TimeSpan.FromMinutes(2);

    private sealed class PendingRequest : IDisposable
    {
        public PendingRequest(IPEndPoint remoteEndPoint, Guid deviceId, string deviceName, DateTimeOffset receivedAt, TerminalPairingHandshake handshake)
        {
            RemoteEndPoint = remoteEndPoint;
            DeviceId = deviceId;
            DeviceName = deviceName;
            ReceivedAt = receivedAt;
            Handshake = handshake;
        }

        public IPEndPoint RemoteEndPoint { get; }
        public Guid DeviceId { get; }
        public string DeviceName { get; }
        public DateTimeOffset ReceivedAt { get; }
        public TerminalPairingHandshake Handshake { get; }
        public PairingHandshakeResult? Result { get; set; }
        public bool UiRaised { get; set; }

        public void Dispose() => Handshake.Dispose();
    }

    private readonly DeviceIdentity _identity;
    private readonly PairedDeviceStore _pairedDevices;
    private readonly DeviceConnectionLogger _connectionLog;
    private readonly UdpClient _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _pendingGate = new();
    private Guid _lastStartMessageId;
    private Guid _lastStartDeviceId;
    private Task<CastStartResult>? _lastStartResult;
    private readonly Dictionary<string, PendingRequest> _pendingRequests = new();
    private readonly Dictionary<string, (DiscoveryProtocol.PairResponseMessage Response, IPEndPoint RemoteEndPoint, DateTimeOffset CreatedAt)> _pairResponseCache = new();
    private readonly Dictionary<(IPAddress Address, string Reason), DateTimeOffset> _lastCastStartRejection = new();

    // Same correlation-by-RequestId pattern as _pendingRequests/Caster's own _pendingPairRequests,
    // for PingAsync/HandlePong — see DiscoveryProtocol.PingMessage's doc comment for why this exists
    // symmetrically on this side too now: originally only a Caster could ping a Terminal (for its
    // own live "真实RTT估算" UI); this lets a Terminal measure RTT to the Caster it's currently
    // receiving a cast from, for DeviceConnectionLogger.LogQualityMetric (PLANNING.md §14.4's
    // "连接质量指标（丢包率/延迟）", previously logged by nothing at all — see this project's README).
    private readonly Dictionary<string, (TaskCompletionSource<TimeSpan> Tcs, Stopwatch Stopwatch)> _pendingPings = new();
    private readonly Dictionary<(Guid DeviceId, Guid SessionId, long SequenceNumber), TaskCompletionSource<bool>> _pendingStatusAcks = new();
    private readonly Guid _statusSessionId = Guid.NewGuid();
    private long _nextStatusSequence;
    private readonly ReplayGuard _replayGuard = new();
    private Guid? _activeCasterDeviceId;

    // Last verified contact per paired device (address + time) for the 设备 page. In memory only:
    // writing the pairing store on every status ack would wear it for no benefit.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (IPAddress Address, DateTimeOffset At)> _sightings = new();

    /// <summary>The last verified message from <paramref name="deviceId"/> this run: its address and time.</summary>
    public (IPAddress Address, DateTimeOffset At)? LastSighting(Guid deviceId) =>
        _sightings.TryGetValue(deviceId, out var sighting) ? sighting : null;

    /// <summary>The paired device whose cast is live right now, if any.</summary>
    public Guid? ActiveCasterDeviceId => _activeCasterDeviceId;
    private string? _activeCasterKey;

    private Task? _receiveLoop;
    private Task? _beaconLoop;
    private int _disposed;
    private readonly int _port;

    public int LocalPort => _port;

    /// <summary>Raised (from a background task — marshal to the UI thread if the handler touches
    /// UI) for any pairing request from a device that isn't already trusted.</summary>
    public event Action<PairingRequest>? PairingRequested;

    /// <summary>A paired device with <c>AllowCast</c> announced it's about to start streaming (see
    /// <see cref="DiscoveryProtocol.CastStartMessage"/>). Raised from a background task — marshal to
    /// the UI thread, since acting on it means showing the overlay and starting a decoder.</summary>
    public event Action<CastStartInfo>? CastStartRequested;

    /// <summary>The device that most recently started casting announced it stopped (see
    /// <see cref="DiscoveryProtocol.CastStopMessage"/>). Same marshaling caveat as
    /// <see cref="CastStartRequested"/>.</summary>
    public event Action<Guid>? CastStopRequested;

    /// <summary>Audio fields are meaningless when <see cref="HasAudio"/> is false (the Caster
    /// couldn't start audio capture) — see <see cref="DiscoveryProtocol.CastStartMessage"/>.
    /// <paramref name="CasterEndPoint"/> (really just its address — the port is always
    /// <see cref="DiscoveryProtocol.Port"/>) is where <see cref="SendCastStatusAsync"/> reports
    /// back to.</summary>
    public readonly record struct CastStartInfo(
        Guid DeviceId, string DeviceName, int Width, int Height, byte PayloadType,
        bool HasAudio, int AudioSampleRate, int AudioChannels, byte AudioPayloadType, bool AudioIsAac,
        IPEndPoint CasterEndPoint, Guid MediaSessionId, byte[] VideoKey, byte[] AudioKey,
        Action<bool, string?, bool> CompleteStart);

    private readonly record struct CastStartResult(bool Ready, string? Error, bool Pending);

    // Bug fixed here: same "step one succeeds and gets kept, step two throws, nothing disposes
    // step one" shape as CastReceiver's own RtpReceiver construction fix (see this project's
    // README) — new UdpClient() above succeeds and allocates a live native socket handle before
    // Bind() ever runs. DiscoveryProtocol.Port is a fixed, well-known port, so Bind() can genuinely
    // throw SocketException (address already in use — a second Terminal instance on this machine,
    // a leftover process from a previous crash still holding the port, ReuseAddress does not
    // guarantee a successful bind) — a real, reachable failure mode, not hypothetical. Without this
    // try/catch, that failure would leave the constructor throwing before it ever finishes, so the
    // caller (Program.cs's `new DiscoveryService(...)`) never receives an instance and can never
    // call Dispose() — the UdpClient just constructed above leaks for the rest of the process's
    // lifetime. This is a distinct issue from the "TerminalApplicationContext doesn't catch a
    // DiscoveryService construction failure" gap already documented in this project's README: even
    // once that outer call is wrapped in try/catch, the caller still never gets this object back to
    // dispose it — the leak has to be closed here, at the point where the partially-constructed
    // resource is still reachable.
    public DiscoveryService(DeviceIdentity identity, PairedDeviceStore pairedDevices, DeviceConnectionLogger connectionLog, int? portOverride = null)
    {
        _identity = identity;
        _pairedDevices = pairedDevices;
        _connectionLog = connectionLog;
        int port = portOverride ?? DiscoveryProtocol.Port;

        var socket = new UdpClient();
        try
        {
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            socket.EnableBroadcast = true;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        _socket = socket;
    }

    public void Start()
    {
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _beaconLoop = Task.Run(() => BeaconLoopAsync(_cts.Token));
    }

    /// <summary>Called by the pairing confirmation UI after the operator compares the v2 six-digit
    /// verification code. Returns false (and does nothing) if <paramref name="requestId"/> is unknown or
    /// already expired/answered, so the UI can tell an operator whose answer came too late.</summary>
    public bool RespondToPairing(string requestId, bool accept, TrustMode trustMode, bool allowCast, bool allowMonitor)
    {
        PendingRequest? pending;
        lock (_pendingGate)
        {
            if (!_pendingRequests.Remove(requestId, out pending)) return false;
        }
        using (pending)
        {
            if (accept && pending.Result == null) accept = false;

            var response = new DiscoveryProtocol.PairResponseMessage
            {
                RequestId = requestId,
                Accepted = accept,
                Reason = accept ? null : "declined_by_terminal",
                PairingKey = null,
                PairingKeyFormatVersion = accept ? PairingSecurity.CurrentKeyFormatVersion : 0,
                KeyConfirmation = accept ? pending.Result?.KeyConfirmation : null,
            };
            _ = SendAsync(response, pending.RemoteEndPoint);
            lock (_pendingGate)
                _pairResponseCache[requestId] = (response, pending.RemoteEndPoint, DateTimeOffset.Now);

            if (accept && pending.Result != null)
            {
                _pairedDevices.Upsert(new PairedDevice
                {
                    DeviceId = pending.DeviceId,
                    DeviceName = pending.DeviceName,
                    TrustMode = trustMode,
                    AllowCast = allowCast,
                    AllowMonitor = allowMonitor,
                    PairedAt = DateTimeOffset.Now,
                    PairingKey = pending.Result.PairingKey,
                    PairingKeyFormatVersion = response.PairingKeyFormatVersion,
                });
                _connectionLog.LogPaired(pending.DeviceId.ToString(), pending.DeviceName);
                _connectionLog.LogConnected(pending.DeviceId.ToString());
            }
            else
            {
                _connectionLog.LogDisconnected(pending.DeviceId.ToString(), "pairing_declined");
            }
        }
        return true;
    }

    private async Task BeaconLoopAsync(CancellationToken token)
    {
        var broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, _port);

        while (!token.IsCancellationRequested)
        {
            // Built fresh each tick rather than once before the loop — _identity.DeviceName can be
            // changed at runtime (the 设置 panel's "设备名称" field, via DeviceIdentity.Save()), and
            // a beacon built once up front would keep broadcasting the name this Terminal had at
            // startup forever, silently ignoring the rename.
            var beacon = new DiscoveryProtocol.BeaconMessage
            {
                DeviceId = _identity.DeviceId,
                DeviceName = _identity.DeviceName,
                MediaAuthenticationVersion = 1,
                PairingProtocolVersion = PairingHandshake.ProtocolVersion,
            };
            await SendAsync(beacon, broadcastEndPoint);
            PruneExpiredPendingRequests();

            try { await Task.Delay(BeaconInterval, token); }
            catch (OperationCanceledException) { }
        }
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
                // the same audit) for the full reasoning. If ReceiveAsync(token) is ever slower to
                // react to cancellation than Dispose()'s timeout, the socket can be torn down while
                // this exact await is still pending, surfacing as ObjectDisposedException rather than
                // OperationCanceledException. Treated the same as cancellation.
                return;
            }

            HandleDatagram(result.Buffer, result.RemoteEndPoint);
        }
    }

    private void HandleDatagram(byte[] data, IPEndPoint remoteEndPoint)
    {
        DiscoveryProtocol.Message? message;
        try { message = DiscoveryProtocol.Decode(data); }
        catch (System.Text.Json.JsonException) { return; } // not one of ours — ignore, don't crash the loop.

        try
        {
            switch (message)
            {
                case DiscoveryProtocol.PairRequestMessage req:
                    HandlePairRequest(req, remoteEndPoint);
                    break;
                case DiscoveryProtocol.PairRevealMessage reveal:
                    HandlePairReveal(reveal, remoteEndPoint);
                    break;
                case DiscoveryProtocol.CastStartMessage start:
                    HandleCastStart(start, remoteEndPoint);
                    break;
                case DiscoveryProtocol.CastStopMessage stop:
                    HandleCastStop(stop);
                    break;
                case DiscoveryProtocol.PingMessage ping:
                    HandlePing(ping, remoteEndPoint);
                    break;
                case DiscoveryProtocol.PongMessage pong:
                    HandlePong(pong);
                    break;
                case DiscoveryProtocol.CastStatusAckMessage ack:
                    HandleCastStatusAck(ack);
                    break;
                // BeaconMessage: this is the Terminal side, which only ever sends beacons, never needs
                // to react to one — that's Caster-side discovery UI's job.
            }
        }
        catch (Exception)
        {
            // This switch is called directly from ReceiveLoopAsync's while loop with nothing else
            // wrapping it — the try/catch above this one only ever protected Decode() itself, not
            // what happens with whatever Decode successfully returns. Any of these five handlers
            // throwing (a subscriber to PairingRequested/CastStartRequested/CastStopRequested
            // throwing, HandlePairRequest's own internal logic, etc.) would otherwise propagate
            // straight out of HandleDatagram and out of the while loop's body, permanently ending
            // this Terminal's entire discovery receive loop for the rest of the process's life — the
            // same "one bad datagram kills a whole background loop forever with zero visible symptom"
            // shape as the DiscoveryProtocol.Decode bug this project's README already documents
            // fixing, just one level further down the same call path than that fix reached. One
            // datagram's handler failing is best-effort, like every other discovery-protocol
            // interaction in this repo — it must not cost every SUBSEQUENT datagram its chance to be
            // handled too.
        }
    }

    /// <summary>Echoes any ping addressed to this Terminal, unconditionally — see
    /// <see cref="DiscoveryProtocol.PingMessage"/>'s own doc comment on why no pairing/trust check
    /// gates this (this whole protocol is already unauthenticated).</summary>
    private void HandlePing(DiscoveryProtocol.PingMessage ping, IPEndPoint remoteEndPoint) =>
        _ = SendAsync(new DiscoveryProtocol.PongMessage { RequestId = ping.RequestId }, remoteEndPoint);

    private void HandleCastStart(DiscoveryProtocol.CastStartMessage msg, IPEndPoint remoteEndPoint)
    {
        if (msg.MediaAuthenticationVersion != 1 || msg.MediaSessionId == Guid.Empty || msg.DeviceId != msg.SenderDeviceId) return;
        var device = _pairedDevices.Find(msg.SenderDeviceId);
        if (device == null || !PairingSecurity.IsSupportedKey(device.PairingKey, device.PairingKeyFormatVersion))
        {
            _ = SendCastStartRejectedAsync(msg, remoteEndPoint, "not_paired");
            return;
        }
        if (!PairingSecurity.Verify(msg, device.PairingKey))
        {
            _ = SendCastStartRejectedAsync(msg, remoteEndPoint, "not_paired");
            return;
        }
        if (!device.AllowCast)
        {
            _ = SendCastStartRejectedAsync(msg, remoteEndPoint, "not_allowed");
            _connectionLog.LogDisconnected(msg.DeviceId.ToString(), "cast_start_rejected_not_allowed");
            return;
        }
        if (msg.MessageId == _lastStartMessageId && _lastStartResult != null)
        {
            // Idempotent retry: this exact cast_start was already acted on, so re-send the
            // cached outcome instead of starting a second cast. De-duplication is deliberately
            // skipped here - a retry IS a duplicate MessageId, that is the whole point - but
            // freshness is NOT. Without this check the branch returned before _replayGuard ran
            // at all, which meant a captured cast_start stayed replayable forever (the memo
            // below is never cleared while a session lasts), each replay eliciting a signed
            // cast_start_ack to whatever source address the datagram claimed. That bypassed the
            // only bound this protocol places on replay age.
            if (!ReplayGuard.IsFresh(msg, DateTimeOffset.UtcNow)) return;
            _ = SendStartResultAsync(msg, device.PairingKey!, remoteEndPoint, _lastStartResult);
            return;
        }
        if (!AcceptFresh(msg, "cast_start")) return;
        _sightings[msg.SenderDeviceId] = (remoteEndPoint.Address, DateTimeOffset.Now);
        _lastStartMessageId = msg.MessageId;
        _lastStartDeviceId = msg.SenderDeviceId;
        void Complete(bool ready, string? error, bool pending = false)
        {
            if (!pending && ready)
            {
                _activeCasterDeviceId = msg.SenderDeviceId;
                _activeCasterKey = device.PairingKey;
            }
            var task = Task.FromResult(new CastStartResult(ready, error, pending));
            _lastStartResult = task;
            _ = SendStartResultAsync(msg, device.PairingKey!, remoteEndPoint, task);
        }
        if (CastStartRequested == null) { Complete(false, "终端接收服务尚未就绪。"); return; }
        try { CastStartRequested.Invoke(new CastStartInfo(
            msg.DeviceId, device.DeviceName, msg.Width, msg.Height, msg.PayloadType,
            msg.HasAudio, msg.AudioSampleRate, msg.AudioChannels, msg.AudioPayloadType, msg.AudioIsAac,
            remoteEndPoint, msg.MediaSessionId,
            PairingSecurity.DeriveMediaKey(device.PairingKey!, msg.MediaSessionId, "video"),
            PairingSecurity.DeriveMediaKey(device.PairingKey!, msg.MediaSessionId, "audio"),
            Complete)); }
        catch (Exception ex) { Complete(false, ex.Message); }
    }

    private async Task SendCastStartRejectedAsync(DiscoveryProtocol.CastStartMessage request, IPEndPoint endpoint, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_pendingGate)
        {
            var key = (endpoint.Address, reason);
            if (_lastCastStartRejection.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(10)) return;
            _lastCastStartRejection[key] = now;
            foreach (var stale in _lastCastStartRejection.Where(kv => now - kv.Value > PendingRequestTimeout).Select(kv => kv.Key).ToList())
                _lastCastStartRejection.Remove(stale);
        }

        await SendAsync(new DiscoveryProtocol.CastStartRejectedMessage
        {
            TerminalDeviceId = _identity.DeviceId,
            CasterDeviceId = request.DeviceId,
            MediaSessionId = request.MediaSessionId,
            Reason = reason,
        }, endpoint);
    }

    private async Task SendStartResultAsync(DiscoveryProtocol.CastStartMessage request, string key,
        IPEndPoint endpoint, Task<CastStartResult> completion)
    {
        try
        {
            var result = await completion.WaitAsync(TimeSpan.FromSeconds(10), _cts.Token);
            var ack = new DiscoveryProtocol.CastStartAckMessage
            { MediaSessionId = request.MediaSessionId, Ready = result.Ready, Pending = result.Pending, Error = result.Error };
            PairingSecurity.Sign(ack, _identity.DeviceId, key);
            await SendAsync(ack, endpoint);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or ObjectDisposedException or SocketException) { }
    }

    /// <summary>Sends a periodic "still alive, here's roughly what's gotten through" status report
    /// back to the Caster currently casting to this Terminal — see
    /// <see cref="DiscoveryProtocol.CastStatusMessage"/> for why this exists. Each report is retried
    /// a bounded number of times until its matching ACK arrives.</summary>
    public async Task SendCastStatusAsync(IPEndPoint casterEndPoint, DiscoveryProtocol.CastStatusMessage status)
    {
        status.SequenceNumber = Interlocked.Increment(ref _nextStatusSequence);
        status.SessionId = _statusSessionId;
        if (_activeCasterDeviceId == null || !PairingSecurity.IsValidKey(_activeCasterKey)) return;
        PairingSecurity.Sign(status, _identity.DeviceId, _activeCasterKey!);
        var key = (status.DeviceId, status.SessionId, status.SequenceNumber);
        var acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate) _pendingStatusAcks[key] = acknowledged;

        try
        {
            await AcknowledgedMessageRetry.SendUntilAcknowledgedAsync(
                () => SendAsync(status, casterEndPoint), acknowledged.Task,
                cancellationToken: _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        finally
        {
            lock (_pendingGate) _pendingStatusAcks.Remove(key);
        }
    }

    public async Task SendCastNackAsync(IPEndPoint casterEndPoint, Guid mediaSessionId,
        IReadOnlyList<ushort> missingVideoSequences)
    {
        if (_activeCasterDeviceId == null || !PairingSecurity.IsValidKey(_activeCasterKey)
            || mediaSessionId == Guid.Empty || missingVideoSequences.Count == 0) return;
        var nack = new DiscoveryProtocol.CastNackMessage
        {
            DeviceId = _activeCasterDeviceId.Value,
            MediaSessionId = mediaSessionId,
            MissingVideoSequences = missingVideoSequences.Take(64).ToArray(),
        };
        PairingSecurity.Sign(nack, _identity.DeviceId, _activeCasterKey!);
        await SendAsync(nack, casterEndPoint);
    }

    private void HandleCastStatusAck(DiscoveryProtocol.CastStatusAckMessage ack)
    {
        if (_activeCasterDeviceId == null || ack.SenderDeviceId != _activeCasterDeviceId
            || !PairingSecurity.Verify(ack, _activeCasterKey)
            || !AcceptFresh(ack, "cast_status_ack")) return;
        if (_sightings.TryGetValue(ack.SenderDeviceId, out var seen)) _sightings[ack.SenderDeviceId] = (seen.Address, DateTimeOffset.Now);
        TaskCompletionSource<bool>? pending;
        lock (_pendingGate)
            _pendingStatusAcks.TryGetValue((ack.DeviceId, ack.SessionId, ack.SequenceNumber), out pending);
        pending?.TrySetResult(true);
    }

    /// <summary>Measures real round-trip time to <paramref name="casterEndPoint"/> — the Terminal-
    /// initiated mirror of <c>Caster.Discovery.TerminalDiscoveryClient.PingAsync</c> (see that
    /// method's and <see cref="DiscoveryProtocol.PingMessage"/>'s own doc comments). Returns null on
    /// timeout, same "a single missed ping isn't meaningful on its own" reasoning as that mirror
    /// method — callers pinging periodically should just try again next cycle.</summary>
    public async Task<TimeSpan?> PingAsync(IPEndPoint casterEndPoint, TimeSpan? timeout = null)
    {
        string requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = new Stopwatch();
        lock (_pendingGate) _pendingPings[requestId] = (tcs, stopwatch);

        try
        {
            stopwatch.Start(); // started as close to the actual send as practical, not at method entry.
            await SendAsync(new DiscoveryProtocol.PingMessage { RequestId = requestId }, casterEndPoint);

            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                return null; // Caster never answered (this ping or its pong got lost).
            }
        }
        finally
        {
            lock (_pendingGate) _pendingPings.Remove(requestId);
        }
    }

    private void HandlePong(DiscoveryProtocol.PongMessage pong)
    {
        (TaskCompletionSource<TimeSpan> Tcs, Stopwatch Stopwatch)? entry = null;
        lock (_pendingGate)
        {
            if (_pendingPings.TryGetValue(pong.RequestId, out var found)) entry = found;
        }
        if (entry != null) entry.Value.Tcs.TrySetResult(entry.Value.Stopwatch.Elapsed);
    }

    /// <summary>The replay/freshness check for an already-authenticated message, with a log line when it
    /// refuses: these refusals used to be bare returns, so a Caster whose clock is more than two minutes
    /// off — every signed message refused — left no trace at all on the Terminal (audit A-14 / C-11).
    /// At most one line per message kind every 10 s, so a replay flood cannot fill the disk.</summary>
    private bool AcceptFresh(DiscoveryProtocol.AuthenticatedMessage message, string kind)
    {
        var now = DateTimeOffset.UtcNow;
        if (_replayGuard.TryAccept(message, now)) return true;
        lock (_rejectionLogGate)
        {
            if (_lastRejectionLog.TryGetValue(kind, out var last) && now - last < TimeSpan.FromSeconds(10)) return false;
            _lastRejectionLog[kind] = now;
        }
        string reason = ReplayGuard.IsFresh(message, now)
            ? "replayed_message"
            : $"stale_or_clock_skew: sender clock {message.IssuedAtUtc:O}, terminal clock {now:O}, difference {(message.IssuedAtUtc - now).TotalSeconds:F0} s";
        _connectionLog.LogMessageRejected(message.SenderDeviceId.ToString(), kind, reason);
        return false;
    }

    private readonly object _rejectionLogGate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastRejectionLog = new();

    private void HandleCastStop(DiscoveryProtocol.CastStopMessage msg)
    {
        var device = _pairedDevices.Find(msg.SenderDeviceId);
        if (msg.DeviceId != msg.SenderDeviceId
            || device == null
            || !PairingSecurity.IsSupportedKey(device.PairingKey, device.PairingKeyFormatVersion)
            || !PairingSecurity.Verify(msg, device.PairingKey)
            || !AcceptFresh(msg, "cast_stop")) return;
        CastStopRequested?.Invoke(msg.DeviceId);
        // Only the sender's own session is retired. A second Caster whose cast_start was declined
        // (or timed out) sends cast_stop on cleanup; clearing unconditionally dropped the key of
        // the Caster still casting, which silenced its status reports and NACKs.
        if (_activeCasterDeviceId == msg.SenderDeviceId)
        {
            _activeCasterDeviceId = null;
            _activeCasterKey = null;
        }
        // Retire the idempotent-retry memo with the session it belongs to. Leaving it armed kept
        // the fast path above answering replays of a cast_start whose cast had already ended.
        if (_lastStartDeviceId == msg.SenderDeviceId)
        {
            _lastStartMessageId = Guid.Empty;
            _lastStartResult = null;
        }
    }

    // Pairing requests are the one unauthenticated entry point (there is no key yet), so they are bounded
    // (audit A-22): a few pending at a time, one per sender address every few seconds, and attacker-
    // chosen strings cut to a sane length. A flood used to stack a modal dialog per datagram on an
    // unattended Terminal's UI thread, grow an unbounded dictionary and drown the connection log.
    private const int MaxPendingPairRequests = 4;
    private static readonly TimeSpan PairRequestInterval = TimeSpan.FromSeconds(5);
    private const int MaxRequestIdLength = 64;
    private const int MaxDeviceNameLength = 64;
    private readonly Dictionary<IPAddress, DateTimeOffset> _lastPairRequestFrom = new();

    private void HandlePairRequest(DiscoveryProtocol.PairRequestMessage req, IPEndPoint remoteEndPoint)
    {
        if (string.IsNullOrEmpty(req.RequestId) || req.RequestId.Length > MaxRequestIdLength) return;
        if (req.PairingProtocolVersion != PairingHandshake.ProtocolVersion
            || string.IsNullOrWhiteSpace(req.CasterPublicKey)
            || string.IsNullOrWhiteSpace(req.CasterNonceCommitment))
        {
            _ = SendAsync(new DiscoveryProtocol.PairResponseMessage
            {
                RequestId = req.RequestId,
                Accepted = false,
                Reason = "投屏机程序版本过旧，请更新后重新配对",
            }, remoteEndPoint);
            return;
        }

        string deviceName = (req.DeviceName ?? "").Trim();
        if (deviceName.Length > MaxDeviceNameLength) deviceName = deviceName[..MaxDeviceNameLength] + "…";
        var now = DateTimeOffset.Now;
        PendingRequest? pending;
        lock (_pendingGate)
        {
            if (_pendingRequests.TryGetValue(req.RequestId, out pending))
            {
                if (!pending.RemoteEndPoint.Address.Equals(remoteEndPoint.Address)) return;
            }
            else
            {
                if (_lastPairRequestFrom.TryGetValue(remoteEndPoint.Address, out var last) && now - last < PairRequestInterval) return;
                if (_pendingRequests.Count >= MaxPendingPairRequests) return;
                TerminalPairingHandshake handshake;
                try
                {
                    handshake = new TerminalPairingHandshake(
                        req.RequestId, req.DeviceId, _identity.DeviceId,
                        req.CasterPublicKey, req.CasterNonceCommitment);
                }
                catch (System.Security.Cryptography.CryptographicException) { return; }
                pending = new PendingRequest(remoteEndPoint, req.DeviceId, deviceName, now, handshake);
                _pendingRequests[req.RequestId] = pending;
            }
            _lastPairRequestFrom[remoteEndPoint.Address] = now;
            foreach (var stale in _lastPairRequestFrom.Where(kv => now - kv.Value > PendingRequestTimeout).Select(kv => kv.Key).ToList())
                _lastPairRequestFrom.Remove(stale);
        }

        _ = SendAsync(new DiscoveryProtocol.PairChallengeMessage
        {
            RequestId = req.RequestId,
            TerminalDeviceId = _identity.DeviceId,
            TerminalPublicKey = pending.Handshake.TerminalPublicKey,
            TerminalNonce = pending.Handshake.TerminalNonce,
        }, remoteEndPoint);
    }

    private void HandlePairReveal(DiscoveryProtocol.PairRevealMessage reveal, IPEndPoint remoteEndPoint)
    {
        if (string.IsNullOrEmpty(reveal.RequestId) || reveal.RequestId.Length > MaxRequestIdLength) return;
        PendingRequest? pending;
        lock (_pendingGate)
        {
            if (_pairResponseCache.TryGetValue(reveal.RequestId, out var cached))
            {
                _ = SendAsync(cached.Response, remoteEndPoint);
                return;
            }
            _pendingRequests.TryGetValue(reveal.RequestId, out pending);
        }
        if (pending == null || !pending.RemoteEndPoint.Address.Equals(remoteEndPoint.Address)) return;

        PairingHandshakeResult result;
        try { result = pending.Handshake.ProcessReveal(reveal.CasterNonce); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            lock (_pendingGate)
            {
                if (_pendingRequests.Remove(reveal.RequestId, out var removed)) removed.Dispose();
            }
            _connectionLog.LogDisconnected(pending.DeviceId.ToString(), "pairing_commitment_mismatch");
            return;
        }

        bool raise;
        lock (_pendingGate)
        {
            pending.Result = result;
            raise = !pending.UiRaised;
            pending.UiRaised = true;
        }
        if (raise)
            PairingRequested?.Invoke(new PairingRequest(
                reveal.RequestId, pending.DeviceId, pending.DeviceName,
                pending.RemoteEndPoint.Address.ToString(), result.VerificationCode));
    }

    private void PruneExpiredPendingRequests()
    {
        var now = DateTimeOffset.Now;
        var cutoff = now - PendingRequestTimeout;
        List<PendingRequest> expired;
        lock (_pendingGate)
        {
            var expiredKeys = _pendingRequests.Where(kv => kv.Value.ReceivedAt < cutoff).Select(kv => kv.Key).ToList();
            expired = expiredKeys.Select(key => _pendingRequests[key]).ToList();
            foreach (var key in expiredKeys)
                if (_pendingRequests.Remove(key, out var removed)) removed.Dispose();
            foreach (var stale in _pairResponseCache.Where(kv => now - kv.Value.CreatedAt > PendingRequestTimeout).Select(kv => kv.Key).ToList())
                _pairResponseCache.Remove(stale);
        }

        // A request nobody answered within the timeout is exactly the kind of thing PLANNING.md
        // §14.4's device-connection log exists to make visible after the fact — silently dropping
        // it would leave no trace that a Caster tried to pair and got left hanging.
        foreach (var request in expired)
            _connectionLog.LogDisconnected(request.DeviceId.ToString(), "pairing_request_timed_out");
    }

    private async Task SendAsync(DiscoveryProtocol.Message message, IPEndPoint destination)
    {
        try
        {
            byte[] payload = DiscoveryProtocol.Encode(message);
            await _socket.SendAsync(payload, payload.Length, destination);
        }
        catch (SocketException) { } // best-effort — a lost beacon/response isn't fatal, the next tick or retry covers it.
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();

        var runningLoops = new List<Task>();
        if (_receiveLoop != null) runningLoops.Add(_receiveLoop);
        if (_beaconLoop != null) runningLoops.Add(_beaconLoop);
        try { Task.WaitAll(runningLoops.ToArray(), TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }

        if (runningLoops.Count == 0 || runningLoops.All(loop => loop.IsCompleted))
            DisposeOwnedResources();
        else
        {
            var allLoops = Task.WhenAll(runningLoops);
            _ = allLoops.ContinueWith(_ => DisposeOwnedResources(),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void DisposeOwnedResources()
    {
        _cts.Dispose();
        _socket.Dispose();
    }
}
