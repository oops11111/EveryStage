namespace EveryStage.Terminal.Data;

/// <summary>
/// One item in a <see cref="Scenario"/>'s activity list — draggable/reorderable, collapsible
/// (PLANNING.md §6, §11 拖拽添加). Order within <see cref="Scenario.Activities"/> is the
/// authoritative play order for <see cref="PlayMode.SequentialAuto"/>.
/// </summary>
public sealed class Activity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public bool IsCollapsed { get; set; }

    /// <summary>Activity-level default; individual files may override via <see cref="MediaFile.PlayModeOverride"/>.</summary>
    public PlayMode DefaultPlayMode { get; set; } = PlayMode.SequentialAuto;

    public List<MediaFile> Files { get; init; } = new();
}
