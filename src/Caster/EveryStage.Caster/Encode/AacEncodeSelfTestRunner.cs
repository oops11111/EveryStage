using EveryStage.Caster.Capture;

namespace EveryStage.Caster.Encode;

/// <summary>
/// Composes <see cref="AudioCaptureSource"/> (WASAPI loopback, 16-bit PCM) with the new
/// <see cref="AacAudioEncoder"/> — this repo's first look at whether the two actually work together
/// at all, the same role <see cref="EncodeSelfTestRunner"/> plays for screen capture + H.264. Given
/// how much of <see cref="AacAudioEncoder"/> is unverified (see its own doc comment, especially the
/// synchronous-MFT assumption), this self-test's most important signal is simply whether
/// construction/negotiation succeeds and any AAC access units come out at all — this class does not
/// attempt to play them back or validate they're correct AAC, only that the pipeline runs without
/// throwing.
/// </summary>
public sealed class AacEncodeSelfTestRunner : IDisposable
{
    private AudioCaptureSource? _capture;
    private AacAudioEncoder? _encoder;

    public bool IsRunning => _capture != null;
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int AccessUnitsEncoded { get; private set; }
    public long TotalEncodedBytes { get; private set; }
    public long TotalPcmBytesIn { get; private set; }
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

        AudioCaptureSource capture;
        AacAudioEncoder encoder;
        try
        {
            capture = new AudioCaptureSource();
            // AacAudioEncoder assumes 16-bit PCM at whatever sample rate/channel count
            // AudioCaptureSource reports — same assumption AudioCaptureSource itself already
            // guarantees (it converts WASAPI's native float32 mix format down to 16-bit PCM before
            // ever raising PcmCaptured).
            encoder = new AacAudioEncoder(capture.SampleRate, capture.Channels);
        }
        catch (Exception ex)
        {
            // Same "construction failure is a normal, expected outcome" reasoning
            // AudioCaptureSelfTestRunner already applies to AudioCaptureSource alone — here it also
            // covers AacAudioEncoder's own construction (no AAC encoder MFT registered, an
            // unsupported input format, etc.).
            LastError = ex.Message;
            StatsUpdated?.Invoke();
            return;
        }

        SampleRate = capture.SampleRate;
        Channels = capture.Channels;

        encoder.AccessUnitEncoded += OnAccessUnitEncoded;
        encoder.EncodingFailed += OnEncodingFailed;
        capture.PcmCaptured += OnPcmCaptured;
        capture.CaptureFailed += OnCaptureFailed;

        _encoder = encoder;
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
        StatsUpdated?.Invoke();
    }

    private void OnEncodingFailed(Exception ex)
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
    }

    public void Dispose() => Stop();
}
