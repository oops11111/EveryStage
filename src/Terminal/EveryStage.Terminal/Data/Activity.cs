namespace EveryStage.Terminal.Data;

/// <summary>
/// One item in a <see cref="Scenario"/>'s activity list — draggable/reorderable, collapsible
/// (PLANNING.md §6, §11 拖拽添加).
///
/// This class's own doc comment used to claim order within <see cref="Scenario.Activities"/> is
/// "the authoritative play order for <see cref="PlayMode.SequentialAuto"/>" — that overstated what
/// actually happened at the time: PLANNING.md §6 only describes 顺序自动/手动点选 as an activity-level
/// default governing progression through THAT activity's own <see cref="Files"/> list, and back then
/// <see cref="EveryStage.Terminal.Playback.PlaybackEngine.TryAdvance"/> was structurally scoped to
/// <c>_currentActivity.Files</c> alone, holding on the last item rather than crossing into the next
/// activity when one ran out.
///
/// **【后续更新，见Terminal README风险第118条】** This order now IS meaningful for automatic playback
/// progression too, confirmed as a real product decision with the user rather than assumed:
/// <see cref="EveryStage.Terminal.Playback.PlaybackEngine.TryAdvanceToNextActivity"/> auto-advances
/// into the NEXT activity in this list once <c>TryAdvance</c> finds nothing further left in the
/// current one — but only for automatic completion-based advance, never a manual 上一项/下一项 click,
/// and never wrapping back to this list's first activity once the last one is reached. See that
/// method's own doc comment for the full reasoning.
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
