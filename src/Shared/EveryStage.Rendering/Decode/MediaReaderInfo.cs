using Vortice.MediaFoundation;

namespace EveryStage.Rendering.Decode;

/// <summary>Where opening or decoding a media file failed, in the words the operator sees on the
/// Preview error card (PLANNING "错误卡片：失败阶段").</summary>
public enum MediaFailureStage
{
    Open,           // 打开文件（文件缺失、无权限、容器无法识别）
    VideoFormat,    // 协商视频解码格式（编码不受支持 / 缺少解码器）
    AudioFormat,    // 协商音频解码格式
    Decode,         // 播放中解码
    Render,         // 呈现
}

/// <summary>A media open/decode failure carrying what the Preview error card needs: the stage it
/// failed at and whatever codec information could be read before it failed. Codec fields are best
/// effort — null when the container itself could not be opened.</summary>
public sealed class MediaOpenException : Exception
{
    public MediaFailureStage Stage { get; }
    public string? VideoCodec { get; }
    public string? AudioCodec { get; }

    public MediaOpenException(MediaFailureStage stage, string message, Exception? inner, string? videoCodec = null, string? audioCodec = null)
        : base(message, inner)
    {
        Stage = stage;
        VideoCodec = videoCodec;
        AudioCodec = audioCodec;
    }
}

/// <summary>
/// Strongly-typed wrappers for the <see cref="IMFSourceReader"/> calls both decode sources need.
///
/// These replace an earlier <c>dynamic</c>-dispatch approach that guessed at Vortice's member shapes
/// and swallowed every failure: checked against the installed Vortice.MediaFoundation 3.6.2 by
/// reflection, the guesses were wrong — <c>SetCurrentPosition</c> takes a single <see cref="long"/>
/// (not a GUID + value), and <c>GetPresentationAttribute</c> has no <see cref="uint"/> overload — so the
/// old seek always returned false and the old duration query always returned null, silently. These use
/// the real overloads.
/// </summary>
public static class MediaReaderInfo
{
    /// <summary>File duration from the media source's presentation descriptor, or null if the source
    /// doesn't report one (live/streaming sources, some broken files).</summary>
    public static TimeSpan? TryGetDuration(IMFSourceReader reader)
    {
        try
        {
            var variant = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
            long ticks = variant.Value switch
            {
                ulong u => checked((long)u),
                long l => l,
                uint u32 => u32,
                int i => i,
                _ => 0,
            };
            return ticks > 0 ? TimeSpan.FromTicks(ticks) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Repositions the reader. MF snaps video to the preceding key frame, so the next samples
    /// may carry timestamps somewhat before <paramref name="position"/>; callers pace by timestamp and
    /// naturally skip past them.</summary>
    public static bool TrySeek(IMFSourceReader reader, TimeSpan position)
    {
        try
        {
            reader.SetCurrentPosition(Math.Max(0, position.Ticks));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Human-readable name of a stream's native (compressed) format, e.g. "H.264", "AAC",
    /// "MP3". Null when the stream doesn't exist or can't be queried.</summary>
    public static string? TryDescribeNativeFormat(IMFSourceReader reader, SourceReaderIndex stream)
    {
        try
        {
            using var native = reader.GetNativeMediaType(stream, 0);
            return DescribeSubtype(native.GetGUID(MediaTypeAttributeKeys.Subtype), audio: stream == SourceReaderIndex.FirstAudioStream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>MF subtypes are FOURCC/wave-format-tag GUIDs of the form {XXXXXXXX-0000-0010-8000-00AA00389B71}.
    /// Well-known ones get a friendly name; anything else falls back to its FOURCC or tag.</summary>
    public static string DescribeSubtype(Guid subtype, bool audio)
    {
        byte[] bytes = subtype.ToByteArray();
        uint data1 = BitConverter.ToUInt32(bytes, 0);
        bool isFourCcFamily = subtype.ToString().EndsWith("-0000-0010-8000-00aa00389b71", StringComparison.OrdinalIgnoreCase);
        if (audio && isFourCcFamily)
        {
            return data1 switch
            {
                0x0001 => "PCM",
                0x0003 => "PCM（浮点）",
                0x0050 => "MPEG-1 Audio",
                0x0055 => "MP3",
                0x0161 => "WMA",
                0x0162 => "WMA Pro",
                0x0163 => "WMA Lossless",
                0x1610 => "AAC",
                0x1602 => "AAC（LATM）",
                0x2000 => "AC-3",
                0x2001 => "DTS",
                0xF1AC => "FLAC",
                0x6C61 => "ALAC",
                0x704F => "Opus",
                _ => $"音频格式 0x{data1:X4}",
            };
        }
        if (isFourCcFamily)
        {
            string fourCc = new string(bytes.Take(4).Select(b => b is >= 0x20 and < 0x7F ? (char)b : '?').ToArray());
            return fourCc.ToUpperInvariant() switch
            {
                "H264" or "AVC1" => "H.264",
                "HEVC" or "HVC1" or "H265" => "H.265/HEVC",
                "MP4V" => "MPEG-4 Part 2",
                "MP43" => "MS MPEG-4 v3",
                "WMV3" => "WMV9",
                "WVC1" => "VC-1",
                "MJPG" => "Motion JPEG",
                "VP80" => "VP8",
                "VP90" => "VP9",
                "AV01" => "AV1",
                "MPG2" or "MP2V" => "MPEG-2",
                _ => fourCc.Trim(),
            };
        }
        if (subtype == new Guid("e06d8026-db46-11cf-b4d1-00805f6cbbea")) return "MPEG-2";
        return subtype.ToString();
    }
}
