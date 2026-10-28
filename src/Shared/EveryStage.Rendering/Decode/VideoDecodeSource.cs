using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using static EveryStage.Rendering.Decode.WellKnownGuids;

namespace EveryStage.Rendering.Decode;

/// <summary>The decoder's own texture-array slot for one video frame. No copy has happened yet —
/// the caller (SwapChainPresenter) consumes ArraySlice directly as a video-processor input.
/// <paramref name="Sample"/> keeps the slot reserved: <see cref="Release"/> once the frame is presented.</summary>
public readonly record struct DecodedVideoFrame(ID3D11Texture2D Texture, int ArraySlice, int Width, int Height, long TimestampTicks, IMFSample? Sample = null)
{
    /// <summary>Releases the texture reference and returns the slot to the decoder's pool.</summary>
    public void Release()
    {
        Texture.Dispose();
        Sample?.Dispose();
    }
}

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

    /// <summary>Display size of the decoded frames (the aperture, not the decoder's padded surface: a
    /// 1080p stream decodes into 1088-row surfaces). Updated when the stream changes format mid-way.</summary>
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }

    /// <summary>False when the file has no audio stream or its audio codec couldn't be decoded to
    /// 16-bit PCM; <see cref="AudioChannels"/>/<see cref="AudioSampleRate"/> are 0 and
    /// <see cref="ReadNextAudioChunk"/> always returns null in that case.</summary>
    public bool HasAudio { get; private set; }

    /// <summary>Channels/rate of the PCM <see cref="ReadNextAudioChunk"/> returns — what the playback
    /// clock must be built with. Fixed for this source's life: more than two decoded channels are folded
    /// to stereo (audit B-20), and a mid-stream format change is converted back to this (audit A-25).</summary>
    public int AudioChannels { get; private set; }
    public int AudioSampleRate { get; private set; }

    // What the decoder currently delivers; changes when MF reports CURRENTMEDIATYPECHANGED.
    private int _decodedChannels;
    private int _decodedRate;

    /// <summary>Native (compressed) format names, e.g. "H.264" / "AAC"; null if absent/unreadable.</summary>
    public string? VideoCodec { get; }
    public string? AudioCodec { get; private set; }

    /// <summary>Why the file's audio stream isn't used when <see cref="HasAudio"/> is false; null when
    /// the file simply has no audio stream.</summary>
    public string? AudioUnavailableReason { get; private set; }

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
                (VideoWidth, VideoHeight) = MediaReaderInfo.DisplaySize(actualVideoType);
            }
            catch (Exception ex)
            {
                string message = VideoCodec == null
                    ? "文件中没有可解码的视频流：" + ex.Message
                    : IsTenBit(_reader)
                        // The decoder exists; it only offers 10-bit output (HDR, iPhone HEVC), which this
                        // 8-bit NV12 pipeline cannot take. Saying "decoder missing" sent people looking for
                        // codec packs (audit C-32).
                        ? $"视频编码 {VideoCodec} 为 10 位色深（常见于 HDR / iPhone 录像），当前播放链路只支持 8 位视频，请转码为 8 位 H.264 后播放"
                        : $"视频编码 {VideoCodec} 无法解码（系统缺少对应解码器或编码不受支持）：" + ex.Message;
                throw new MediaOpenException(MediaFailureStage.VideoFormat, message, ex, VideoCodec, AudioCodec);
            }
            _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            // Audio is best effort: no stream, or a codec MF can't turn into 16-bit PCM, leaves the
            // video playable on its own. When the first audio track cannot be decoded, the others are
            // tried: MKV files commonly carry DTS (no Windows decoder) first and AC-3 second, and used to
            // play silently although a decodable track was right there (audit C-30).
            if (AudioCodec != null)
            {
                Exception? firstFailure = null;
                try { UseAudioStream(SourceReaderIndex.FirstAudioStream); }
                catch (Exception ex) { firstFailure = ex; }
                if (!HasAudio)
                {
                    foreach (int index in AudioStreamIndices(_reader))
                    {
                        try
                        {
                            UseAudioStream((SourceReaderIndex)index);
                            AudioCodec = MediaReaderInfo.TryDescribeNativeFormat(_reader, (SourceReaderIndex)index) ?? AudioCodec;
                            break;
                        }
                        catch (Exception) { }
                    }
                }
                if (!HasAudio)
                    AudioUnavailableReason = $"音频编码 {AudioCodec} 无法解码，按无声视频播放：{firstFailure?.Message}";
            }
            // Only the stream in use stays selected, so the reader doesn't queue up samples nobody reads.
            try { _reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, false); } catch (Exception) { }
            foreach (int index in AudioStreamIndices(_reader))
                try { _reader.SetStreamSelection((SourceReaderIndex)index, false); } catch (Exception) { }
            if (HasAudio)
            {
                _reader.SetStreamSelection(_audioStream, true);
            }
            else
            {
                AudioChannels = 0;
                AudioSampleRate = 0;
            }
        }
        catch
        {
            _reader?.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    // The audio stream in use: the reader's first audio stream, or another track when that one cannot be decoded.
    private SourceReaderIndex _audioStream = SourceReaderIndex.FirstAudioStream;

    /// <summary>Negotiates 16-bit PCM on <paramref name="stream"/> and adopts it; throws when the stream
    /// cannot be decoded.</summary>
    private void UseAudioStream(SourceReaderIndex stream)
    {
        using var audioType = MediaFactory.MFCreateMediaType();
        audioType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        audioType.Set(MF_MT_SUBTYPE, MFAudioFormat_PCM);
        // 16-bit is what AudioPlaybackClock is built for; ask for it explicitly rather
        // than trusting the decoder's default, and verify what came back (audit C-6).
        audioType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        _reader.SetCurrentMediaType(stream, audioType);
        using var actualAudioType = _reader.GetCurrentMediaType(stream);
        var (bits, channels, rate) = MediaReaderInfo.PcmFormat(actualAudioType);
        if (bits != 16) throw new NotSupportedException($"解码器输出 {bits} 位 PCM，播放链路需要 16 位");
        if (channels <= 0 || rate <= 0) throw new NotSupportedException("解码器没有报告声道数或采样率");
        _decodedChannels = channels;
        _decodedRate = rate;
        AudioChannels = EveryStage.Rendering.Audio.PcmDownmix.OutputChannels(channels);
        AudioSampleRate = rate;
        _audioStream = stream;
        HasAudio = true;
    }

    /// <summary>Indices of the reader's audio streams, in file order.</summary>
    private static List<int> AudioStreamIndices(IMFSourceReader reader)
    {
        var indices = new List<int>();
        for (int index = 0; index < 32; index++)
        {
            try
            {
                using var native = reader.GetNativeMediaType((SourceReaderIndex)index, 0);
                if (native.GetGUID(MF_MT_MAJOR_TYPE) == MFMediaType_Audio) indices.Add(index);
            }
            catch (Exception)
            {
                break; // MF_E_INVALIDSTREAMNUMBER: no more streams
            }
        }
        return indices;
    }

    private static readonly Guid MFVideoFormat_P010 = new("30313050-0000-0010-8000-00aa00389b71");

    /// <summary>True when the video decoder accepts 10-bit P010 output (after NV12 was refused).</summary>
    private static bool IsTenBit(IMFSourceReader reader)
    {
        try
        {
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            type.Set(MF_MT_SUBTYPE, MFVideoFormat_P010);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
            return true;
        }
        catch (Exception)
        {
            return false;
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
            // The sample returned WITH this flag is already in the new format (audit A-25).
            if ((streamFlags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
            {
                OnCurrentMediaTypeChanged(stream);
                if (stream == _audioStream && !HasAudio) { sample?.Dispose(); return null; }
            }
            if (sample != null) return sample;
        }
        timestamp = 0;
        return null;
    }

    private void OnCurrentMediaTypeChanged(SourceReaderIndex stream)
    {
        try
        {
            using var type = _reader.GetCurrentMediaType(stream);
            if (stream == SourceReaderIndex.FirstVideoStream)
            {
                (VideoWidth, VideoHeight) = MediaReaderInfo.DisplaySize(type);
                return;
            }
            var (bits, channels, rate) = MediaReaderInfo.PcmFormat(type);
            if (bits != 16 || channels <= 0 || rate <= 0)
            {
                AudioUnavailableReason = $"音频格式在播放中变为 {bits} 位 {channels} 声道 {rate} Hz，无法继续出声";
                DisableAudio();
                return;
            }
            _decodedChannels = channels;
            _decodedRate = rate;
        }
        catch (Exception)
        {
            // Keep the previous format: better a wrong-sounding chunk than a dead decode thread.
        }
    }

    /// <summary>Returns null once the video stream reports end-of-stream (a mid-stream empty read is
    /// retried past rather than mistaken for EOS — see <see cref="ReadNextNonEmptySample"/>). The caller
    /// owns the frame and must <see cref="DecodedVideoFrame.Release"/> it once presented.</summary>
    public DecodedVideoFrame? ReadNextVideoFrame()
    {
        var sample = ReadNextNonEmptySample(SourceReaderIndex.FirstVideoStream, out var timestamp);
        if (sample == null)
            return null;

        try
        {
            using var buffer = sample.ConvertToContiguousBuffer();
            IMFDXGIBuffer dxgiBuffer;
            try
            {
                dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
            }
            catch (Exception ex)
            {
                // System-memory frames: this codec only has a software decoder on this machine (MPEG-4
                // Part 2 / Xvid, Motion JPEG, …). The zero-copy pipeline needs GPU (DXVA) frames; say so
                // rather than surfacing "no such interface" (audit C-31).
                throw new MediaOpenException(MediaFailureStage.Decode,
                    $"视频编码 {VideoCodec} 在本机只能软件解码，当前播放链路需要显卡硬件解码，请转码为 H.264 后播放", ex, VideoCodec, AudioCodec);
            }
            using (dxgiBuffer)
            {
                var texture = new ID3D11Texture2D(dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID));
                int arraySlice = (int)dxgiBuffer.SubresourceIndex;
                // The sample travels with the frame: releasing it returns this texture-array slice to the
                // decoder's pool, which may then decode the next frame into it — so it must stay alive
                // until the frame has been presented (audit C-28).
                return new DecodedVideoFrame(texture, arraySlice, VideoWidth, VideoHeight, timestamp, sample);
            }
        }
        catch
        {
            sample.Dispose();
            throw;
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
        var sample = ReadNextNonEmptySample(_audioStream, out var timestamp);
        if (sample == null)
            return null;

        using (sample)
        using (var buffer = sample.ConvertToContiguousBuffer())
        {
            buffer.Lock(out var data, out _, out var currentLength);
            var pcm = new byte[currentLength];
            System.Runtime.InteropServices.Marshal.Copy(data, pcm, 0, currentLength);
            buffer.Unlock();
            var converted = EveryStage.Rendering.Audio.PcmConverter.Convert(pcm, _decodedChannels, _decodedRate, AudioChannels, AudioSampleRate);
            return new DecodedAudioChunk(converted, timestamp);
        }
    }

    /// <summary>Stops using the audio track for the rest of this source's life (the caller lost its
    /// audio output and continues silently): deselects the stream so the reader stops queueing audio
    /// samples nobody will read. Call from the thread that reads samples.</summary>
    public void DisableAudio()
    {
        if (!HasAudio) return;
        HasAudio = false;
        try { _reader.SetStreamSelection(_audioStream, false); } catch (Exception) { }
    }

    public void Dispose()
    {
        _reader.Dispose();
        MediaFactory.MFShutdown();
    }
}
