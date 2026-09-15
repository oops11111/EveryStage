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
}
