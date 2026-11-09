using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI.Panels;

public sealed partial class ActivitiesPanel
{
    // --- 开演前检查 ---

    private readonly Dictionary<MediaFile, PreShowProblem> _preShowProblems = new(ReferenceEqualityComparer.Instance);
    private Guid? _preShowCheckedScenario;
    private int _preShowGeneration;
    private Form? _preShowWindow;

    /// <summary>Runs 开演前检查 for the current scenario in the background: rows get their marks, and the
    /// non-modal list opens when something was found (always when the operator pressed the button).</summary>
    private async void RunPreShowCheck(bool requestedByOperator)
    {
        if (CurrentScenario is not { } scenario) return;
        int generation = ++_preShowGeneration;
        _preShowCheckedScenario = scenario.Id;
        IReadOnlyList<PreShowFinding> findings;
        try { findings = await Task.Run(() => PreShowCheck.Run(scenario)); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Pre-show check failed: {0}", ex); return; }
        if (IsDisposed || generation != _preShowGeneration) return;
        _preShowProblems.Clear();
        foreach (var finding in findings) _preShowProblems[finding.File] = finding.Problem;
        foreach (Control row in _activityRows.Controls) row.Invalidate();
        int total = scenario.Activities.Sum(a => a.Files.Count);
        if (findings.Count > 0 || requestedByOperator) ShowPreShowResult(scenario, findings, total);
    }

    private void ShowPreShowResult(Scenario scenario, IReadOnlyList<PreShowFinding> findings, int total)
    {
        _preShowWindow?.Close();
        var window = new Form
        {
            Text = $"开演前检查 · {scenario.Name}", FormBorderStyle = FormBorderStyle.SizableToolWindow, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(560, findings.Count == 0 ? 150 : 340),
            MinimumSize = new Size(420, 150),
        };
        var summary = new Label
        {
            Dock = DockStyle.Top, Height = 56, Padding = new Padding(12, 10, 12, 0),
            Text = (findings.Count == 0 ? $"全部 {total} 个文件都存在、可读取且格式支持。" : $"{total} 个文件中有 {findings.Count} 个需要处理：") + "\n" + PreShowCheck.Scope,
        };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.None };
        list.Columns.Add("位置", 70);
        list.Columns.Add("文件", 220);
        list.Columns.Add("问题", 240);
        foreach (var finding in findings)
        {
            int section = scenario.Activities.IndexOf(finding.Activity) + 1;
            var item = new ListViewItem(new[] { $"{section}.{finding.Index + 1}", Path.GetFileName(finding.File.SourcePath), PreShowCheck.Describe(finding.Problem) })
            {
                ToolTipText = finding.File.SourcePath,
                // AP-05: the finding itself rides along so a click can locate the row it
                // came from — the position text alone left the operator counting rows.
                Tag = finding,
            };
            list.Items.Add(item);
        }
        list.ShowItemToolTips = true;
        // AP-05: single click selects the finding's row in the main panel (the window is
        // non-modal, so the operator can triage several findings in a row); double click
        // locates and closes. Wired after population so adding the items can't fire it.
        list.ItemSelectionChanged += (_, e) =>
        {
            if (e.IsSelected && e.Item?.Tag is PreShowFinding clicked) LocatePreShowFinding(clicked);
        };
        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is PreShowFinding target
                && LocatePreShowFinding(target))
                window.Close();
        };
        var close = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 34 };
        close.Click += (_, _) => window.Close();
        if (findings.Count > 0) window.Controls.Add(list);
        window.Controls.Add(close);
        window.Controls.Add(summary);
        window.FormClosed += (_, _) => { if (ReferenceEquals(_preShowWindow, window)) _preShowWindow = null; window.Dispose(); };
        ModernUi.StyleDialog(window);
        _preShowWindow = window;
        window.Show(FindForm()); // non-modal: the show is never blocked
    }

    /// <summary>AP-05: selects <paramref name="finding"/>'s file in the main panel and scrolls its
    /// activity row into view, through the exact path keyboard navigation uses
    /// (<see cref="MoveSelectionByKey"/>): expand the owning activity in the hidden tree first if
    /// it is collapsed (same AfterExpand path, so the collapse state persists the same way), then
    /// set <see cref="_tree"/>'s selection — AfterSelect refreshes the rows and the detail panel
    /// on its own. Returns false and does nothing when the finding is stale: the check ran against
    /// a snapshot, and by the time the operator clicks, the scenario may have been switched or the
    /// file removed — <see cref="FindNode"/> searches the tree the panel currently shows, so a
    /// finding whose file is no longer in it simply has no node to select.</summary>
    private bool LocatePreShowFinding(PreShowFinding finding)
    {
        if (FindNode(finding.File) is not { } node) return false;
        if (node.Parent is { } parent && !parent.IsExpanded) parent.Expand();
        _tree.SelectedNode = node;
        if (node.Parent?.Tag is Activity owner) ScrollActivityRowIntoView(owner);
        return true;
    }
}
