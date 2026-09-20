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
            var attributes = MediaFactory.MFCreateAttributes(2);
            using (attributes)
            {
                attributes.Set(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1u);
                attributes.Set(MF_SOURCE_READER_D3D_MANAGER, gpu.DeviceManager);
                _reader = MediaFactory.MFCreateSourceReaderFromURL(filePathOrUrl, attributes);
            }

            // Force NV12 on the video stream: keeps the decoder's native DXVA surface format flowing
            // straight through instead of an internal color-conversion transform breaking the
            // zero-copy chain ahead of us.
            var videoType = MediaFactory.MFCreateMediaType();
            using (videoType)
            {
                videoType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                videoType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
                _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, videoType);
            }

            var audioType = MediaFactory.MFCreateMediaType();
            using (audioType)
            {
                audioType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                audioType.Set(MF_MT_SUBTYPE, MFAudioFormat_PCM);
                _reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, audioType);
            }

            using var actualVideoType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            (VideoWidth, VideoHeight) = ReadFrameSize(actualVideoType);

            using var actualAudioType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
            AudioChannels = (int)actualAudioType.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
            AudioSampleRate = (int)actualAudioType.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);

            _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            _reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
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
        var sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
            out _, out var streamFlags, out var timestamp);

        if ((streamFlags & SourceReaderFlag.EndOfStream) != 0 || sample == null)
            return null;

        using (sample)
        using (var buffer = sample.ConvertToContiguousBuffer())
        using (var dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>())
        {
            var texture = new ID3D11Texture2D(dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID));
            int arraySlice = (int)dxgiBuffer.SubresourceIndex;
            return new DecodedVideoFrame(texture, arraySlice, VideoWidth, VideoHeight, timestamp);
        }
    }

    /// <summary>Same "unverified, goes through <c>dynamic</c> so a wrong guess fails at runtime
    /// instead of at build time" reasoning as <see cref="AudioDecodeSource.TryGetDuration"/>'s own
    /// (much longer) doc comment — deliberately independent code, not shared, matching this file's
    /// existing "each decode source keeps its own copy" convention (see e.g.
    /// <see cref="ReadFrameSize"/> vs. <c>AudioDecodeSource</c>'s analogous channel/rate reads).</summary>
    public TimeSpan? TryGetDuration()
    {
        try
        {
            dynamic reader = _reader;
            dynamic variant = reader.GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, MF_PD_DURATION);
            long ticks = ExtractDurationTicks(variant);
            return ticks > 0 ? TimeSpan.FromTicks(ticks) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>See <see cref="AudioDecodeSource"/>'s identical private helper's own doc comment —
    /// deliberately duplicated, not shared, same reasoning as <see cref="TryGetDuration"/> above.</summary>
    private static long ExtractDurationTicks(dynamic variant)
    {
        try { return checked((long)variant.Value); } catch { }
        try { return checked((long)variant.UInt64); } catch { }
        try { return checked((long)(ulong)variant); } catch { }
        throw new InvalidOperationException("Unable to extract a UInt64 value from the Variant returned by GetPresentationAttribute.");
    }

    /// <summary>Returns null once the audio stream reports end-of-stream.</summary>
    public DecodedAudioChunk? ReadNextAudioChunk()
    {
        var sample = _reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None,
            out _, out var streamFlags, out var timestamp);

        if ((streamFlags & SourceReaderFlag.EndOfStream) != 0 || sample == null)
            return null;

        using (sample)
        using (var buffer = sample.ConvertToContiguousBuffer())
        {
            // NOTE: exact Lock() signature/return type (Span<byte> vs. raw pointer + length outs)
            // needs checking against the installed Vortice.MediaFoundation version — see the
            // Phase 0 demo's README "待验证事项". Native IMFMediaBuffer::Lock is
            // (out BYTE*, out maxLength, out currentLength).
            buffer.Lock(out var data, out _, out var currentLength);
            var pcm = new byte[currentLength];
            System.Runtime.InteropServices.Marshal.Copy(data, pcm, 0, currentLength);
            buffer.Unlock();
            return new DecodedAudioChunk(pcm, timestamp);
        }
    }

    private static (int width, int height) ReadFrameSize(IMFMediaType type)
    {
        ulong packed = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
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
