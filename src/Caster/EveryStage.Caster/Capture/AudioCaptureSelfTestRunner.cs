using System.Diagnostics;

namespace EveryStage.Caster.Capture;

/// <summary>
/// Drives <see cref="AudioCaptureSource"/> on its own (no encoding, no RTP send — nothing leaves this
/// process) purely to prove WASAPI loopback capture actually works on this machine, the same role
/// <see cref="CaptureSelfTestRunner"/> plays for <see cref="ScreenCaptureSource"/>. This closes a gap
/// this project's README used to call out explicitly: screen capture, H.264 encoding, and RTP
/// transport each already had an isolated self-test button on <c>MainForm</c>'s paired panel, but
/// the audio side — added a full round later than those three — never got one, so a WASAPI capture
/// failure on a real machine had no independent way to confirm/rule out before this.
///
/// Lower-risk to write than <see cref="CaptureSelfTestRunner"/>: <see cref="AudioCaptureSource"/>
/// itself is already the lower-risk half of this repo's capture code (NAudio, not raw DirectX/Media
/// Foundation interop — see that class's own doc comment), so this runner is mostly plumbing that
/// wires its two events to a few counters a polling UI timer can read.
/// </summary>
public sealed class AudioCaptureSelfTestRunner : IDisposable
{
    private AudioCaptureSource? _capture;
    private Stopwatch? _rateWindow;
    private long _bytesSinceLastRateUpdate;

    // Same "deliberately unsynchronized diagnostic stats" reasoning as CaptureSelfTestRunner's own
    // fields: AudioCaptureSource.PcmCaptured fires on NAudio's own single capture callback thread
    // (never concurrently with itself), and a UI polling timer is the only reader — an occasional
    // stale read costs nothing for a self-test display, so this doesn't bother locking every field.
    public bool IsRunning => _capture != null;
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public long TotalBytesCaptured { get; private set; }
    public double BytesPerSecond { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Raised from NAudio's own capture callback thread (roughly once a second, throttled
    /// the same way <see cref="CaptureSelfTestRunner"/> throttles its own Fps updates — see
    /// <see cref="OnPcmCaptured"/>) — marshal to the UI thread before touching UI.</summary>
    public event Action? StatsUpdated;

    public void Start()
    {
        if (IsRunning) return;

        LastError = null;
        TotalBytesCaptured = 0;
        BytesPerSecond = 0;
        _bytesSinceLastRateUpdate = 0;

        AudioCaptureSource capture;
        try
        {
            capture = new AudioCaptureSource();
        }
        catch (Exception ex)
        {
            // Same "construction failure is a normal, expected outcome" reasoning LiveCastSession
            // already applies to its own AudioCaptureSource — no active playback session, no
            // default render device, WASAPI busy, etc.
            LastError = ex.Message;
            StatsUpdated?.Invoke();
            return;
        }

        SampleRate = capture.SampleRate;
        Channels = capture.Channels;
        _rateWindow = Stopwatch.StartNew();

        capture.PcmCaptured += OnPcmCaptured;
        capture.CaptureFailed += OnCaptureFailed;
        _capture = capture;
        capture.Start();
        StatsUpdated?.Invoke();
    }

    private void OnPcmCaptured(byte[] pcm)
    {
        TotalBytesCaptured += pcm.Length;
        _bytesSinceLastRateUpdate += pcm.Length;

        // Throttled to roughly once a second — WASAPI loopback callbacks fire far more often than
        // that (~10ms buffers), and nothing needs per-buffer resolution for a diagnostic display;
        // same throttling reasoning CaptureSelfTestRunner.RunLoop already applies to its own Fps.
        var elapsed = _rateWindow!.Elapsed;
        if (elapsed.TotalSeconds < 1) return;

        BytesPerSecond = _bytesSinceLastRateUpdate / elapsed.TotalSeconds;
        _bytesSinceLastRateUpdate = 0;
        _rateWindow.Restart();
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
        _rateWindow = null;
    }

    public void Dispose() => Stop();
}
