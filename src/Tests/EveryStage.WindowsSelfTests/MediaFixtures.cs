using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

/// <summary>
/// Generates real media files for the self-tests with the encoders Windows itself ships (Media
/// Foundation Sink Writer) — no FFmpeg, no binary fixtures checked into the repo. Every file is
/// synthetic: a moving colour bar for video, a sine tone for audio.
///
/// Each writer throws if Windows can't produce that format on this machine; callers record that as
/// "未验证" for the format matrix rather than failing the whole suite.
/// </summary>
internal static class MediaFixtures
{
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private const uint MFVideoInterlace_Progressive = 2;

    public static void WriteVideo(string path, Guid container, Guid videoSubtype, bool withAudio,
        Guid audioSubtype = default, int width = 640, int height = 360, int fps = 30, double seconds = 3)
    {
        MediaFactory.MFStartup().CheckError();
        IMFSinkWriter? writer = null;
        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(1);
            attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, container);
            writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);

            using var videoOut = MediaFactory.MFCreateMediaType();
            videoOut.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            videoOut.Set(MF_MT_SUBTYPE, videoSubtype);
            videoOut.Set(MediaTypeAttributeKeys.AvgBitrate, 1_500_000u);
            videoOut.Set(MediaTypeAttributeKeys.InterlaceMode, MFVideoInterlace_Progressive);
            videoOut.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
            videoOut.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
            videoOut.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            int videoStream = writer.AddStream(videoOut);

            using var videoIn = MediaFactory.MFCreateMediaType();
            videoIn.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            videoIn.Set(MF_MT_SUBTYPE, VideoFormatGuids.NV12);
            videoIn.Set(MediaTypeAttributeKeys.InterlaceMode, MFVideoInterlace_Progressive);
            videoIn.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
            videoIn.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
            videoIn.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            writer.SetInputMediaType(videoStream, videoIn, null!);

            (int Index, int Rate, int Channels) audioStream = (-1, 0, 0);
            if (withAudio) audioStream = AddAudioStream(writer, audioSubtype);

            writer.BeginWriting();
            int frames = (int)Math.Round(seconds * fps);
            long frameDuration = 10_000_000L / fps;
            int ySize = width * height, frameBytes = ySize + ySize / 2;
            var nv12 = new byte[frameBytes];
            for (int i = 0; i < frames; i++)
            {
                FillNv12(nv12, width, height, i, frames);
                WriteSample(writer, videoStream, nv12, i * frameDuration, frameDuration);
            }
            if (withAudio) WriteSineTone(writer, audioStream.Index, seconds, audioStream.Rate, audioStream.Channels);
            writer.Finalize();
        }
        finally
        {
            writer?.Dispose();
            MediaFactory.MFShutdown();
        }
    }

    public static void WriteAudio(string path, Guid container, Guid audioSubtype, double seconds = 3)
    {
        MediaFactory.MFStartup().CheckError();
        IMFSinkWriter? writer = null;
        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(1);
            attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, container);
            writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);
            var stream = AddAudioStream(writer, audioSubtype);
            writer.BeginWriting();
            WriteSineTone(writer, stream.Index, seconds, stream.Rate, stream.Channels);
            writer.Finalize();
        }
        finally
        {
            writer?.Dispose();
            MediaFactory.MFShutdown();
        }
    }

    /// <summary>Plain RIFF/WAVE PCM, written by hand (no encoder involved).</summary>
    public static void WriteWav(string path, double seconds = 3, int sampleRate = 48000, int channels = 2)
    {
        byte[] pcm = SinePcm(seconds, sampleRate, channels);
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + pcm.Length); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(sampleRate);
        w.Write(sampleRate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(pcm.Length); w.Write(pcm);
    }

    private static (int Index, int Rate, int Channels) AddAudioStream(IMFSinkWriter writer, Guid subtype)
    {
        int sampleRate = 48000, channels = 2;
        using var audioOut = MediaFactory.MFCreateMediaType();
        audioOut.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        audioOut.Set(MF_MT_SUBTYPE, subtype);
        audioOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
        audioOut.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
        if (subtype == AudioFormatGuids.Pcm)
        {
            audioOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            audioOut.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(channels * 2));
            audioOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(sampleRate * channels * 2));
        }
        else if (subtype == AudioFormatGuids.Flac)
        {
            audioOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        }
        else if (subtype == AudioFormatGuids.Aac)
        {
            audioOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            audioOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 16000u); // 128 kbps
        }
        int stream = subtype == AudioFormatGuids.Pcm || subtype == AudioFormatGuids.Flac || subtype == AudioFormatGuids.Aac
            ? writer.AddStream(audioOut)
            : AddPickedStream(writer, subtype, ref sampleRate, ref channels);

        using var audioIn = MediaFactory.MFCreateMediaType();
        audioIn.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        audioIn.Set(MF_MT_SUBTYPE, AudioFormatGuids.Pcm);
        audioIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
        audioIn.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
        audioIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        audioIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(channels * 2));
        audioIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(sampleRate * channels * 2));
        writer.SetInputMediaType(stream, audioIn, null!);
        return (stream, sampleRate, channels);
    }

    /// <summary>Encoders like MP3 and WMA only accept an output type drawn from their own list (they need
    /// codec-private user data a hand-built type doesn't carry), so ask Windows for that list and take
    /// the entry closest to 48 kHz stereo ~128 kbps.</summary>
    private static IMFMediaType PickEncoderOutputType(Guid subtype, int sampleRate, int channels)
    {
        const int MFT_ENUM_FLAG_ALL_EXCEPT_FIELDOFUSE = 0x3F & ~0x10;
        MediaFactory.MFTranscodeGetAudioOutputAvailableTypes(subtype, MFT_ENUM_FLAG_ALL_EXCEPT_FIELDOFUSE, null!, out var types).CheckError();
        using (types)
        {
            IMFMediaType? best = null;
            long bestScore = long.MaxValue;
            for (int i = 0; i < types.ElementCount; i++)
            {
                using var element = (SharpGen.Runtime.ComObject)types.GetElement(i);
                var type = element.QueryInterface<IMFMediaType>();
                long rate = type.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
                long ch = type.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
                type.GetUInt32(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, out uint bytesPerSecond);
                long score = (rate == sampleRate ? 0 : 1_000_000_000L) + (ch == channels ? 0 : 100_000_000L) + Math.Abs(bytesPerSecond - 16000L);
                if (score < bestScore) { best?.Dispose(); best = type; bestScore = score; }
                else type.Dispose();
            }
            return best ?? throw new NotSupportedException($"No encoder output type available for {subtype}.");
        }
    }

    private static int AddPickedStream(IMFSinkWriter writer, Guid subtype, ref int sampleRate, ref int channels)
    {
        using var picked = PickEncoderOutputType(subtype, sampleRate, channels);
        // Encoders don't resample: the PCM fed in must match the output type actually chosen.
        sampleRate = (int)picked.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
        channels = (int)picked.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
        return writer.AddStream(picked);
    }

    private static void WriteSineTone(IMFSinkWriter writer, int stream, double seconds, int sampleRate, int channels)
    {
        int chunkSamples = sampleRate / 10; // 100 ms chunks
        byte[] all = SinePcm(seconds, sampleRate, channels);
        int chunkBytes = chunkSamples * channels * 2;
        long chunkDuration = 10_000_000L * chunkSamples / sampleRate;
        for (int offset = 0, index = 0; offset < all.Length; offset += chunkBytes, index++)
        {
            int length = Math.Min(chunkBytes, all.Length - offset);
            WriteSample(writer, stream, all.AsSpan(offset, length).ToArray(), index * chunkDuration,
                10_000_000L * (length / (channels * 2)) / sampleRate);
        }
    }

    private static byte[] SinePcm(double seconds, int sampleRate, int channels)
    {
        int samples = (int)(seconds * sampleRate);
        var pcm = new byte[samples * channels * 2];
        for (int i = 0; i < samples; i++)
        {
            short value = (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * 8000);
            for (int c = 0; c < channels; c++)
                BitConverter.TryWriteBytes(pcm.AsSpan((i * channels + c) * 2, 2), value);
        }
        return pcm;
    }

    private static void WriteSample(IMFSinkWriter writer, int stream, byte[] data, long time, long duration)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        buffer.Lock(out IntPtr ptr, out _, out _);
        Marshal.Copy(data, 0, ptr, data.Length);
        buffer.Unlock();
        buffer.CurrentLength = data.Length;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        writer.WriteSample(stream, sample);
    }

    /// <summary>Dark blue background with a bright bar sweeping left to right over the clip.</summary>
    private static void FillNv12(byte[] nv12, int width, int height, int frame, int frames)
    {
        int ySize = width * height;
        int barX = (int)((long)frame * (width - 40) / Math.Max(1, frames - 1));
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                nv12[y * width + x] = (byte)(x >= barX && x < barX + 40 ? 220 : 40);
        for (int i = ySize; i < nv12.Length; i += 2) { nv12[i] = 160; nv12[i + 1] = 110; }
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;
}
