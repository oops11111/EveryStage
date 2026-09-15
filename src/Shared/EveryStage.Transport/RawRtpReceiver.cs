using System.Net;
using System.Net.Sockets;

namespace EveryStage.Transport;

/// <summary>
/// Receiver-side counterpart to <see cref="RtpSession.SendRawPayloadAsync"/>: decodes each incoming
/// datagram as an RTP packet and hands its payload straight through, in arrival order, with no
/// H.264-specific depacketization. Suited to a continuous byte stream like raw PCM audio, where
/// there's no equivalent to <see cref="H264RtpDepacketizer"/>'s "reassemble one fragmented unit"
/// step — every packet's payload already is a complete, independently-usable chunk of the stream.
///
/// Deliberately a separate class from <see cref="RtpReceiver"/> rather than a generalized/shared
/// base — the two loops are nearly identical, but <see cref="RtpReceiver"/>'s whole reason to exist
/// is wiring in <see cref="H264RtpDepacketizer"/>, and forcing that decision to be generic (a
/// pluggable depacketizer, a payload-vs-NAL-unit distinction in the event shape) would only pay off
/// if this project grew a third RTP payload kind. Same reasoning this repo has used elsewhere for
/// small, purpose-specific duplication over a premature shared abstraction (see e.g.
/// <c>EveryStage.Caster.Discovery.TerminalDiscoveryClient</c>'s doc comment on its own
/// pending-request tracking).
/// </summary>
public sealed class RawRtpReceiver : IDisposable
{
    private readonly UdpClient _socket;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;

    /// <summary>Raised from the background receive loop — marshal to another thread/UI as needed.</summary>
    public event Action<byte[]>? PayloadReceived;

    public RawRtpReceiver(int listenPort)
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

            PayloadReceived?.Invoke(packet.Payload.ToArray());
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
