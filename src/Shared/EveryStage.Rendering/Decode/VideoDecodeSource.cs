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
///
/// The audio track is optional (<see cref="HasAudio"/>). A file with no audio stream (screen
/// recordings, silent clips) or with an audio codec MF can't decode still plays its video: the audio
/// stream is deselected and the caller paces video against a wall clock instead. Previously the
/// constructor unconditionally negotiated and read the audio stream's type, so a video without an
/// audio track failed to load at all.
///
/// Construction failures are thrown as <see cref="MediaOpenException"/> tagged with the stage that
/// failed and whatever codec names could be read, for the Preview error card.
/// </summary>
public sealed class VideoDecodeSource : IDisposable
{
    private readonly IMFSourceReader _reader;
    public int VideoWidth { get; }
    public int VideoHeight { get; }

    /// <summary>False when the file has no audio stream or its audio codec couldn't be decoded to
    /// 16-bit PCM; <see cref="AudioChannels"/>/<see cref="AudioSampleRate"/> are 0 and
    /// <see cref="ReadNextAudioChunk"/> always returns null in that case.</summary>
    public bool HasAudio { get; }
    public int AudioChannels { get; }
    public int AudioSampleRate { get; }

    /// <summary>Native (compressed) format names, e.g. "H.264" / "AAC"; null if absent/unreadable.</summary>
    public string? VideoCodec { get; }
    public string? AudioCodec { get; }

    /// <summary>Why the file's audio stream isn't used when <see cref="HasAudio"/> is false; null when
    /// the file simply has no audio stream.</summary>
    public string? AudioUnavailableReason { get; }

    public VideoDecodeSource(string filePathOrUrl, D3D11Device gpu)
    {
        MediaFactory.MFStartup().CheckError();

        // MFStartup() above already succeeded; if anything below throws, no instance exists for the
        // caller to Dispose(), so this constructor must release the reader and balance MFStartup
        // itself (same shape as every other MF-backed constructor in this repo).
        try
        {
            try
            {
                using var attributes = MediaFactory.MFCreateAttributes(2);
                attributes.Set(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1u);
                attributes.Set(MF_SOURCE_READER_D3D_MANAGER, gpu.DeviceManager);
                _reader = MediaFactory.MFCreateSourceReaderFromURL(filePathOrUrl, attributes);
            }
            catch (Exception ex)
            {
                throw new MediaOpenException(MediaFailureStage.Open, "无法打开媒体文件：" + ex.Message, ex);
            }

            VideoCodec = MediaReaderInfo.TryDescribeNativeFormat(_reader, SourceReaderIndex.FirstVideoStream);
            AudioCodec = MediaReaderInfo.TryDescribeNativeFormat(_reader, SourceReaderIndex.FirstAudioStream);

            // Force NV12 on the video stream: keeps the decoder's native DXVA surface format flowing
            // straight through instead of an internal color-conversion transform breaking the
            // zero-copy chain ahead of us.
            try
            {
                using var videoType = MediaFactory.MFCreateMediaType();
                videoType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                videoType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
                _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, videoType);
                using var actualVideoType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
                (VideoWidth, VideoHeight) = ReadFrameSize(actualVideoType);
            }
            catch (Exception ex)
            {
                string message = VideoCodec == null
                    ? "文件中没有可解码的视频流：" + ex.Message
                    : $"视频编码 {VideoCodec} 无法解码（系统缺少对应解码器或编码不受支持）：" + ex.Message;
                throw new MediaOpenException(MediaFailureStage.VideoFormat, message, ex, VideoCodec, AudioCodec);
            }
            _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            // Audio is best effort: no stream, or a codec MF can't turn into 16-bit PCM, leaves the
            // video playable on its own.
            if (AudioCodec != null)
            {
                try
                {
                    using var audioType = MediaFactory.MFCreateMediaType();
                    audioType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                    audioType.Set(MF_MT_SUBTYPE, MFAudioFormat_PCM);
                    // 16-bit is what AudioPlaybackClock is built for; ask for it explicitly rather
                    // than trusting the decoder's default, and verify what came back (audit C-6).
                    audioType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                    _reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, audioType);
                    using var actualAudioType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
                    uint bits = actualAudioType.GetUInt32(MediaTypeAttributeKeys.AudioBitsPerSample);
                    if (bits != 16) throw new NotSupportedException($"解码器输出 {bits} 位 PCM，播放链路需要 16 位");
                    AudioChannels = (int)actualAudioType.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
                    AudioSampleRate = (int)actualAudioType.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
                    HasAudio = AudioChannels > 0 && AudioSampleRate > 0;
                }
                catch (Exception ex)
                {
                    AudioUnavailableReason = $"音频编码 {AudioCodec} 无法解码，按无声视频播放：{ex.Message}";
                }
            }
            if (HasAudio)
            {
                _reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
            }
            else
            {
                AudioChannels = 0;
                AudioSampleRate = 0;
                // Deselect so the reader doesn't queue up audio samples nobody will ever read.
                try { _reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, false); } catch (Exception) { }
            }
        }
        catch
        {
            _reader?.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    // A null sample WITHOUT EndOfStream is a documented, normal mid-stream event (MF_SOURCE_READERF_
    // STREAMTICK — a gap/discontinuity — or a mid-stream media-type change), NOT end of playback. The
    // old "sample == null => return null" treated it as EOS and truncated content (audit A-9), which
    // the consumer then logs as a normal completion. Loop past an empty read to the next real sample
    // instead, bounded so a pathological source that never yields a sample and never signals EOS can't
    // spin this decode thread forever (that case still degrades to "stream ended"). Returns null only on
    // genuine EndOfStream or on exhausting the retry budget.
    private const int MaxEmptyReadRetries = 128;

    private IMFSample? ReadNextNonEmptySample(SourceReaderIndex stream, out long timestamp)
    {
        for (int attempt = 0; attempt < MaxEmptyReadRetries; attempt++)
        {
            var sample = _reader.ReadSample(stream, SourceReaderControlFlag.None, out _, out var streamFlags, out timestamp);
            if ((streamFlags & SourceReaderFlag.EndOfStream) != 0) return null;
            if (sample != null) return sample;
        }
        timestamp = 0;
        return null;
    }

    /// <summary>Returns null once the video stream reports end-of-stream (a mid-stream empty read is
    /// retried past rather than mistaken for EOS — see <see cref="ReadNextNonEmptySample"/>).</summary>
    public DecodedVideoFrame? ReadNextVideoFrame()
    {
        var sample = ReadNextNonEmptySample(SourceReaderIndex.FirstVideoStream, out var timestamp);
        if (sample == null)
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

    /// <summary>Total duration from the container, or null if it doesn't report one.</summary>
    public TimeSpan? TryGetDuration() => MediaReaderInfo.TryGetDuration(_reader);

    /// <summary>Repositions both streams. Video lands on the key frame at or before
    /// <paramref name="position"/>; callers pacing by timestamp drop the frames before the target.</summary>
    public bool TrySeek(TimeSpan position) => MediaReaderInfo.TrySeek(_reader, position);

    /// <summary>Returns null once the audio stream reports end-of-stream, and always when
    /// <see cref="HasAudio"/> is false.</summary>
    public DecodedAudioChunk? ReadNextAudioChunk()
    {
        if (!HasAudio) return null;
        var sample = ReadNextNonEmptySample(SourceReaderIndex.FirstAudioStream, out var timestamp);
        if (sample == null)
            return null;

        using (sample)
        using (var buffer = sample.ConvertToContiguousBuffer())
        {
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
