using Vortice.MediaFoundation;
using static EveryStage.Rendering.Decode.WellKnownGuids;

namespace EveryStage.Rendering.Decode;

/// <summary>
/// Audio-only counterpart to <see cref="VideoDecodeSource"/> — same <see cref="IMFSourceReader"/>-
/// based approach (PLANNING.md §4.2), but for a file with no video stream at all (or one this class
/// deliberately never selects/reads even if present, e.g. embedded cover art some containers expose
/// as a "video" stream). Kept as an entirely separate class rather than adding an "audio-only" mode
/// to <see cref="VideoDecodeSource"/>: that class's constructor takes a <see cref="EveryStage.Rendering.D3D11Device"/>
/// and sets <c>MF_SOURCE_READER_D3D_MANAGER</c>/<c>MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS</c> purely
/// for the video stream's DXVA hardware decode — none of that is needed or meaningful here.
///
/// Duration and seeking go through <see cref="MediaReaderInfo"/>'s strongly-typed calls. They used
/// to go through <c>dynamic</c> with guessed member shapes that turned out not to exist on the
/// installed Vortice version, so both silently failed every time: seeking never moved and the total
/// duration was never known (no progress readout, no fade-out).
/// </summary>
public sealed class AudioDecodeSource : IDisposable
{
    private readonly IMFSourceReader _reader;
    public int AudioChannels { get; }
    public int AudioSampleRate { get; }

    /// <summary>Native (compressed) format name, e.g. "MP3" / "AAC"; null if unreadable.</summary>
    public string? AudioCodec { get; }

    public AudioDecodeSource(string filePathOrUrl)
    {
        MediaFactory.MFStartup().CheckError();

        // MFStartup() above already succeeded; if anything below throws, no instance exists for the
        // caller to Dispose(), so release the reader and balance MFStartup here (same shape as every
        // other MF-backed constructor in this repo).
        try
        {
            try
            {
                using var attributes = MediaFactory.MFCreateAttributes(0);
                _reader = MediaFactory.MFCreateSourceReaderFromURL(filePathOrUrl, attributes);
            }
            catch (Exception ex)
            {
                throw new MediaOpenException(MediaFailureStage.Open, "无法打开音频文件：" + ex.Message, ex);
            }

            AudioCodec = MediaReaderInfo.TryDescribeNativeFormat(_reader, SourceReaderIndex.FirstAudioStream);

            try
            {
                using var audioType = MediaFactory.MFCreateMediaType();
                audioType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                audioType.Set(MF_MT_SUBTYPE, MFAudioFormat_PCM);
                // AudioPlaybackClock and ComputePeakLevel both assume 16-bit PCM. Request it
                // explicitly and verify the negotiated result instead of assuming (audit C-6: a
                // decoder defaulting to 24/32-bit would otherwise play as noise with no error).
                audioType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                _reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, audioType);

                using var actualAudioType = _reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
                uint bits = actualAudioType.GetUInt32(MediaTypeAttributeKeys.AudioBitsPerSample);
                if (bits != 16) throw new NotSupportedException($"解码器输出 {bits} 位 PCM，播放链路需要 16 位");
                AudioChannels = (int)actualAudioType.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
                AudioSampleRate = (int)actualAudioType.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
            }
            catch (Exception ex)
            {
                string message = AudioCodec == null
                    ? "文件中没有可解码的音频流：" + ex.Message
                    : $"音频编码 {AudioCodec} 无法解码（系统缺少对应解码器或编码不受支持）：" + ex.Message;
                throw new MediaOpenException(MediaFailureStage.AudioFormat, message, ex, audioCodec: AudioCodec);
            }

            _reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
            // Deliberately never selects the video stream sentinel — leaving it unselected means
            // ReadSample is never called against it (embedded cover art, etc.).
        }
        catch
        {
            _reader?.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    /// <summary>File duration, or null if the container doesn't report one. Every caller treats null
    /// as "unknown duration" (no fade-out, position shown without a total).</summary>
    public TimeSpan? TryGetDuration() => MediaReaderInfo.TryGetDuration(_reader);

    /// <summary>Repositions the reader for <c>AudioContentController.TrySeekTo</c>.</summary>
    public bool TrySeek(TimeSpan position) => MediaReaderInfo.TrySeek(_reader, position);

    // A null sample WITHOUT EndOfStream is a documented, normal mid-stream event (MF_SOURCE_READERF_
    // STREAMTICK — a gap/discontinuity — or a mid-stream media-type change), NOT end of playback. The
    // old "sample == null => return null" treated it as EOS and truncated content (audit A-9), which
    // the consumer then logs as a normal completion. Loop past an empty read to the next real sample
    // instead, bounded so a pathological source that never yields a sample and never signals EOS can't
    // spin this decode thread forever. Returns null only on genuine EndOfStream or on exhausting the budget.
    private const int MaxEmptyReadRetries = 128;

    private IMFSample? ReadNextNonEmptySample(out long timestamp)
    {
        for (int attempt = 0; attempt < MaxEmptyReadRetries; attempt++)
        {
            var sample = _reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None, out _, out var streamFlags, out timestamp);
            if ((streamFlags & SourceReaderFlag.EndOfStream) != 0) return null;
            if (sample != null) return sample;
        }
        timestamp = 0;
        return null;
    }

    /// <summary>Returns null once the audio stream reports end-of-stream (a mid-stream empty read is
    /// retried past rather than mistaken for EOS — see <see cref="ReadNextNonEmptySample"/>).</summary>
    public DecodedAudioChunk? ReadNextChunk()
    {
        var sample = ReadNextNonEmptySample(out var timestamp);
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

    public void Dispose()
    {
        _reader.Dispose();
        MediaFactory.MFShutdown();
    }
}
