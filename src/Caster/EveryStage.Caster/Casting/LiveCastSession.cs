using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using EveryStage.Caster.Capture;
using EveryStage.Caster.Discovery;
using EveryStage.Caster.Encode;
using EveryStage.Discovery;
using EveryStage.Rendering;
using EveryStage.Transport;
using Vortice.Direct3D11;

namespace EveryStage.Caster.Casting;

/// <summary>
/// The real "开始投屏" pipeline (PLANNING.md §12), and this repository's first attempt at wiring
/// every previously-isolated piece into one live stream: <see cref="ScreenCaptureSource"/> (BGRA
/// texture) -> <see cref="BgraToNv12Converter"/> (NV12 texture) -> <see cref="H264HardwareEncoder"/>
/// (Annex-B H.264 access units) -> <see cref="AnnexBNalSplitter"/> (individual NAL units) ->
/// <see cref="RtpSession"/> (RTP-over-UDP), sent to the paired Terminal's
/// <see cref="DiscoveryProtocol.VideoRtpPort"/>. <see cref="EncodeSelfTestRunner"/> already exercises
/// this same chain up through the encoder in isolation; this class carries it the rest of the way
/// onto the network and additionally signals the Terminal out-of-band
/// (<see cref="DiscoveryProtocol.CastStartMessage"/> / <see cref="DiscoveryProtocol.CastStopMessage"/>)
/// so it knows a stream is starting/stopping and what resolution to expect, instead of the Terminal
/// having to infer that purely from RTP packets arriving on a fixed port.
///
/// Nothing in this class knows whether the Terminal actually received or displayed anything — there
/// is no acknowledgment channel, so "投屏中" here only ever means "still capturing/encoding/sending
/// without a local error", never "confirmed visible on the other end". See this project's README
/// "已知风险" for why that's an accepted, documented limitation rather than an oversight.
///
/// One instance is single-use: <see cref="Start"/> then, once, <see cref="Stop"/> or
/// <see cref="Dispose"/> — the internal send queue is completed on stop and cannot be reopened.
/// This matches how the UI actually uses it (a fresh instance is constructed per successful
/// pairing), so re-starting the same instance was never a case worth supporting.
/// </summary>
public sealed class LiveCastSession : IDisposable
{
    // RFC 3551 dynamic payload type range — arbitrary, fixed by convention between this repo's two
    // sides the same way every other unnegotiated parameter here is (no SDP or equivalent exists).
    // Also sent explicitly in CastStartMessage.PayloadType so the Terminal doesn't additionally need
    // its own hardcoded copy of this constant, even though today both sides happen to agree anyway.
    private const byte PayloadType = 96;

    private readonly TerminalDiscoveryClient _discoveryClient;
    private readonly DeviceIdentity _identity;
    private readonly DiscoveredTerminal _terminal;
    private readonly Stopwatch _clock = new();

    // Unbounded, single-reader: H264HardwareEncoder's own event-loop thread (via
    // OnAccessUnitEncoded) is the only writer, and a dedicated RunSendLoop task is the only reader.
    // Queuing (rather than firing off SendNalUnitAsync directly from OnAccessUnitEncoded) exists
    // for one reason: RTP requires strictly increasing sequence numbers in send order, and
    // RtpReceiver never reorders (see EveryStage.Transport's README) — if two NAL sends ran
    // concurrently as independent fire-and-forget tasks, their SendAsync completions (and therefore
    // which one's bytes actually reach the socket first) could interleave in a different order than
    // they were produced in, silently corrupting playback on the receiving end. A single sequential
    // reader task makes "sent in encode order" true by construction instead of by hoping two
    // concurrent async sends happen to complete in the order they were started.
    private readonly Channel<(ReadOnlyMemory<byte> Nal, uint Timestamp, bool IsLastNalOfAccessUnit)> _sendQueue =
        Channel.CreateUnbounded<(ReadOnlyMemory<byte>, uint, bool)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private D3D11Device? _gpu;
    private ScreenCaptureSource? _capture;
    private BgraToNv12Converter? _converter;
    private H264HardwareEncoder? _encoder;
    private RtpSession? _rtpSession;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Task? _sendLoopTask;

    public bool IsRunning => _loopTask != null;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long FramesCaptured { get; private set; }
    public long AccessUnitsSent { get; private set; }
    public long BytesSent { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Raised from the background capture/encode loop, or from the encoder's own event
    /// loop — marshal to the UI thread before touching UI (mirrors every other self-test runner in
    /// this project).</summary>
    public event Action? StatsUpdated;

    public LiveCastSession(TerminalDiscoveryClient discoveryClient, DeviceIdentity identity, DiscoveredTerminal terminal)
    {
        _discoveryClient = discoveryClient;
        _identity = identity;
        _terminal = terminal;
    }

    public void Start()
    {
        if (IsRunning) return;

        LastError = null;
        FramesCaptured = 0;
        AccessUnitsSent = 0;
        BytesSent = 0;

        try
        {
            _gpu = new D3D11Device();
            _capture = new ScreenCaptureSource(_gpu);
            Width = _capture.Width;
            Height = _capture.Height;
            _converter = new BgraToNv12Converter(_gpu);
            _encoder = new H264HardwareEncoder(_gpu, Width, Height, frameRateNumerator: 30, bitrateBps: 4_000_000);
            _rtpSession = new RtpSession(new IPEndPoint(_terminal.Address, DiscoveryProtocol.VideoRtpPort), PayloadType);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StopInternal();
            StatsUpdated?.Invoke();
            return;
        }

        _encoder.AccessUnitEncoded += OnAccessUnitEncoded;
        _encoder.EncodingFailed += OnEncodingFailed;

        // Best-effort, fire-and-forget, same as every other discovery-protocol send in this repo —
        // there is no acknowledgment/retry to wait on, and a lost cast_start datagram on an
        // otherwise-healthy LAN is treated as an acceptable risk rather than a reason to block
        // pipeline startup (see this project's README).
        _ = _discoveryClient.SendCastStartAsync(_terminal, _identity, Width, Height, PayloadType);

        _clock.Restart();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoop(_gpu, _capture, _converter, _encoder, _cts.Token));
        _sendLoopTask = Task.Run(() => RunSendLoop(_rtpSession, _cts.Token));
    }

    private void RunLoop(D3D11Device gpu, ScreenCaptureSource capture, BgraToNv12Converter converter, H264HardwareEncoder encoder, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                CapturedFrame? frame;
                try
                {
                    frame = capture.AcquireNextFrame(500);
                }
                catch (ScreenCaptureLostException ex)
                {
                    LastError = ex.Message;
                    break;
                }

                if (frame == null) continue; // normal timeout — screen hasn't changed.

                using (frame)
                {
                    if (!frame.HasNewImage) continue;

                    var nv12 = converter.Convert(frame.Texture, frame.Width, frame.Height);
                    // Same reasoning as EncodeSelfTestRunner: the converter reuses one output
                    // texture across calls, so it must be copied before handing off to the
                    // encoder's async input queue.
                    encoder.SubmitFrame(CopyTexture(gpu, nv12));
                    FramesCaptured++;
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            StatsUpdated?.Invoke();
        }
    }

    private static ID3D11Texture2D CopyTexture(D3D11Device gpu, ID3D11Texture2D source)
    {
        var copy = gpu.Device.CreateTexture2D(source.Description);
        gpu.ImmediateContext.CopyResource(copy, source);
        return copy;
    }

    private void OnAccessUnitEncoded(byte[] accessUnit)
    {
        uint timestamp = RtpVideoClock.FromElapsed(_clock.Elapsed);
        var nalUnits = AnnexBNalSplitter.Split(accessUnit).ToList();
        for (int i = 0; i < nalUnits.Count; i++)
        {
            bool isLastNalOfAccessUnit = i == nalUnits.Count - 1;
            // Non-blocking enqueue — this callback runs on H264HardwareEncoder's own background
            // event-loop thread (see its doc comment), which must not block on network I/O.
            // RunSendLoop is the single reader that actually calls SendNalUnitAsync, in enqueue
            // order — see the field doc comment on _sendQueue for why order matters here.
            _sendQueue.Writer.TryWrite((nalUnits[i], timestamp, isLastNalOfAccessUnit));
        }

        AccessUnitsSent++;
        BytesSent += accessUnit.Length;
        StatsUpdated?.Invoke();
    }

    private async Task RunSendLoop(RtpSession rtpSession, CancellationToken token)
    {
        try
        {
            await foreach (var (nal, timestamp, isLastNalOfAccessUnit) in _sendQueue.Reader.ReadAllAsync(token))
            {
                try
                {
                    await rtpSession.SendNalUnitAsync(nal, timestamp, isLastNalOfAccessUnit);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    StatsUpdated?.Invoke();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path — Stop()/Dispose() cancels the token that ReadAllAsync is
            // observing.
        }
    }

    private void OnEncodingFailed(Exception ex)
    {
        LastError = ex.Message;
        StatsUpdated?.Invoke();
    }

    public void Stop()
    {
        bool wasRunning = IsRunning;
        StopInternal();
        // Only worth telling the Terminal if a stream had actually started — an aborted Start()
        // (e.g. no hardware encoder found) never sent cast_start in the first place.
        if (wasRunning) _ = _discoveryClient.SendCastStopAsync(_terminal, _identity);
    }

    private void StopInternal()
    {
        _cts?.Cancel();
        _sendQueue.Writer.TryComplete();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        try { _sendLoopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _loopTask = null;
        _sendLoopTask = null;
        _cts?.Dispose();
        _cts = null;

        if (_encoder != null)
        {
            _encoder.AccessUnitEncoded -= OnAccessUnitEncoded;
            _encoder.EncodingFailed -= OnEncodingFailed;
        }
        _encoder?.Dispose();
        _encoder = null;
        _converter?.Dispose();
        _converter = null;
        _capture?.Dispose();
        _capture = null;
        _gpu?.Dispose();
        _gpu = null;
        _rtpSession?.Dispose();
        _rtpSession = null;
    }

    public void Dispose() => StopInternal();
}
