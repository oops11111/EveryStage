namespace EveryStage.Terminal.Logging;

/// <summary>
/// 设备连接记录 (PLANNING.md §14.4): "配对/取消配对、连接建立/断开时间、连接质量指标(丢包率/延迟)、
/// 断开原因" — for diagnosing network problems with Casters, separate from what content played
/// (<see cref="PlaybackLogger"/>) or library edits (<see cref="FileOperationLogger"/>).
///
/// Nothing calls this yet: device discovery/pairing (PLANNING.md §7, Phase 3) hasn't been built.
/// This exists now so that work has a logging surface ready to call into from day one, rather than
/// bolting logging on after the fact.
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

    public void LogQualityMetric(string deviceId, double packetLossPercent, double latencyMs) =>
        _writer.Write("device_quality", new { deviceId, packetLossPercent, latencyMs });
}
