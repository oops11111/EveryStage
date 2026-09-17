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

    /// <summary>Best-effort file duration, for <c>PlaybackEngine</c>'s fade-out (this project's
    /// README — <c>MediaFile.FadeDuration</c>/<c>VolumeFollowsFade</c>'s fade-out half). UNVERIFIED
    /// more thoroughly than any other Media Foundation call in this class: every other call here
    /// goes through a strongly-typed Vortice.MediaFoundation member already exercised successfully
    /// elsewhere in this same file (<c>_reader.GetCurrentMediaType</c>,
    /// <see cref="IMFAttributes.Get{T}"/>), but this sandbox has never had
    /// <c>IMFSourceReader::GetPresentationAttribute</c> — or its C# binding's exact shape — to check
    /// against anything, not even a raw GUID diff against Windows SDK headers the way
    /// <see cref="WellKnownGuids"/>'s other constants at least allow. To keep a wrong guess here
    /// from being a BUILD-BREAKING mistake (unlike a wrong runtime value, which the rest of this
    /// codebase can already degrade around), the entire call — the method name itself, not just the
    /// PROPVARIANT-equivalent result's value extraction — goes through <c>dynamic</c>, the same
    /// late-bound escape hatch <c>Poc.WpsComInteropSpike</c> already uses for its own
    /// never-verified-against-a-real-install COM surface. A <c>dynamic</c> member access that
    /// doesn't actually exist on the underlying type throws
    /// <see cref="Microsoft.CSharp.RuntimeBinder.RuntimeBinderException"/> at the call site, at
    /// runtime — caught below like everything else this method can fail on — instead of the whole
    /// project refusing to build over one guessed member name. Returns null on ANY failure (wrong
    /// method name, wrong parameter shape, wrong Variant accessor, or a genuine "this source
    /// doesn't know its own duration") — every caller already treats null as "no fade-out for this
    /// file".</summary>
    public TimeSpan? TryGetDuration()
    {
        try
        {
            dynamic reader = _reader;
            dynamic variant = reader.GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, MF_PD_DURATION);
            long ticks = ExtractDurationTicks(variant);
            // MF_PD_DURATION (mfidl.h) is documented as 100ns units — the exact same unit
            // TimeSpan.Ticks uses — so a successful read needs no unit conversion beyond the
            // accessor guess above and this final widen-to-long.
            return ticks > 0 ? TimeSpan.FromTicks(ticks) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Vortice.MediaFoundation's exact PROPVARIANT-wrapper shape for a VT_UI8 result has
    /// never been checked against a real installed package in this sandbox (see
    /// <see cref="TryGetDuration"/>'s own doc comment) — tries several plausible accessor shapes in
    /// turn, via <c>dynamic</c> so a wrong guess throws (caught, tries the next one) instead of
    /// failing to compile. Each candidate matches a shape seen across different .NET COM/PROPVARIANT
    /// wrapper APIs; throws if none of them work, which <see cref="TryGetDuration"/>'s own
    /// catch-all then turns into a null return the same as any other failure.</summary>
    private static long ExtractDurationTicks(dynamic variant)
    {
        try { return checked((long)variant.Value); } catch { }
        try { return checked((long)variant.UInt64); } catch { }
        try { return checked((long)(ulong)variant); } catch { }
        throw new InvalidOperationException("Unable to extract a UInt64 value from the Variant returned by GetPresentationAttribute.");
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
