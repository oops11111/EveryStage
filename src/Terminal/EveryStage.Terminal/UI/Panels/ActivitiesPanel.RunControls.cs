using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI.Panels;

public sealed partial class ActivitiesPanel
{
    private TreeNode? FindNode(object tag)
    {
        foreach (TreeNode activityNode in _tree.Nodes)
        {
            if (activityNode.Tag == tag) return activityNode;
            foreach (TreeNode fileNode in activityNode.Nodes)
                if (fileNode.Tag == tag) return fileNode;
        }
        return null;
    }

    /// <summary>The engine whose current item is 「大屏播放中」: Program when bound, else Preview.</summary>
    private PlaybackEngine? LiveEngine => ProgramEngine?.Invoke() ?? _playback;

    /// <summary>Every runnable item of the current scenario in run order (background music is
    /// layered under the show and never becomes 「下一项」).</summary>
    private List<(Activity Activity, int Index)> RunOrder()
    {
        var order = new List<(Activity, int)>();
        if (CurrentScenario is not { } scenario) return order;
        foreach (var activity in scenario.Activities)
            for (int i = 0; i < activity.Files.Count; i++)
                if (!activity.Files[i].IsBackgroundAudio) order.Add((activity, i));
        return order;
    }

    private static int? FirstRunnable(Activity activity, int from)
    {
        for (int i = from; i < activity.Files.Count; i++)
            if (!activity.Files[i].IsBackgroundAudio) return i;
        return null;
    }

    /// <summary>Position of the live item in <see cref="RunOrder"/>, or -1.</summary>
    private int LivePosition(List<(Activity Activity, int Index)> order)
    {
        var live = LiveEngine is { State: not PlaybackChannelState.Idle } engine ? engine.CurrentFile : null;
        if (live == null) return -1;
        return order.FindIndex(o => ReferenceEquals(o.Activity.Files[o.Index], live));
    }

    /// <summary>「下一项」: the item after the live one — only within the live section (环节): manual
    /// 下一项 never crosses into the next section by itself. At the end of a section it is -1 (button
    /// greyed) until the operator explicitly selects an item or section elsewhere, which is then next.
    /// With nothing live: the selected item (or the first of the selected section), else the first item.
    /// (Automatic continuation across sections is the engine's own, separate behaviour.)</summary>
    private int NextPosition(List<(Activity Activity, int Index)> order)
    {
        int live = LivePosition(order);
        if (live >= 0)
        {
            if (live + 1 < order.Count && ReferenceEquals(order[live + 1].Activity, order[live].Activity)) return live + 1;
            int chosen = SelectedPosition(order);
            return chosen >= 0 && !ReferenceEquals(order[chosen].Activity, order[live].Activity) ? chosen : -1;
        }
        return SelectedPosition(order) is var selectedPosition and >= 0 ? selectedPosition : order.Count > 0 ? 0 : -1;
    }

    /// <summary>The selected file's position, or the first item of the selected section; -1 if none.</summary>
    private int SelectedPosition(List<(Activity Activity, int Index)> order)
    {
        if (_tree.SelectedNode?.Tag is MediaFile file)
        {
            int selected = order.FindIndex(o => ReferenceEquals(o.Activity.Files[o.Index], file));
            if (selected >= 0) return selected;
        }
        if (_tree.SelectedNode?.Tag is Activity activity)
        {
            int first = order.FindIndex(o => ReferenceEquals(o.Activity, activity));
            if (first >= 0) return first;
        }
        return -1;
    }

    RunRowState IActivityRowHost.StateOf(MediaFile file)
    {
        var order = RunOrder();
        int index = order.FindIndex(o => ReferenceEquals(o.Activity.Files[o.Index], file));
        if (index < 0) return RunRowState.Pending;
        int live = LivePosition(order);
        if (index == live) return ProgramIsOnAir(file) ? RunRowState.Live : RunRowState.Pending;
        if (index == NextPosition(order)) return RunRowState.Next;
        return live >= 0 && index < live ? RunRowState.Done : RunRowState.Pending;
    }

    MediaFile? IActivityRowHost.PreviewFile => _playback is { State: not PlaybackChannelState.Idle } preview ? preview.CurrentFile : null;

    private bool ProgramIsOnAir(MediaFile file) => _stateMachine.State == OutputState.Active
        && _stateMachine.Source == ProgramSource.LocalMedia && ReferenceEquals(ProgramEngine?.Invoke()?.CurrentFile, file);

    (FilesPanel.CardProgress Progress, bool OnProgram)? IActivityRowHost.ProgressFor(MediaFile file)
    {
        if (ProgramIsOnAir(file) && FilesPanel.CardPlaybackProgress(file, ProgramEngine?.Invoke()) is { } program)
            return (program, true);
        if (ReferenceEquals(_playback?.CurrentFile, file) && FilesPanel.CardPlaybackProgress(file, _playback) is { } preview)
            return (preview, false);
        return null;
    }

    /// <summary>设置 · 热键 下一项 / 上一项 (MainWindow), same as the run-sheet buttons.</summary>
    internal void RunNext() => GoNext();
    internal void RunPrevious() => GoPrevious();

    /// <summary>「下一项 ▶」 / space: put the next item on the big screen.</summary>
    private void GoNext()
    {
        var order = RunOrder();
        int next = NextPosition(order);
        if (next < 0) return;
        RunRequested?.Invoke(order[next].Activity, order[next].Index);
    }

    /// <summary>「◀ 上一项」: back to the item before the live one, within the live section.</summary>
    private void GoPrevious()
    {
        var order = RunOrder();
        int live = LivePosition(order);
        if (live <= 0 || !ReferenceEquals(order[live - 1].Activity, order[live].Activity)) return; // stays in the section
        RunRequested?.Invoke(order[live - 1].Activity, order[live - 1].Index);
    }

    /// <summary>「预览下一项」: load the next item in the Preview monitor only.</summary>
    private void PreviewNext()
    {
        var order = RunOrder();
        int next = NextPosition(order);
        if (next < 0 || _playback == null) return;
        _playback.RequestPlay(order[next].Activity, order[next].Index, PlaybackTrigger.ManualSkip);
    }

    private void UpdateRunControls()
    {
        var order = RunOrder();
        int next = NextPosition(order), live = LivePosition(order);
        var scenario = CurrentScenario;
        string NumberOf((Activity Activity, int Index) item) =>
            $"{(scenario?.Activities.IndexOf(item.Activity) ?? 0) + 1}.{item.Index + 1}";
        bool sectionEnd = next < 0 && live >= 0;
        _nextName.Text = next < 0
            ? (sectionEnd
                ? (live + 1 < order.Count ? "本环节已结束 · 选中下一个环节后按「下一项」进入" : "已是最后一项")
                : "流程里还没有可播放的文件")
            : $"{NumberOf(order[next])}  {Path.GetFileName(order[next].Activity.Files[order[next].Index].SourcePath)}";
        ModernUi.SetForegroundRole(_nextName, next < 0 ? UiForegroundRole.Muted : UiForegroundRole.Text);
        _runNextButton.Enabled = next >= 0;
        _runPreviewButton.Enabled = next >= 0 && _playback != null;
        _runPreviousButton.Enabled = live > 0 && ReferenceEquals(order[live - 1].Activity, order[live].Activity);
    }

    // Space is the global 播放 / 暂停 (MainWindow, 第二十一轮); 下一项 is the button or Ctrl+→ only.

    bool IActivityRowHost.IsSelected(TreeNode node) => ReferenceEquals(_tree.SelectedNode, node);
    bool IActivityRowHost.IsMultiSelected(TreeNode node) => _multiSelectedFileNodes.Contains(node);
    void IActivityRowHost.RowNodeClicked(TreeNode node) => OnCardNodeClick(node);
    void IActivityRowHost.RowNodeDoubleClicked(TreeNode node) => OnCardNodeDoubleClick(node);

    void IActivityRowHost.RowContextMenu(TreeNode node, Control source, Point location)
    {
        // Right-clicking one file of an existing Ctrl+click multi-selection keeps that selection, so
        // 统一设置属性... is reachable from the context menu as well as from 编辑 ▾.
        if (!_multiSelectedFileNodes.Contains(node)) OnCardNodeClick(node);
        ShowEditMenu(source, location);
    }

    /// <summary>The one place the shared <see cref="_editMenu"/> is shown from — a row's
    /// right-click, the header's 「编辑 ▾」 button and the Apps / Shift+F10 keyboard path all
    /// land here, so every entry point gets the same menu instance and the same
    /// UpdateButtonStates refresh (via the menu's own Opening handler).</summary>
    private void ShowEditMenu(Control source, Point location) => _editMenu.Show(source, location);

    /// <summary>AP-08's keyboard path: opens the edit menu anchored at the selected
    /// selection's row (its header's left edge, below where the ⋮ handle would be), or at the
    /// panel's own top-left when the selection's row can't be found.</summary>
    private void ShowEditMenuForSelection()
    {
        if (_tree.SelectedNode is not { } node) return;
        var activity = node.Tag as Activity ?? node.Parent?.Tag as Activity;
        if (activity != null)
        {
            foreach (var row in _activityRows.Controls.OfType<ActivityRowView>())
            {
                if (row.Activity != activity) continue;
                ShowEditMenu(_activityRows, new Point(
                    row.Left + LogicalToDeviceUnits(28), row.Top + LogicalToDeviceUnits(50)));
                return;
            }
        }
        ShowEditMenu(this, new Point(LogicalToDeviceUnits(16), LogicalToDeviceUnits(64)));
    }

    void IActivityRowHost.ToggleCollapsed(Activity activity)
    {
        // Frozen while a filter is up (AP-06): sections are presented expanded there, and a
        // toggle would persist an IsCollapsed change the operator can't even see happen.
        if (IsFiltering) return;
        // Deferred: this runs inside the row's own MouseDown, and RefreshTree disposes that row.
        BeginInvoke(() =>
        {
            activity.IsCollapsed = !activity.IsCollapsed;
            if (!TrySave()) return;
            RefreshTree();
        });
    }

    Image? IActivityRowHost.ThumbnailFor(MediaFile file)
    {
        string key = $"{file.Kind}|{file.SourcePath}";
        if (_thumbnails.TryGetValue(key, out var cached)) return cached;
        if (_thumbnailsRequested.Add(key)) _ = LoadThumbnailAsync(file, key, _thumbnailGeneration);
        return null;
    }
}
