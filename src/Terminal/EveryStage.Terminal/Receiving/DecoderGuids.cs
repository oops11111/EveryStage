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
/// wrong member name is to catch by reading this file alone, and this project has never been
/// compiled.
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
}
