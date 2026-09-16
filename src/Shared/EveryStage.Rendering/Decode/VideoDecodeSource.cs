using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using static EveryStage.Rendering.Decode.WellKnownGuids;

namespace EveryStage.Rendering.Decode;

/// <summary>The decoder's own texture-array slot for one video frame. No copy has happened yet —
/// the caller (SwapChainPresenter) consumes ArraySlice directly as a video-processor input.</summary>
public readonly record struct DecodedVideoFrame(ID3D11Texture2D Texture, int ArraySlice, int Width, int Height, long TimestampTicks);

/// <summary>PCM comes off the reader as regular CPU memory since audio isn't part of the
/// zero-copy path this decoder is meant to validate/serve (see AudioPlaybackClock).</summary>
public readonly record struct DecodedAudioChunk(byte[] Pcm, long TimestampTicks);

/// <summary>
/// Wraps a single <see cref="IMFSourceReader"/> configured for D3D11-aware hardware decode
/// (PLANNING.md §4.2: "Media Foundation硬件解码(DXVA) → D3D11纹理直出"). Video and audio are read
/// as two independent stream requests against MF's FIRST_VIDEO_STREAM / FIRST_AUDIO_STREAM
/// sentinels — IMFSourceReader::ReadSample accepts those directly, so there's no need to resolve
/// which physical (container-defined) stream index actually carries video vs. audio.
/// </summary>
public sealed class VideoDecodeSource : IDisposable
{
    private readonly IMFSourceReader _reader;
    public int VideoWidth { get; }
    public int VideoHeight { get; }
    public int AudioChannels { get; }
    public int AudioSampleRate { get; }

    public VideoDecodeSource(string filePathOrUrl, D3D11Device gpu)
    {
        MediaFactory.MFStartup().CheckError();

        // Bug fixed here (same shape as H264HardwareDecoder/H264HardwareEncoder/AudioDecodeSource's
        // own constructor fixes, see any of their doc comments): MFStartup() above already
        // succeeded by the time execution reaches this line — but if anything below throws, this
        // constructor never finishes, so no VideoDecodeSource instance ever exists for its owner to
        // later Dispose() and hit the MFShutdown() call below. Without this try/catch, a bad/
        // corrupt/unsupported video file (a real, not hypothetical, condition — this class exists
        // specifically to open arbitrary user-supplied files) would leak one MFStartup() reference
        // count every time.
        try
        {
            // Bug also fixed here: attributes/videoType/audioType below were never wrapped in
            // `using` (unlike AudioDecodeSource's equivalent audioType, and this class's own
            // actualVideoType/actualAudioType just below, which already do this correctly) — each is
            // its own IMFAttributes/IMFMediaType COM object that MFCreateSourceReaderFromURL/
            // SetCurrentMediaType only ever reads from, never takes ownership of, so all three used
            // to leak one native handle per VideoDecodeSource construction (i.e. every time local
            // video playback starts).
            MediaFactory.MFCreateAttributes(out var attributes, 2).CheckError();
            using (attributes)
            {
                attributes.Set(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1u);
                attributes.Set(MF_SOURCE_READER_D3D_MANAGER, gpu.DeviceManager);
                MediaFactory.MFCreateSourceReaderFromURL(filePathOrUrl, attributes, out _reader).CheckError();
            }

            // Force NV12 on the video stream: keeps the decoder's native DXVA surface format flowing
            // straight through instead of an internal color-conversion transform breaking the
            // zero-copy chain ahead of us.
            MediaFactory.MFCreateMediaType(out var videoType).CheckError();
            using (videoType)
            {
                videoType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                videoType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
                _reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, videoType);
            }

            MediaFactory.MFCreateMediaType(out var audioType).CheckError();
            using (audioType)
            {
                audioType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                audioType.Set(MF_MT_SUBTYPE, MFAudioFormat_PCM);
                _reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, audioType);
            }

            using var actualVideoType = _reader.GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM);
            (VideoWidth, VideoHeight) = ReadFrameSize(actualVideoType);

            using var actualAudioType = _reader.GetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM);
            AudioChannels = (int)actualAudioType.Get<uint>(MediaTypeAttributeKeys.AudioNumChannels());
            AudioSampleRate = (int)actualAudioType.Get<uint>(MediaTypeAttributeKeys.AudioSamplesPerSecond());

            _reader.SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);
            _reader.SetStreamSelection(MF_SOURCE_READER_FIRST_AUDIO_STREAM, true);
        }
        catch
        {
            _reader?.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    /// <summary>Returns null once the video stream reports end-of-stream.</summary>
    public DecodedVideoFrame? ReadNextVideoFrame()
    {
        _reader.ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, SourceReaderControlFlags.None,
            out _, out var streamFlags, out var timestamp, out var sample);

        if ((streamFlags & SourceReaderFlags.Endofstream) != 0 || sample == null)
            return null;

        using (sample)
        using (var buffer = sample.ConvertToContiguousBuffer())
        using (var dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>())
        {
            var texture = dxgiBuffer.GetResource<ID3D11Texture2D>();
            int arraySlice = (int)dxgiBuffer.GetSubresourceIndex();
            return new DecodedVideoFrame(texture, arraySlice, VideoWidth, VideoHeight, timestamp);
        }
    }

    /// <summary>Returns null once the audio stream reports end-of-stream.</summary>
    public DecodedAudioChunk? ReadNextAudioChunk()
    {
        _reader.ReadSample(MF_SOURCE_READER_FIRST_AUDIO_STREAM, SourceReaderControlFlags.None,
            out _, out var streamFlags, out var timestamp, out var sample);

        if ((streamFlags & SourceReaderFlags.Endofstream) != 0 || sample == null)
            return null;

        using (sample)
        using (var buffer = sample.ConvertToContiguousBuffer())
        {
            // NOTE: exact Lock() signature/return type (Span<byte> vs. raw pointer + length outs)
            // needs checking against the installed Vortice.MediaFoundation version — see the
            // Phase 0 demo's README "待验证事项". Native IMFMediaBuffer::Lock is
            // (out BYTE*, out maxLength, out currentLength).
            var span = buffer.Lock(out _, out var currentLength);
            var pcm = new byte[currentLength];
            span.Slice(0, currentLength).CopyTo(pcm);
            buffer.Unlock();
            return new DecodedAudioChunk(pcm, timestamp);
        }
    }

    private static (int width, int height) ReadFrameSize(IMFMediaType type)
    {
        ulong packed = type.Get<ulong>(MediaTypeAttributeKeys.FrameSize());
        int width = (int)(packed >> 32);
        int height = (int)(packed & 0xFFFFFFFF);
        return (width, height);
    }

    public void Dispose()
    {
        _reader.Dispose();
        MediaFactory.MFShutdown();
    }
}
