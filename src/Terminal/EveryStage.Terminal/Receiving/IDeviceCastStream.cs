using EveryStage.Terminal.Display;

namespace EveryStage.Terminal.Receiving;

/// <summary>Counts decoded frames and presentation submissions, not physical screen refreshes.
/// <c>KeyframeRequests</c> is the cumulative count of keyframe requests this stream's decoder has
/// raised (access units dropped under backpressure, or decode errors — P-25); it rides along here
/// so <c>DeviceCastController</c> can forward it in the periodic CastStatus report without growing
/// a second statistics channel. It defaults to 0 so test doubles need not report it.</summary>
internal readonly record struct CastFrameStatistics(long Decoded, long ProgramSubmitted, long ProgramSkipped,
    long PreviewSubmitted, long PreviewSkipped, long QueueOverflow, long AudioLateDropped,
    long KeyframeRequests = 0);

internal interface IDeviceCastStream : IDisposable
{
    event Action<IReadOnlyList<ushort>> VideoPacketsMissing;
    event Action<string> AudioDegraded;

    bool AudioMuted { get; set; }
    DateTime LastPacketReceivedAt { get; }
    int ConsecutiveVideoDecodeErrors { get; }
    bool PresentLoopFailed { get; }
    bool DecodeLoopFailed { get; }
    long FramesDecoded { get; }
    CastFrameStatistics FrameStatistics { get; }
    long BytesReceived { get; }
    string? LastError { get; }
    bool HasAudio { get; }
    long AudioBytesReceived { get; }
    string? AudioError { get; }
    long PayloadTypeMismatches { get; }
    double? EstimatedPacketLossPercent { get; }
    long VideoPacketsLost { get; }

    void Start();
    void RequestDefaultAudioMigration();
    void AttachProgram(VideoSurface surface);
    void DetachProgram();
}
