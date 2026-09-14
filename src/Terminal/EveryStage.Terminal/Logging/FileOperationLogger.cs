namespace EveryStage.Terminal.Logging;

/// <summary>
/// 文件操作日志 (PLANNING.md §14.4): "本地文件导入/删除、方案/活动的创建/修改/删除、播放属性变更" —
/// for tracing back accidental content edits, not for playback behavior (see
/// <see cref="PlaybackLogger"/>) or device connectivity (see <see cref="DeviceConnectionLogger"/>).
/// </summary>
public sealed class FileOperationLogger
{
    private readonly DailyRollingLogWriter _writer;

    public FileOperationLogger(string? logRootOverride = null)
    {
        _writer = new DailyRollingLogWriter(
            Path.Combine(logRootOverride ?? LogPaths.DefaultRoot, "file-operations"), "file-ops");
    }

    public void LogFileImported(Guid mediaFileId, string sourcePath) =>
        _writer.Write("file_imported", new { mediaFileId, sourcePath });

    public void LogFileRemoved(Guid mediaFileId, string sourcePath) =>
        _writer.Write("file_removed", new { mediaFileId, sourcePath });

    public void LogScenarioCreated(Guid scenarioId, string name) =>
        _writer.Write("scenario_created", new { scenarioId, name });

    public void LogScenarioDeleted(Guid scenarioId, string name) =>
        _writer.Write("scenario_deleted", new { scenarioId, name });

    public void LogActivityCreated(Guid scenarioId, Guid activityId, string name) =>
        _writer.Write("activity_created", new { scenarioId, activityId, name });

    public void LogActivityModified(Guid scenarioId, Guid activityId, string name) =>
        _writer.Write("activity_modified", new { scenarioId, activityId, name });

    public void LogActivityDeleted(Guid scenarioId, Guid activityId, string name) =>
        _writer.Write("activity_deleted", new { scenarioId, activityId, name });

    public void LogPlaybackPropertyChanged(Guid mediaFileId, string propertyName, string? oldValue, string? newValue) =>
        _writer.Write("playback_property_changed", new { mediaFileId, propertyName, oldValue, newValue });
}
