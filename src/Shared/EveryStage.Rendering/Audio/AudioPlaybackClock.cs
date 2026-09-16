using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EveryStage.Rendering.Audio;

/// <summary>
/// Plays decoded PCM through WASAPI and doubles as the pipeline's master clock
/// (PLANNING.md §4.2: "音频输出WASAPI，音频时钟为主时钟，视频帧同步音频时钟"). Uses NAudio rather
/// than hand-rolled WASAPI interop since audio is explicitly *not* the zero-copy path this library
/// validates/serves — only the reported playback position needs to be trustworthy enough to pace
/// video.
/// </summary>
public sealed class AudioPlaybackClock : IDisposable
{
    private readonly WaveFormat _format;
    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiOut _output;

    public AudioPlaybackClock(int sampleRate, int channels)
    {
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
    }

    public void Start() => _output.Play();

    /// <summary>Pauses WASAPI playback in place — <see cref="PositionTicks"/> stops advancing at
    /// whatever byte count has actually been rendered so far, and <see cref="Resume"/> continues from
    /// there. Does not touch <see cref="_buffer"/>: whatever's already queued stays queued (subject to
    /// its own <c>DiscardOnBufferOverflow</c> policy if a caller keeps calling <see cref="Enqueue"/>
    /// while paused — this class doesn't stop that, since a paused audio player choosing not to keep
    /// decoding is the caller's own responsibility, see <c>Terminal.ContentEngine.AudioContentController</c>'s
    /// own <c>Pause</c> method (a different project, so not linkable from here) for why its decode
    /// loop naturally stops enqueueing once this is paused anyway).</summary>
    public void Pause() => _output.Pause();

    /// <summary>Resumes playback paused by <see cref="Pause"/> from the exact position it left off.</summary>
    public void Resume() => _output.Play();

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
    /// comparable against the MF sample timestamps video frames carry.</summary>
    public long PositionTicks
    {
        get
        {
            // NOTE: verify WasapiOut.GetPosition() semantics against the installed NAudio version
            // — it should report bytes actually rendered (IAudioClock-backed), not bytes queued.
            long bytesPlayed = _output.GetPosition();
            double seconds = bytesPlayed / (double)_format.AverageBytesPerSecond;
            return (long)(seconds * 10_000_000L);
        }
    }

    public void Dispose()
    {
        _output.Stop();
        _output.Dispose();
    }
}
