namespace EveryStage.Terminal.Receiving;

/// <summary>
/// Media Foundation GUIDs for driving an H.264 decoder MFT directly, spelled out as raw literals
/// from mfapi.h / mfobjects.h — same reasoning, and largely the same literals, as
/// <c>EveryStage.Caster.Encode.EncoderGuids</c> and <c>EveryStage.Rendering.Decode.WellKnownGuids</c>,
/// which this mirrors rather than references: those two are (respectively) Caster-internal and
/// Terminal already depends on but at `internal` visibility, so duplicating the handful of literals
/// actually needed here was simpler than threading a new shared visibility surface through either
/// project for what is, in each case, only a handful of universal Media Foundation constants. A
/// wrong literal here is far easier to catch by diffing against the Windows SDK headers than a
///
/// VERIFIED against the Windows SDK headers on a real Windows machine (Windows Kits 10,
/// 10.0.18362.0, um/mfapi.h and um/mftransform.h), which is how three literals in this family were
/// caught being wrong: MF_TRANSFORM_ASYNC and MF_TRANSFORM_ASYNC_UNLOCK (both also wrong in
/// EveryStage.Caster.Encode.EncoderGuids and, for the unlock, EveryStage.Rendering.Decode.
/// WellKnownGuids, where they had been driving every async-MFT unlock in this repo) and
/// MF_SA_MINIMUM_OUTPUT_SAMPLE_COUNT. Do not re-derive any of these from memory; copy them from a
/// header.
/// </summary>
internal static class DecoderGuids
{
    // --- MF_MT_* media type attribute keys (mfapi.h) ---
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");

    // --- Major types / subtypes ---
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");

    // --- MFT category (mfobjects.h / mftransform.h) ---
    // The "video decoder" MFT category GUID used with MFTEnumEx to find candidate transforms.
    // Lower confidence than the block above: reconstructed from memory rather than a header file
    // just read, same caveat EncoderGuids.cs already flags for its own CODECAPI_* GUIDs — if
    // MFTEnumEx returns nothing when a hardware H.264 decoder is known to exist, treat this literal
    // as the first suspect.
    public static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");

    // --- Async MFT protocol (mftransform.h) ---
    // Every hardware MFT (anything MFTEnumEx returns under MFT_ENUM_FLAG_HARDWARE) is an
    // asynchronous MFT, and starts out locked: calls fail with MF_E_TRANSFORM_ASYNC_LOCKED until
    // MF_TRANSFORM_ASYNC_UNLOCK is set on it. Same literals as
    // EveryStage.Caster.Encode.EncoderGuids and EveryStage.Rendering.Decode.WellKnownGuids, which
    // already carry them for the encode and audio-decode sides.
    public static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");

    // --- D3D11 and DXVA support attributes (mfapi.h) ---
    // MF_SA_D3D11_AWARE lives on the MFT itself and must read TRUE before it is legal to hand the
    // transform a DXGI device manager. The other two live on the MFT OUTPUT STREAM attributes and
    // are written by the client to steer the decoder surface pool the MFT allocates.
    public static readonly Guid MF_SA_D3D11_AWARE = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");
    public static readonly Guid MF_SA_D3D11_BINDFLAGS = new("eacf97ad-065c-4408-bee3-fdcbfd128be2");
    public static readonly Guid MF_SA_MINIMUM_OUTPUT_SAMPLE_COUNT = new("851745d5-c3d6-476d-9527-498ef2d10d18");
}
