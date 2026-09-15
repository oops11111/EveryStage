using System.Buffers.Binary;
using NAudio.Wave;

namespace EveryStage.Caster.Capture;

/// <summary>
/// Captures the Caster machine's system audio output via WASAPI loopback (NAudio's
/// <see cref="WasapiLoopbackCapture"/>) — "cast my screen" (PLANNING.md's framing of Caster as
/// "全屏捕获推流") includes whatever audio is currently playing on that machine, the same way
/// consumer screen-casting tools capture system audio alongside the picture, not a separate
/// microphone feed.
///
/// WASAPI loopback's native mix format is virtually always 32-bit IEEE float — this class converts
/// each captured buffer down to 16-bit PCM before handing it off, so the wire format matches what
/// <c>EveryStage.Rendering.Audio.AudioPlaybackClock</c> already expects on the receiving end
/// (<c>new WaveFormat(sampleRate, 16, channels)</c>) without needing to touch that already-working
/// class, and so the RTP payload is half the size of sending float32 for the same audio.
///
/// Lower risk than the video pipeline's DirectX/Media Foundation interop: WASAPI loopback capture
/// via NAudio is the same well-trodden, already-used-in-this-repo API category as
/// <c>AudioPlaybackClock</c>/<c>AudioTakeoverService</c> (playback/session control), not raw COM
/// interop this session has to guess member names for.
/// </summary>
public sealed class AudioCaptureSource : IDisposable
{
    private readonly WasapiLoopbackCapture _capture;

    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>Raised on NAudio's own capture callback thread with one buffer of interleaved,
    /// little-endian 16-bit PCM — marshal/queue before doing anything slow with it.</summary>
    public event Action<byte[]>? PcmCaptured;

    /// <summary>Raised (same calling-thread caveat as <see cref="PcmCaptured"/>) if capture stops
    /// on its own due to an error, or if a captured buffer isn't in the assumed 32-bit float format
    /// — see <see cref="ConvertFloatToPcm16"/>. Same "don't let a background callback fail
    /// silently" reasoning as every other *_Failed event in this repo.</summary>
    public event Action<Exception>? CaptureFailed;

    public AudioCaptureSource()
    {
        _capture = new WasapiLoopbackCapture();
        SampleRate = _capture.WaveFormat.SampleRate;
        Channels = _capture.WaveFormat.Channels;

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public void Start() => _capture.StartRecording();

    public void Stop() => _capture.StopRecording();

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            byte[] pcm16 = ConvertFloatToPcm16(e.Buffer, e.BytesRecorded, _capture.WaveFormat);
            if (pcm16.Length > 0) PcmCaptured?.Invoke(pcm16);
        }
        catch (Exception ex)
        {
            CaptureFailed?.Invoke(ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // NAudio raises this on a normal, requested Stop() too, with e.Exception == null — only an
        // unrequested stop (the audio device disappeared, the default output device changed, etc.)
        // carries a real exception worth surfacing.
        if (e.Exception != null) CaptureFailed?.Invoke(e.Exception);
    }

    /// <summary>NOTE: assumes <paramref name="sourceFormat"/> is 32-bit IEEE float, which is
    /// virtually universal for WASAPI's shared-mode loopback mix format but not guaranteed by the
    /// API contract — throws rather than silently reinterpreting the bytes as something else and
    /// producing noise if a real machine ever reports a different mix format.</summary>
    private static byte[] ConvertFloatToPcm16(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat)
    {
        if (sourceFormat.Encoding != WaveFormatEncoding.IeeeFloat || sourceFormat.BitsPerSample != 32)
        {
            throw new NotSupportedException(
                $"AudioCaptureSource assumes a 32-bit IEEE float WASAPI mix format; got {sourceFormat.Encoding} {sourceFormat.BitsPerSample}-bit.");
        }

        int sampleCount = bytesRecorded / 4;
        var pcm16 = new byte[sampleCount * 2];
        for (int i = 0; i < sampleCount; i++)
        {
            float sample = BitConverter.ToSingle(buffer, i * 4);
            short quantized = (short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(i * 2, 2), quantized);
        }
        return pcm16;
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
    }
}
