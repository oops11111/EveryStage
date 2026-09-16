namespace EveryStage.Terminal.Data;

/// <summary>
/// One item in a <see cref="Scenario"/>'s activity list — draggable/reorderable, collapsible
/// (PLANNING.md §6, §11 拖拽添加).
///
/// This class's own doc comment used to claim order within <see cref="Scenario.Activities"/> is
/// "the authoritative play order for <see cref="PlayMode.SequentialAuto"/>" — that overstated what
/// actually happens: PLANNING.md §6 only describes 顺序自动/手动点选 as an activity-level default
/// governing progression through THAT activity's own <see cref="Files"/> list, and
/// <see cref="EveryStage.Terminal.Playback.PlaybackEngine.TryAdvance"/> is structurally scoped to
/// <c>_currentActivity.Files</c> — it never crosses into a different <see cref="Activity"/> when
/// one runs out. Reaching the end of an activity's file list under <see cref="PlayMode.SequentialAuto"/>
/// holds on the last item rather than auto-advancing into the next activity in this list; this
/// order only matters for how activities are displayed/manually reordered in the UI, not for any
/// automatic playback progression across activities. See this project's README "尚未开始" for
/// whether cross-activity auto-advance is a real product gap worth building.
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
