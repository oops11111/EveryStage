using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI.Panels;

public sealed partial class ActivitiesPanel
{
    private void EnsureAtLeastOneScenario()
    {
        if (_store.Scenarios.Count > 0) return;

        var scenario = new Scenario { Name = "默认方案" };
        _store.Scenarios.Add(scenario);
        _store.CurrentScenarioId = scenario.Id;
        // First run, or a store that could not be read (the repository then refuses to save, protecting
        // the real file): the default scenario lives in memory until a save succeeds.
        try { _repository.Save(_store); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private Scenario? CurrentScenario =>
        _store.Scenarios.FirstOrDefault(s => s.Id == _store.CurrentScenarioId) ?? _store.Scenarios.FirstOrDefault();

    /// <summary>Saves an edit already applied to <see cref="_store"/>. On failure (file locked, no write
    /// access, or the store could not be read at startup) the operator is told, and the panel goes back to
    /// what is on disk. Every edit used to call Save bare: a failure reached only the crash log while the
    /// screen showed the change as done — a deleted activity came back after a restart (audit A-7).</summary>
    private bool TrySave()
    {
        try
        {
            _repository.Save(_store);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            bool reverted = _repository.TryReloadInto(_store);
            EnsureAtLeastOneScenario();
            MessageBox.Show(this,
                reverted ? $"修改未能保存，已恢复为保存前的内容。\n{DataDirectory.Describe(ex)}" : $"修改未能保存，重启后将丢失。\n{ex.Message}",
                "方案保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshScenarioCombo();
            RefreshTree();
            return false;
        }
    }

    private void RefreshScenarioCombo()
    {
        _scenarioCombo.SelectedIndexChanged -= OnScenarioComboChanged;
        _scenarioCombo.Items.Clear();
        foreach (var scenario in _store.Scenarios) _scenarioCombo.Items.Add(scenario);
        _scenarioCombo.DisplayMember = nameof(Scenario.Name);
        _scenarioCombo.SelectedItem = CurrentScenario;
        _scenarioCombo.SelectedIndexChanged += OnScenarioComboChanged;
    }

    private void OnScenarioComboChanged(object? sender, EventArgs e)
    {
        if (_scenarioCombo.SelectedItem is not Scenario scenario) return;
        _store.CurrentScenarioId = scenario.Id;
        if (!TrySave()) return;
        RefreshTree();
    }

    private void OnNewScenario()
    {
        string? name = TextInputDialog.Prompt(this, "新建方案", "方案名称：");
        if (name == null) return;

        var scenario = new Scenario { Name = name };
        _store.Scenarios.Add(scenario);
        _store.CurrentScenarioId = scenario.Id;
        if (!TrySave()) return;
        _fileOpLog.LogScenarioCreated(scenario.Id, scenario.Name);
        RefreshScenarioCombo();
        RefreshTree();
    }

    private void OnSaveAsScenario()
    {
        var current = CurrentScenario;
        if (current == null) return;

        string? name = TextInputDialog.Prompt(this, "另存为", "新方案名称：", current.Name + " 副本");
        if (name == null) return;

        // Deep-clone activities/files so editing the copy never mutates the original scenario —
        // sharing MediaFile instances between two scenarios would make "another modified" quietly
        // change both.
        var clone = new Scenario
        {
            Name = name,
            Activities = current.Activities.Select(CloneActivity).ToList(),
        };
        _store.Scenarios.Add(clone);
        _store.CurrentScenarioId = clone.Id;
        if (!TrySave()) return;
        _fileOpLog.LogScenarioCreated(clone.Id, clone.Name);
        RefreshScenarioCombo();
        RefreshTree();
    }

    private static Activity CloneActivity(Activity source)
    {
        var clone = new Activity
        {
            Name = source.Name,
            IsCollapsed = source.IsCollapsed,
            DefaultPlayMode = source.DefaultPlayMode,
            MusicPlaylistId = source.MusicPlaylistId,
            MusicFadeOut = source.MusicFadeOut,
        };
        // MediaFile.Clone() — see that method's own doc comment; this used to be a private
        // CloneFile method living only here, pulled out onto MediaFile itself now that FilesPanel's
        // "加入活动..." needs the exact same field-by-field copy.
        clone.Files.AddRange(source.Files.Select(file => file.Clone()));
        return clone;
    }

    private void OnDeleteScenario()
    {
        var current = CurrentScenario;
        if (current == null || _store.Scenarios.Count <= 1) return; // always keep at least one.

        var confirm = MessageBox.Show(this, $"确定要删除方案 \"{current.Name}\" 吗？其中的活动也会一并删除。",
            "删除方案", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        // Remember the slot and whether this was the current scenario, so the undo offer below can
        // put the scenario back exactly where it was.
        int index = _store.Scenarios.IndexOf(current);
        bool wasCurrent = _store.CurrentScenarioId == current.Id;
        _store.Scenarios.Remove(current);
        _store.CurrentScenarioId = _store.Scenarios.First().Id;
        if (!TrySave()) return;
        _fileOpLog.LogScenarioDeleted(current.Id, current.Name);
        RefreshScenarioCombo();
        RefreshTree();
        OfferUndoScenarioDelete(current, index, wasCurrent);
    }

    /// <summary>删后撤销 for a scenario deletion (L-01). The pre-delete confirmation stays, but a
    /// scenario is a whole show's running order — deleting the wrong one is accident-grade, and a
    /// confirmation the operator clicked through cannot be unclicked. So a successful delete offers
    /// 撤销 in a toast instead (a toast with actions stays 10 s, the same window 设置 gives after a
    /// risky change). The offer is only made after the delete AND its save succeeded: if TrySave
    /// failed it has already rolled the screen back to disk, and there is nothing to undo. There is
    /// deliberately no "restored" file-operation log entry — FileOperationLogger has no such event,
    /// and logging the restore as a creation would falsify the record; the deletion itself stays
    /// logged as what it was.</summary>
    private void OfferUndoScenarioDelete(Scenario deleted, int index, bool wasCurrent)
    {
        if (ShowToast == null) return;
        ShowToast($"方案“{deleted.Name}”已删除", ToastSeverity.Info, new ToastAction[]
        {
            new("撤销", () =>
            {
                if (_store.Scenarios.Contains(deleted)) return; // already back; a second click changes nothing.
                _store.Scenarios.Insert(Math.Min(index, _store.Scenarios.Count), deleted);
                if (wasCurrent) _store.CurrentScenarioId = deleted.Id;
                if (!TrySave()) return;
                RefreshScenarioCombo();
                RefreshTree();
            }),
        });
    }

    private void OnNewActivity()
    {
        var scenario = CurrentScenario;
        if (scenario == null) return;

        string? name = TextInputDialog.Prompt(this, "新建活动", "活动名称：");
        if (name == null) return;

        var activity = new Activity { Name = name };
        scenario.Activities.Add(activity);
        if (!TrySave()) return;
        _fileOpLog.LogActivityCreated(scenario.Id, activity.Id, activity.Name);
        RefreshTree();
    }

    private void OnRenameActivity()
    {
        var (scenario, activity, _) = GetSelection();
        if (scenario == null || activity == null) return;

        string? name = TextInputDialog.Prompt(this, "重命名活动", "活动名称：", activity.Name);
        if (name == null) return;

        activity.Name = name;
        if (!TrySave()) return;
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        RefreshTree();
    }

    private void OnDeleteActivity()
    {
        var (scenario, activity, _) = GetSelection();
        if (scenario == null || activity == null) return;

        var confirm = MessageBox.Show(this, $"确定要删除活动 \"{activity.Name}\" 吗？",
            "删除活动", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        // Remember the owning scenario and slot, for the undo offer below (same reasoning as
        // OfferUndoScenarioDelete: confirmation first, 10 s 撤销 after a successful delete).
        int index = scenario.Activities.IndexOf(activity);
        scenario.Activities.Remove(activity);
        if (!TrySave()) return;
        _fileOpLog.LogActivityDeleted(scenario.Id, activity.Id, activity.Name);
        RefreshTree();
        OfferUndoActivityDelete(scenario, activity, index);
    }

    /// <summary>删后撤销 for an activity deletion — see <see cref="OfferUndoScenarioDelete"/> for why
    /// this exists and why the restore is not logged as a creation. One extra guard: if the owning
    /// scenario was itself deleted inside the undo window, the activity's home is gone and the undo
    /// quietly does nothing rather than resurrecting half a scenario.</summary>
    private void OfferUndoActivityDelete(Scenario scenario, Activity deleted, int index)
    {
        if (ShowToast == null) return;
        ShowToast($"活动“{deleted.Name}”已删除", ToastSeverity.Info, new ToastAction[]
        {
            new("撤销", () =>
            {
                if (!_store.Scenarios.Contains(scenario) || scenario.Activities.Contains(deleted)) return;
                scenario.Activities.Insert(Math.Min(index, scenario.Activities.Count), deleted);
                if (!TrySave()) return;
                RefreshTree();
            }),
        });
    }
}
