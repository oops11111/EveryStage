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
}
