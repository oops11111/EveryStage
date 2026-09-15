using EveryStage.Caster.Capture;
using EveryStage.Rendering.Decode;

namespace EveryStage.Caster.Encode;

/// <summary>
/// Composes <see cref="AudioCaptureSource"/> (WASAPI loopback, 16-bit PCM) with
/// <see cref="AacAudioEncoder"/> and — now that both directions exist — <see cref="AacAudioDecoder"/>
/// too, feeding every encoded access unit straight back into a decoder in the same process. This is
/// this repo's first look at whether capture, encode, AND decode all actually work together, the
/// same role <see cref="EncodeSelfTestRunner"/> plays for screen capture + H.264 (which has no
/// equivalent decode-side self-test of its own, since Terminal-side H.264 decode reuses the already-
/// separately-validated <c>VideoDecodeSource</c>/<c>SwapChainPresenter</c> pipeline from Phase 0,
/// rather than a from-scratch decoder this repo wrote itself the way <see cref="AacAudioDecoder"/> is).
/// Given how much of both <see cref="AacAudioEncoder"/> and <see cref="AacAudioDecoder"/> is
/// unverified (see their own doc comments, especially the shared synchronous-MFT assumption), this
/// self-test's most important signal is simply whether construction/negotiation succeeds on both
/// ends and PCM comes back out the other side at all — this class does not attempt to actually play
/// the decoded PCM back or validate it perceptually matches the original audio, only that the full
/// round trip runs without throwing.
/// </summary>
public sealed class AacEncodeSelfTestRunner : IDisposable
{
    private AudioCaptureSource? _capture;
    private AacAudioEncoder? _encoder;
    private AacAudioDecoder? _decoder;

    public bool IsRunning => _capture != null;
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int AccessUnitsEncoded { get; private set; }
    public long TotalEncodedBytes { get; private set; }
    public long TotalPcmBytesIn { get; private set; }

    /// <summary>PCM bytes <see cref="AacAudioDecoder"/> handed back after decoding
    /// <see cref="AacAudioEncoder"/>'s output — a healthy round trip keeps this roughly tracking
    /// <see cref="TotalPcmBytesIn"/> (not exactly: AAC's fixed 1024-sample frame size means the
    /// last, partial frame at any given moment hasn't been encoded/decoded yet, and encoder/decoder
    /// startup latency means the two totals are never expected to match exactly instant-to-instant).</summary>
    public long TotalDecodedPcmBytes { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Raised from NAudio's own capture callback thread (see
    /// <see cref="AudioCaptureSource.PcmCaptured"/>) — <see cref="AacAudioEncoder.SubmitPcm"/> runs
    /// synchronously on that same thread with no marshaling of its own, so this event carries the
    /// same calling-thread caveat as every other self-test runner's <c>StatsUpdated</c>.</summary>
    public event Action? StatsUpdated;

    public void Start()
    {
        if (IsRunning) return;

        LastError = null;
        AccessUnitsEncoded = 0;
        TotalEncodedBytes = 0;
        TotalPcmBytesIn = 0;
        TotalDecodedPcmBytes = 0;

        AudioCaptureSource capture;
        AacAudioEncoder encoder;
        AacAudioDecoder decoder;
        try
        {
            capture = new AudioCaptureSource();
            // AacAudioEncoder/AacAudioDecoder both assume 16-bit PCM at whatever sample rate/channel
            // count AudioCaptureSource reports — same assumption AudioCaptureSource itself already
            // guarantees (it converts WASAPI's native float32 mix format down to 16-bit PCM before
            // ever raising PcmCaptured).
            encoder = new AacAudioEncoder(capture.SampleRate, capture.Channels);
            decoder = new AacAudioDecoder(capture.SampleRate, capture.Channels);
        }
        catch (Exception ex)
        {
            // Same "construction failure is a normal, expected outcome" reasoning
            // AudioCaptureSelfTestRunner already applies to AudioCaptureSource alone — here it also
            // covers AacAudioEncoder/AacAudioDecoder's own construction (no AAC encoder/decoder MFT
            // registered, an unsupported negotiated format, etc.).
            LastError = ex.Message;
            StatsUpdated?.Invoke();
            return;
        }

        SampleRate = capture.SampleRate;
        Channels = capture.Channels;

        encoder.AccessUnitEncoded += OnAccessUnitEncoded;
        encoder.EncodingFailed += OnEncodingFailed;
        decoder.PcmDecoded += OnPcmDecoded;
        decoder.DecodingFailed += OnDecodingFailed;
        capture.PcmCaptured += OnPcmCaptured;
        capture.CaptureFailed += OnCaptureFailed;

        _encoder = encoder;
        _decoder = decoder;
        _capture = capture;
        capture.Start();
        StatsUpdated?.Invoke();
    }

    private void OnPcmCaptured(byte[] pcm)
    {
        TotalPcmBytesIn += pcm.Length;
        // Runs inline on NAudio's capture callback thread — see AacAudioEncoder's own doc comment
        // on why that's fine (no background thread, no async event loop, unlike H264HardwareEncoder).
        _encoder?.SubmitPcm(pcm);
    }

    private void OnAccessUnitEncoded(byte[] accessUnit)
    {
        AccessUnitsEncoded++;
        TotalEncodedBytes += accessUnit.Length;
        // Feeds straight back into the decoder inline, on the same thread — AacAudioEncoder raises
        // this synchronously from within SubmitPcm (see that method's own doc comment: no background
        // thread), and AacAudioDecoder.SubmitAccessUnit is equally synchronous, so this chains
        // capture -> encode -> decode all on NAudio's original capture callback thread with no
        // marshaling anywhere in between.
        _decoder?.SubmitAccessUnit(accessUnit);
        StatsUpdated?.Invoke();
    }

    private void OnEncodingFailed(Exception ex)
    {
        LastError = ex.Message;
        StatsUpdated?.Invoke();
    }

    private void OnPcmDecoded(byte[] pcm)
    {
        TotalDecodedPcmBytes += pcm.Length;
        StatsUpdated?.Invoke();
    }

    private void OnDecodingFailed(Exception ex)
    {
        LastError = ex.Message;
        StatsUpdated?.Invoke();
    }

    private void OnCaptureFailed(Exception ex)
    {
        LastError = ex.Message;
        StatsUpdated?.Invoke();
    }

    public void Stop()
    {
        if (_capture == null) return;

        _capture.Stop();
        _capture.PcmCaptured -= OnPcmCaptured;
        _capture.CaptureFailed -= OnCaptureFailed;
        _capture.Dispose();
        _capture = null;

        if (_encoder != null)
        {
            _encoder.AccessUnitEncoded -= OnAccessUnitEncoded;
            _encoder.EncodingFailed -= OnEncodingFailed;
            _encoder.Dispose();
            _encoder = null;
        }

        if (_decoder != null)
        {
            _decoder.PcmDecoded -= OnPcmDecoded;
            _decoder.DecodingFailed -= OnDecodingFailed;
            _decoder.Dispose();
            _decoder = null;
        }
    }

    public void Dispose() => Stop();
}
