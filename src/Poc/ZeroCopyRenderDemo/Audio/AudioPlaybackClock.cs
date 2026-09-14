using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EveryStage.Poc.ZeroCopyRenderDemo.Audio;

/// <summary>
/// Plays decoded PCM through WASAPI and doubles as the pipeline's master clock
/// (PLANNING.md §4.2: "音频输出WASAPI，音频时钟为主时钟，视频帧同步音频时钟"). Uses NAudio rather
/// than hand-rolled WASAPI interop since audio is explicitly *not* the zero-copy path under test
/// here — only the reported playback position needs to be trustworthy enough to pace video.
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
        // Shared mode + a modest latency: this is a validation demo, not the final low-latency
        // audio path, so we don't fight for exclusive-mode access here.
        _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 50);
        _output.Init(_buffer);
    }

    public void Start() => _output.Play();

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
