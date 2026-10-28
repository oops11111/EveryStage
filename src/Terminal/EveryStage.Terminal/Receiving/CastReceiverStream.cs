using System.Net;
using EveryStage.Terminal.Display;

namespace EveryStage.Terminal.Receiving;

internal sealed class CastReceiverStream : IDeviceCastStream
{
    private readonly CastReceiver _receiver;

    public CastReceiverStream(
        VideoSurface surface,
        int width,
        int height,
        int videoPort,
        bool hasAudio,
        int audioSampleRate,
        int audioChannels,
        int audioPort,
        bool audioIsAac,
        byte payloadType,
        byte audioPayloadType,
        Guid mediaSessionId,
        byte[] videoKey,
        byte[] audioKey,
        IPAddress allowedSender)
    {
        _receiver = new CastReceiver(surface, width, height, videoPort, hasAudio, audioSampleRate, audioChannels, audioPort,
            audioIsAac, payloadType, audioPayloadType, mediaSessionId, videoKey, audioKey, allowedSender);
        _receiver.VideoPacketsMissing += missing => VideoPacketsMissing?.Invoke(missing);
        _receiver.AudioDegraded += reason => AudioDegraded?.Invoke(reason);
    }

    public event Action<IReadOnlyList<ushort>>? VideoPacketsMissing;
    public event Action<string>? AudioDegraded;

    public bool AudioMuted { get => _receiver.AudioMuted; set => _receiver.AudioMuted = value; }
    public DateTime LastPacketReceivedAt => _receiver.LastPacketReceivedAt;
    public int ConsecutiveVideoDecodeErrors => _receiver.ConsecutiveVideoDecodeErrors;
    public bool PresentLoopFailed => _receiver.PresentLoopFailed;
    public bool DecodeLoopFailed => _receiver.DecodeLoopFailed;
    public long FramesDecoded => _receiver.FramesDecoded;
    public long BytesReceived => _receiver.BytesReceived;
    public string? LastError => _receiver.LastError;
    public bool HasAudio => _receiver.HasAudio;
    public long AudioBytesReceived => _receiver.AudioBytesReceived;
    public string? AudioError => _receiver.AudioError;
    public long PayloadTypeMismatches => _receiver.PayloadTypeMismatches;
    public double? EstimatedPacketLossPercent => _receiver.EstimatedPacketLossPercent;
    public long VideoPacketsLost => _receiver.VideoPacketsLost;

    public void Start() => _receiver.Start();
    public void AttachProgram(VideoSurface surface) => _receiver.AttachProgram(surface);
    public void DetachProgram() => _receiver.DetachProgram();
    public void Dispose() => _receiver.Dispose();
}
