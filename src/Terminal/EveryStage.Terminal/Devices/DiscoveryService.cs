using System.Net;
using System.Net.Sockets;
using EveryStage.Discovery;
using EveryStage.Terminal.Logging;

namespace EveryStage.Terminal.Devices;

/// <summary>
/// Terminal-side LAN discovery/pairing (PLANNING.md §7) over the draft UDP protocol in
/// <see cref="DiscoveryProtocol"/>. Broadcasts a presence beacon so Caster-side discovery UI can
/// find this Terminal, and listens for pairing requests — auto-accepting only devices already
/// paired with <see cref="TrustMode.Trusted"/>, everything else raises <see cref="PairingRequested"/>
/// for a human to decide (no UI exists yet to show that prompt; see this project's README).
///
/// Runs its send/receive loops as background <see cref="Task"/>s, following the same
/// background-thread pattern <c>VideoContentController</c> uses for its own reasons (the UI thread
/// must not block on network I/O).
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    private static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PendingRequestTimeout = TimeSpan.FromMinutes(2);

    private readonly record struct PendingRequest(IPEndPoint RemoteEndPoint, Guid DeviceId, string DeviceName, DateTimeOffset ReceivedAt);

    private readonly DeviceIdentity _identity;
    private readonly PairedDeviceStore _pairedDevices;
    private readonly DeviceConnectionLogger _connectionLog;
    private readonly UdpClient _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _pendingGate = new();
    private readonly Dictionary<string, PendingRequest> _pendingRequests = new();

    private Task? _receiveLoop;
    private Task? _beaconLoop;

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

    public readonly record struct CastStartInfo(Guid DeviceId, int Width, int Height, byte PayloadType);

    public DiscoveryService(DeviceIdentity identity, PairedDeviceStore pairedDevices, DeviceConnectionLogger connectionLog)
    {
        _identity = identity;
        _pairedDevices = pairedDevices;
        _connectionLog = connectionLog;

        _socket = new UdpClient();
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.Port));
        _socket.EnableBroadcast = true;
    }

    public void Start()
    {
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _beaconLoop = Task.Run(() => BeaconLoopAsync(_cts.Token));
    }

    /// <summary>Called by whatever future UI implements PLANNING.md §7's confirmation popup/PIN
    /// prompt. No-op if <paramref name="requestId"/> is unknown or already expired/answered.</summary>
    public void RespondToPairing(string requestId, bool accept, TrustMode trustMode, bool allowCast, bool allowMonitor)
    {
        PendingRequest pending;
        lock (_pendingGate)
        {
            if (!_pendingRequests.Remove(requestId, out pending)) return;
        }

        var response = new DiscoveryProtocol.PairResponseMessage
        {
            RequestId = requestId,
            Accepted = accept,
            Reason = accept ? null : "declined_by_terminal",
        };
        _ = SendAsync(response, pending.RemoteEndPoint);

        if (accept)
        {
            _pairedDevices.Upsert(new PairedDevice
            {
                DeviceId = pending.DeviceId,
                DeviceName = pending.DeviceName,
                TrustMode = trustMode,
                AllowCast = allowCast,
                AllowMonitor = allowMonitor,
                PairedAt = DateTimeOffset.Now,
            });
            _connectionLog.LogPaired(pending.DeviceId.ToString(), pending.DeviceName);
            _connectionLog.LogConnected(pending.DeviceId.ToString());
        }
        else
        {
            _connectionLog.LogDisconnected(pending.DeviceId.ToString(), "pairing_declined");
        }
    }

    private async Task BeaconLoopAsync(CancellationToken token)
    {
        var beacon = new DiscoveryProtocol.BeaconMessage { DeviceId = _identity.DeviceId, DeviceName = _identity.DeviceName };
        var broadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryProtocol.Port);

        while (!token.IsCancellationRequested)
        {
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

            HandleDatagram(result.Buffer, result.RemoteEndPoint);
        }
    }

    private void HandleDatagram(byte[] data, IPEndPoint remoteEndPoint)
    {
        DiscoveryProtocol.Message? message;
        try { message = DiscoveryProtocol.Decode(data); }
        catch (System.Text.Json.JsonException) { return; } // not one of ours — ignore, don't crash the loop.

        switch (message)
        {
            case DiscoveryProtocol.PairRequestMessage req:
                HandlePairRequest(req, remoteEndPoint);
                break;
            case DiscoveryProtocol.CastStartMessage start:
                HandleCastStart(start);
                break;
            case DiscoveryProtocol.CastStopMessage stop:
                HandleCastStop(stop);
                break;
            // BeaconMessage: this is the Terminal side, which only ever sends beacons, never needs
            // to react to one — that's Caster-side discovery UI's job.
        }
    }

    private void HandleCastStart(DiscoveryProtocol.CastStartMessage msg)
    {
        var device = _pairedDevices.Find(msg.DeviceId);
        if (device is not { AllowCast: true })
        {
            // Not paired at all, or paired but casting was never granted (§7 "权限分离...不因配对
            // 而默认互相授权") — silently ignore rather than start receiving/decoding a stream from
            // a sender this Terminal never agreed to accept casts from.
            _connectionLog.LogDisconnected(msg.DeviceId.ToString(), "cast_start_rejected_not_allowed");
            return;
        }
        CastStartRequested?.Invoke(new CastStartInfo(msg.DeviceId, msg.Width, msg.Height, msg.PayloadType));
    }

    private void HandleCastStop(DiscoveryProtocol.CastStopMessage msg) => CastStopRequested?.Invoke(msg.DeviceId);

    private void HandlePairRequest(DiscoveryProtocol.PairRequestMessage req, IPEndPoint remoteEndPoint)
    {
        var existing = _pairedDevices.Find(req.DeviceId);
        if (existing is { TrustMode: TrustMode.Trusted })
        {
            var response = new DiscoveryProtocol.PairResponseMessage { RequestId = req.RequestId, Accepted = true };
            _ = SendAsync(response, remoteEndPoint);
            _connectionLog.LogConnected(req.DeviceId.ToString());
            return;
        }

        lock (_pendingGate)
        {
            _pendingRequests[req.RequestId] = new PendingRequest(remoteEndPoint, req.DeviceId, req.DeviceName, DateTimeOffset.Now);
        }
        PairingRequested?.Invoke(new PairingRequest(req.RequestId, req.DeviceId, req.DeviceName, remoteEndPoint.Address.ToString()));
    }

    private void PruneExpiredPendingRequests()
    {
        var cutoff = DateTimeOffset.Now - PendingRequestTimeout;
        List<PendingRequest> expired;
        lock (_pendingGate)
        {
            var expiredKeys = _pendingRequests.Where(kv => kv.Value.ReceivedAt < cutoff).Select(kv => kv.Key).ToList();
            expired = expiredKeys.Select(key => _pendingRequests[key]).ToList();
            foreach (var key in expiredKeys) _pendingRequests.Remove(key);
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
        _cts.Cancel();

        var runningLoops = new List<Task>();
        if (_receiveLoop != null) runningLoops.Add(_receiveLoop);
        if (_beaconLoop != null) runningLoops.Add(_beaconLoop);
        try { Task.WaitAll(runningLoops.ToArray(), TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }

        _cts.Dispose();
        _socket.Dispose();
    }
}
