namespace EveryStage.Rendering.Decode;

/// <summary>
/// Media Foundation GUIDs used by <see cref="VideoDecodeSource"/>, spelled out as raw literals
/// (from mfapi.h / mfreadwrite.h / mfobjects.h) rather than guessed Vortice.MediaFoundation
/// constant names, since this project has never been compiled in this sandbox (Linux, no
/// Windows SDK/GPU available) and a wrong literal here is far easier to catch by diffing against
/// the Windows SDK headers than a wrong member name is to catch by reading this file alone.
///
/// If Vortice.MediaFoundation exposes typed equivalents (e.g. a `MediaTypeGuids` /
/// `SourceReaderAttributeKeys` class), prefer swapping to those once verified on a real Windows
/// build — it's more readable and self-documenting than raw GUID fields.
/// </summary>
internal static class WellKnownGuids
{
    // --- MF_MT_* media type attribute keys (mfapi.h) ---
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");

    // --- Major types ---
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");

    // --- Subtypes ---
    // NV12: requested explicitly so the DXVA decoder's native output format passes straight
    // through as a D3D11 texture instead of an internal color-convert/software path kicking in.
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");
    // Same wFormatTag-derived pattern as MFAudioFormat_PCM above — AAC's registered tag is
    // WAVE_FORMAT_MPEG_HEAAC (0x1610). Independently duplicated from
    // EveryStage.Caster.Encode.EncoderGuids's identical constant rather than shared/referenced —
    // same "each file keeps its own independently-verifiable copy" convention this repo already
    // applies to every raw-GUID file (see AacAudioDecoder's own doc comment).
    public static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00aa00389b71");

    // --- MF_MT_AUDIO_* attribute keys (mfapi.h) — used by AacAudioDecoder, mirroring
    // EveryStage.Caster.Encode.EncoderGuids's identical set for AacAudioEncoder.
    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    // Same "ADTS, self-describing per frame" choice AacAudioEncoder makes on the output side (1 =
    // ADTS) — see that class's and EncoderGuids' doc comments.
    public static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17491");

    // --- IMFSourceReader / IMFReadWriteClassFactory creation attributes (mfreadwrite.h) ---
    // Binds the source reader's internal DXVA decoder to our D3D11 device via IMFDXGIDeviceManager
    // so decoded frames land in a texture our renderer can consume without a CPU round-trip.
    public static readonly Guid MF_SOURCE_READER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");

    // Without this, the source reader silently prefers software decoders/transforms even when a
    // D3D manager is attached.
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");

    // --- Well-known stream index sentinels (mfreadwrite.h) ---
    public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = unchecked((uint)-4);
    public const uint MF_SOURCE_READER_FIRST_AUDIO_STREAM = unchecked((uint)-3);
    public const uint MF_SOURCE_READER_ANY_STREAM = unchecked((uint)-2);
    // Continues the same enumeration the three sentinels above belong to (mfreadwrite.h defines
    // all four together) — "ask about the underlying IMFMediaSource itself" rather than any one
    // stream, which is what a presentation-level attribute like MF_PD_DURATION below needs.
    public const uint MF_SOURCE_READER_MEDIASOURCE = unchecked((uint)-1);

    // --- MF_PD_* presentation descriptor attribute keys (mfidl.h) — used by
    // AudioDecodeSource.TryGetDuration/VideoDecodeSource.TryGetDuration. Recalled from memory, NOT
    // independently re-verified against a real Windows SDK header in this sandbox — see this
    // class's own doc comment on that general caveat, and TryGetDuration's own doc comment on why
    // this specific call goes through `dynamic` rather than a direct typed call the way every other
    // GUID in this file is used: unlike those, this one has never had anything in this codebase
    // exercise it even once, so there's nothing here to fall back on if the raw value itself turns
    // out wrong beyond "GetPresentationAttribute returns some other, equally wrong duration" —
    // exactly the same failure mode as any other bit-for-bit-wrong GUID, not a new risk category.
    public static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd8d2");

    // --- MFT category (mfobjects.h) — used by AacAudioDecoder to find the built-in AAC decoder MFT,
    // mirroring EveryStage.Caster.Encode.EncoderGuids.MFT_CATEGORY_AUDIO_ENCODER's identical role
    // for the encode direction.
    public static readonly Guid MFT_CATEGORY_AUDIO_DECODER = new("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");

    // Attribute set (to UINT32 1) on an async MFT's own attribute store before use — same role as
    // EveryStage.Caster.Encode.EncoderGuids.MF_TRANSFORM_ASYNC_UNLOCK, duplicated here rather than
    // shared for the same independent-verification reasoning as every other GUID in this file.
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb3-8daf-32ecd6e15d96");
}
