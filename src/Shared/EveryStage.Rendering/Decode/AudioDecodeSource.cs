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
/// for the video stream's DXVA hardware decode — none of that is needed or meaningful here, so this
/// class's constructor takes no GPU device at all, which a bolted-on flag couldn't express as
/// cleanly as a separate type.
///
/// Reuses <see cref="EveryStage.Rendering.Decode.DecodedAudioChunk"/> (same shape
/// <see cref="VideoDecodeSource"/> already defines) rather than declaring a duplicate record — the
/// two decode sources happen to produce identically-shaped chunks, and <see cref="EveryStage.Terminal.Playback.PlaybackEngine"/>'s
/// eventual consumer doesn't care which decode source a given chunk came from.
/// </summary>
public sealed class AudioDecodeSource : IDisposable
{
    private readonly IMFSourceReader _reader;
    public int AudioChannels { get; }
    public int AudioSampleRate { get; }

    public AudioDecodeSource(string filePathOrUrl)
    {
        MediaFactory.MFStartup().CheckError();

        // Bug fixed here (same shape as H264HardwareDecoder/H264HardwareEncoder's own constructor
        // fixes, see either's doc comment): MFStartup() above already succeeded by the time
        // execution reaches this line — but if anything below throws, this constructor never
        // finishes, so no AudioDecodeSource instance ever exists for its owner to later Dispose()
        // and hit the MFShutdown() call below. Without this try/catch, a bad/corrupt/unsupported
        // audio file (a real, not hypothetical, condition — this class exists specifically to open
        // arbitrary user-supplied files) would leak one MFStartup() reference count every time.
        try
        {
            // No attributes are actually needed for a pure audio read (no D3D hardware transform to
            // enable) — constructing a (currently empty) attributes object mirrors
            // MFCreateSourceReaderFromURL's call shape in VideoDecodeSource rather than guessing whether
            // Vortice's binding accepts a null IMFAttributes here, matching this project's convention of
            // not introducing a new unverified parameter shape when an already-used one is available.
            MediaFactory.MFCreateAttributes(out var attributes, 0).CheckError();
            using (attributes)
            {
                MediaFactory.MFCreateSourceReaderFromURL(filePathOrUrl, attributes, out _reader).CheckError();
            }

            MediaFactory.MFCreateMediaType(out var audioType).CheckError();
            using (audioType)
            {
                audioType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                audioType.Set(MF_MT_SUBTYPE, MFAudioFormat_PCM);
                _reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, audioType);
            }

            using var actualAudioType = _reader.GetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM);
            AudioChannels = (int)actualAudioType.Get<uint>(MediaTypeAttributeKeys.AudioNumChannels());
            AudioSampleRate = (int)actualAudioType.Get<uint>(MediaTypeAttributeKeys.AudioSamplesPerSecond());

            _reader.SetStreamSelection(MF_SOURCE_READER_FIRST_AUDIO_STREAM, true);
            // Deliberately never calls SetStreamSelection for the video stream sentinel — leaving it
            // unselected (the IMFSourceReader default for a stream this class never asks about) means
            // ReadSample is never called against it and this class never has to handle a video sample it
            // has no surface to present.
        }
        catch
        {
            _reader?.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    /// <summary>Returns null once the audio stream reports end-of-stream. Same PCM-off-as-CPU-memory
    /// reasoning as <see cref="VideoDecodeSource.ReadNextAudioChunk"/> — audio was never part of the
    /// zero-copy path either decode source exists to validate.</summary>
    public DecodedAudioChunk? ReadNextChunk()
    {
        _reader.ReadSample(MF_SOURCE_READER_FIRST_AUDIO_STREAM, SourceReaderControlFlags.None,
            out _, out var streamFlags, out var timestamp, out var sample);

        if ((streamFlags & SourceReaderFlags.Endofstream) != 0 || sample == null)
            return null;

        using (sample)
        using (var buffer = sample.ConvertToContiguousBuffer())
        {
            // NOTE: same unverified Lock() signature caveat as VideoDecodeSource.ReadNextAudioChunk
            // — see that method's doc comment, not repeated per-callsite elsewhere in this repo.
            var span = buffer.Lock(out _, out var currentLength);
            var pcm = new byte[currentLength];
            span.Slice(0, currentLength).CopyTo(pcm);
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
