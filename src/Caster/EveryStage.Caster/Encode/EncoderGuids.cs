namespace EveryStage.Caster.Encode;

/// <summary>
/// Media Foundation GUIDs for H.264 hardware encoding, spelled out as raw literals from mfapi.h /
/// mfobjects.h / codecapi.h rather than guessed Vortice.MediaFoundation constant names — same
/// reasoning as <c>EveryStage.Rendering.Decode.WellKnownGuids</c>, which this mirrors: a wrong
/// literal here is far easier to catch by diffing against the Windows SDK headers than a wrong
/// member name is to catch by reading this file alone, and this project has never been compiled.
/// </summary>
internal static class EncoderGuids
{
    // --- MF_MT_* media type attribute keys (mfapi.h) ---
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");

    // --- Major types / subtypes ---
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");

    // --- MFT category / async-unlock (mfobjects.h / mftransform.h) ---
    // The "video encoder" MFT category GUID used with MFTEnumEx to find candidate transforms.
    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    // Attribute set (to UINT32 1) on an async MFT's own attribute store before use, per the
    // Windows 8+ MFT async-unlock requirement — without this, ProcessInput/ProcessOutput on an
    // async MFT fail with MF_E_TRANSFORM_ASYNC_LOCKED.
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb3-8daf-32ecd6e15d96");
    public static readonly Guid MF_TRANSFORM_ASYNC = new("f81f7434-462f-4a4a-8d0c-2be166e2ac9a");

    // --- CodecAPI properties (codecapi.h) used for zero-latency low-delay real-time encoding
    // (PLANNING.md §4.2 "禁用B帧，零延迟预设，CBR码率控制，短GOP") ---
    // Lower confidence than the mfapi.h/mfobjects.h GUIDs above: these are reconstructed from
    // memory of commonly-referenced MF encoding sample code rather than a header file just read,
    // and codecapi.h has many similarly-named properties that are easy to misremember a hex digit
    // of. If SetValue() on any of these throws or silently has no effect, treat the GUID itself as
    // the first suspect, not the calling code around it.
    public static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CODECAPI_AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b2227a7444ff");
    public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9394-8ee2461e1fc4");
    public static readonly Guid CODECAPI_AVEncVideoTemporalLayerCount = new("19caebff-e738-4faf-a1d0-3910305ff052");
    public static readonly Guid CODECAPI_AVEncH264CABACEnable = new("ee6cad62-d305-4248-a5e4-7e5f18f3b0f3");
}
