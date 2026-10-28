using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EveryStage.Rendering.Audio;

/// <summary>
/// Plays decoded PCM through WASAPI and doubles as the pipeline's master clock
/// (PLANNING.md §4.2: "音频输出WASAPI，音频时钟为主时钟，视频帧同步音频时钟"). Uses NAudio rather
/// than hand-rolled WASAPI interop since audio is explicitly *not* the zero-copy path this library
/// validates/serves — only the reported playback position needs to be trustworthy enough to pace
/// video.
///
/// Output loss (audit A-24): when the render endpoint goes away mid-play (an HDMI display or projector
/// switched off, a USB sound card unplugged), NAudio's own playback thread stops the output and reports
/// it only through <c>WasapiOut.PlaybackStopped</c>; <c>GetPosition()</c> then reads 0. Callers pacing
/// video against <see cref="PositionTicks"/> used to wait on that 0 forever (a frozen picture, still
/// "playing", nothing logged). This class now treats any stop it did not request as a fault:
/// <see cref="IsFaulted"/>/<see cref="Faulted"/> report it, and <see cref="PositionTicks"/> holds the
/// last position read before the fault instead of dropping to 0, so a caller can continue on another
/// clock from where the audio left off.
/// </summary>
public sealed class AudioPlaybackClock : IDisposable
{
    private readonly WaveFormat _format;
    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiOut _output;
    private readonly object _gate = new();
    private volatile bool _stopRequested;
    private long _lastPositionTicks;

    // Live instances, for SimulateOutputLossForTests only.
    private static readonly List<WeakReference<AudioPlaybackClock>> Live = new();

    /// <summary>Test hook: makes construction fail the way it does on a machine without an audio
    /// output device (degradation paths). Never set in production.</summary>
    public static bool SimulateNoOutputDevice { get; set; }

    /// <summary>Test hook: stops every live output behind its owner's back — the same state NAudio's
    /// playback thread leaves the output in when the endpoint is invalidated.</summary>
    public static void SimulateOutputLossForTests()
    {
        List<AudioPlaybackClock> clocks;
        lock (Live) clocks = Live.Select(w => w.TryGetTarget(out var c) ? c : null).OfType<AudioPlaybackClock>().ToList();
        foreach (var clock in clocks)
        {
            try { clock._output.Stop(); } catch (Exception) { }
        }
    }

    public AudioPlaybackClock(int sampleRate, int channels)
    {
        if (SimulateNoOutputDevice)
            throw new COMException("找不到元素。（模拟：没有音频输出设备）", unchecked((int)0x80070490));
        _format = new WaveFormat(sampleRate, 16, channels);
        _buffer = new BufferedWaveProvider(_format)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true,
        };
        // Shared mode + a modest latency: this validates/serves the decode->render pipeline, not
        // a separately-optimized low-latency audio path, so we don't fight for exclusive-mode
        // access here.
        _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 50);
        // Bug fixed here: same "step one succeeds and gets kept, step two throws, nothing disposes
        // step one" shape as EveryStage.Rendering's D3D11Device/SwapChainPresenter constructor
        // fixes (see that library's README) — Init() is a real WASAPI call that can genuinely fail
        // (format negotiation, the default render device changing/disappearing between
        // construction and Init), and if it does, this constructor never finishes, so no
        // AudioPlaybackClock instance ever exists for a caller (VideoContentController.Play/
        // AudioContentController.Play, both of which construct one fresh per file played) to later
        // Dispose() and release the WasapiOut instance already created. Calls only Dispose() here,
        // not the Stop() this class's own Dispose() also calls — Stop() on a WasapiOut whose Init()
        // never succeeded is untested territory this fix has no reason to risk.
        try
        {
            _output.Init(_buffer);
        }
        catch
        {
            _output.Dispose();
            throw;
        }
        _output.PlaybackStopped += OnPlaybackStopped;
        lock (Live)
        {
            Live.RemoveAll(w => !w.TryGetTarget(out _));
            Live.Add(new WeakReference<AudioPlaybackClock>(this));
        }
    }

    /// <summary>True once the output stopped without being asked to (endpoint removed, audio service
    /// stopped). Permanent for this instance: playback cannot resume on it.</summary>
    public bool IsFaulted { get => _faulted; private set => _faulted = value; }
    private volatile bool _faulted;

    /// <summary>Why the output stopped, when <see cref="IsFaulted"/>.</summary>
    public string? FaultReason { get; private set; }

    /// <summary>Raised once when the output stops on its own — on the UI thread (NAudio's posted
    /// <c>PlaybackStopped</c>) or on whichever thread first reads <see cref="PositionTicks"/> after it.</summary>
    public event Action<string>? Faulted;

    // NAudio posts PlaybackStopped to the SynchronizationContext captured at construction (the UI thread
    // for every caller here), so the event can arrive late; PositionTicks also detects the stopped
    // state directly so a pacing loop on another thread never waits on the event.
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e) =>
        MarkFaulted(e.Exception?.Message.Trim() is { Length: > 0 } message ? message : "音频输出设备已停止");

    private void MarkFaulted(string reason)
    {
        lock (_gate)
        {
            if (_stopRequested || IsFaulted) return;
            FaultReason = reason;
            IsFaulted = true;
        }
        try { Faulted?.Invoke(reason); } catch (Exception) { }
    }

    public void Start()
    {
        if (IsFaulted) return;
        _started = true;
        _output.Play();
    }

    private volatile bool _started;

    /// <summary>Pauses WASAPI playback in place — <see cref="PositionTicks"/> stops advancing at
    /// whatever byte count has actually been rendered so far, and <see cref="Resume"/> continues from
    /// there. Does not touch <see cref="_buffer"/>: whatever's already queued stays queued (subject to
    /// its own <c>DiscardOnBufferOverflow</c> policy if a caller keeps calling <see cref="Enqueue"/>
    /// while paused — this class doesn't stop that, since a paused audio player choosing not to keep
    /// decoding is the caller's own responsibility, see <c>Terminal.ContentEngine.AudioContentController</c>'s
    /// own <c>Pause</c> method (a different project, so not linkable from here) for why its decode
    /// loop naturally stops enqueueing once this is paused anyway). No-op once faulted.</summary>
    public void Pause()
    {
        if (!IsFaulted) _output.Pause();
    }

    /// <summary>Resumes playback paused by <see cref="Pause"/> from the exact position it left off.
    /// No-op once faulted (restarting a dead endpoint would only fault again).</summary>
    public void Resume()
    {
        if (!IsFaulted) { _started = true; _output.Play(); }
    }

    /// <summary>Output volume, 0.0 (silent) to 1.0 (full) — wraps <c>WasapiOut.Volume</c> directly,
    /// which NAudio documents as valid at any point in this player's lifetime (before, during, or
    /// after <see cref="Start"/>), not just while actively playing. No clamping here: this class
    /// trusts its caller (<c>Terminal.ContentEngine.AudioContentController.Volume</c>) to have
    /// already clamped to [0, 1] — a value outside that range would reach <c>WasapiOut.Volume</c>
    /// directly, whatever that does (this class doesn't know without a real run, see this project's
    /// README).</summary>
    public float Volume
    {
        get => _output.Volume;
        set => _output.Volume = value;
    }

    public void Enqueue(byte[] pcm) => _buffer.AddSamples(pcm, 0, pcm.Length);

    /// <summary>Current hardware playback position, converted to 100ns ticks so it's directly
    /// comparable against the MF sample timestamps video frames carry. Never moves backwards for one
    /// instance (a seek builds a new clock): after a fault it keeps returning the last position read
    /// before it, where WASAPI itself would read 0.</summary>
    public long PositionTicks
    {
        get
        {
            long last = Volatile.Read(ref _lastPositionTicks);
            if (IsFaulted) return last;
            if (_started && !_stopRequested && _output.PlaybackState == PlaybackState.Stopped)
            {
                MarkFaulted("音频输出设备已停止");
                return last;
            }
            long bytesPlayed;
            try { bytesPlayed = _output.GetPosition(); }
            catch (Exception ex)
            {
                MarkFaulted(ex.Message.Trim());
                return last;
            }
            long ticks = (long)(bytesPlayed / (double)_format.AverageBytesPerSecond * 10_000_000L);
            if (ticks <= last) return last;
            Volatile.Write(ref _lastPositionTicks, ticks);
            return ticks;
        }
    }

    /// <summary>Audio already queued but not yet rendered, expressed on the same 100ns timeline as
    /// <see cref="PositionTicks"/>. Used to map an incoming RTP packet to when it will actually play.</summary>
    public long BufferedDurationTicks =>
        (long)(_buffer.BufferedBytes / (double)_format.AverageBytesPerSecond * TimeSpan.TicksPerSecond);

    public void Dispose()
    {
        lock (_gate) _stopRequested = true;
        _output.PlaybackStopped -= OnPlaybackStopped;
        try { _output.Stop(); } catch (Exception) { }
        _output.Dispose();
        lock (Live) Live.RemoveAll(w => !w.TryGetTarget(out var c) || ReferenceEquals(c, this));
    }
}
