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
        _output.Init(_buffer);
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
