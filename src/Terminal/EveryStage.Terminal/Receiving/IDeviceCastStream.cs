using EveryStage.Terminal.Display;

namespace EveryStage.Terminal.Receiving;

/// <summary>Counts decoded frames and presentation submissions, not physical screen refreshes.</summary>
internal readonly record struct CastFrameStatistics(long Decoded, long ProgramSubmitted, long ProgramSkipped,
    long PreviewSubmitted, long PreviewSkipped, long QueueOverflow, long AudioLateDropped);

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
