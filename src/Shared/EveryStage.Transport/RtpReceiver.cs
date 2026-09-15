using System.Net;
using System.Net.Sockets;

namespace EveryStage.Transport;

/// <summary>
/// Receiver-side counterpart to <see cref="RtpSession"/>: listens on a UDP port, decodes each
/// datagram as an RTP packet, and feeds the payload through an <see cref="H264RtpDepacketizer"/>,
/// raising <see cref="NalUnitReceived"/> whenever a complete NAL unit comes out the other end.
///
/// Assumes packets from only one sender (one SSRC) arrive on this port — there is no per-SSRC
/// demultiplexing here. Runs its receive loop as a background <see cref="Task"/>, the same pattern
/// <c>DiscoveryService</c>/<c>TerminalDiscoveryClient</c> use for their own UDP loops.
/// </summary>
public sealed class RtpReceiver : IDisposable
{
    private readonly UdpClient _socket;
    private readonly H264RtpDepacketizer _depacketizer = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;

    /// <summary>Raised from the background receive loop — marshal to another thread/UI as needed.
    /// The <c>bool</c> is the completing RTP packet's Marker bit, i.e. RFC 6184 §5.3 "this was the
    /// last NAL unit of its access unit" — passed through as-is rather than making every consumer
    /// re-derive it, since <see cref="H264RtpPacketizer"/>/<see cref="H264RtpDepacketizer"/> already
    /// encode/decode it symmetrically.</summary>
    public event Action<byte[], bool>? NalUnitReceived;

    public RtpReceiver(int listenPort)
    {
        _socket = new UdpClient(listenPort);
    }

    public void Start() => _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));

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

            if (!RtpPacket.TryDecode(result.Buffer, out var packet)) continue; // not one of ours — ignore.

            var nalUnit = _depacketizer.Process(packet.Payload);
            if (nalUnit != null) NalUnitReceived?.Invoke(nalUnit, packet.Marker);
        }
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
