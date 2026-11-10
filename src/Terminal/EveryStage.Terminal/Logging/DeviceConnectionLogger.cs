namespace EveryStage.Terminal.Logging;

/// <summary>
/// 设备连接记录 (PLANNING.md §14.4): "配对/取消配对、连接建立/断开时间、连接质量指标(丢包率/延迟)、
/// 断开原因" — for diagnosing network problems with Casters, separate from what content played
/// (<see cref="PlaybackLogger"/>) or library edits (<see cref="FileOperationLogger"/>).
///
/// <see cref="LogQualityMetric"/> went uncalled for a long time after this class was first written
/// (device discovery/pairing hadn't been built yet) — see this project's README for when a real
/// caller finally showed up (<c>Program.cs</c>'s <c>LogConnectionQualityAsync</c>, once both a real
/// RTT measurement and a real packet-loss estimate existed to feed it).
/// </summary>
public sealed class DeviceConnectionLogger
{
    private readonly DailyRollingLogWriter _writer;

    public DeviceConnectionLogger(string? logRootOverride = null)
    {
        _writer = new DailyRollingLogWriter(
            Path.Combine(logRootOverride ?? LogPaths.DefaultRoot, "device-connection"), "device");
    }

    public void LogPaired(string deviceId, string deviceName) =>
        _writer.Write("device_paired", new { deviceId, deviceName });

    /// <summary>Went uncalled from this class's first round until <c>DevicesPanel</c>'s "移除配对"
    /// button existed to call it — see this project's README "已知风险" (the same "existed since an
    /// early logging round, no caller until the round that built the UI action it corresponds to"
    /// shape as <see cref="LogQualityMetric"/>'s own history above).</summary>
    public void LogUnpaired(string deviceId) =>
        _writer.Write("device_unpaired", new { deviceId });

    public void LogConnected(string deviceId) =>
        _writer.Write("device_connected", new { deviceId });

    public void LogDisconnected(string deviceId, string reason) =>
        _writer.Write("device_disconnected", new { deviceId, reason });

    /// <summary>Either figure may be null for a given call — a missed ping shouldn't suppress a
    /// perfectly good local packet-loss reading gathered the same round, and vice versa. Nullable
    /// rather than defaulting a missing measurement to some sentinel like 0: a 0 in a persisted log
    /// would silently read as "measured, found perfect" instead of "not measured this round", which
    /// is a worse lie for a diagnostic log to tell than an honest null.</summary>
    public void LogQualityMetric(string deviceId, double? packetLossPercent, double? latencyMs) =>
        _writer.Write("device_quality", new { deviceId, packetLossPercent, latencyMs });

    /// <summary>A cast lost its audio mid-way (output device removed, persistent audio failure) and
    /// continues as video only.</summary>
    public void LogCastAudioLost(string deviceId, string reason) =>
        _writer.Write("cast_audio_lost", new { deviceId, reason });

    internal void LogCastFrames(string deviceId, string reason, EveryStage.Terminal.Receiving.CastFrameStatistics stats) =>
        _writer.Write("cast_video_frames", new { deviceId, reason, stats.Decoded, stats.ProgramSubmitted, stats.ProgramSkipped,
            stats.PreviewSubmitted, stats.PreviewSkipped, stats.QueueOverflow, stats.AudioLateDropped });

    /// <summary>An authenticated control message was refused as a replay or as too old / from the future
    /// (the two devices' clocks more than the allowed skew apart).</summary>
    /// <summary>One step of a 开始投屏 request: received, refused (with why), 「准备中」 sent, or answered
    /// (ready or the error the Caster was given). Without these, a Caster that reported 「终端机没有回应」
    /// left nothing on the Terminal to say whether the request even arrived.</summary>
    public void LogCastStart(string deviceId, string outcome, string? detail = null) =>
        _writer.Write("cast_start", new { deviceId, outcome, detail });

    /// <summary>A pairing confirmation was put in front of the operator.</summary>
    public void LogPairingPrompt(string deviceId, string deviceName) =>
        _writer.Write("pairing_prompt_shown", new { deviceId, deviceName });

    public void LogMessageRejected(string deviceId, string messageKind, string reason) =>
        _writer.Write("message_rejected", new { deviceId, messageKind, reason });
}
