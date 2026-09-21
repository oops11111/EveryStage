using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

/// <summary>
/// Lets <c>FilesPanel</c>'s "加入活动..." (PLANNING.md §11 "批量选择") pick a destination
/// Scenario + Activity to copy selected library files into — the inverse direction of
/// <see cref="LibraryFilePickerDialog"/> (which lets <c>ActivitiesPanel</c> pick a file FROM the
/// library to add to whichever activity is already selected there). This dialog exists because
/// <c>FilesPanel</c> has no activity-list UI of its own to drag onto or select within — see that
/// class's own doc comment on why "加入活动" was previously left unimplemented (needing exactly this
/// kind of cross-panel picker, which didn't exist until now).
///
/// Same <see cref="ComboBox.DisplayMember"/> convention <c>ActivitiesPanel.RefreshScenarioCombo</c>
/// already uses for its own scenario picker, applied here to both the scenario and activity
/// dropdowns, rather than a private wrapper record — <see cref="Scenario"/>/<see cref="Activity"/>
/// already expose a plain <c>Name</c> property, so there's nothing a wrapper would add.
/// </summary>
public sealed class ActivityPickerDialog : Form
{
    private readonly ComboBox _scenarioCombo;
    private readonly ComboBox _activityCombo;
    private readonly Button _okButton;

    /// <summary>Non-null together with <see cref="SelectedActivity"/> only when this dialog closed
    /// with <see cref="DialogResult.OK"/> — needed alongside the activity itself so the caller can
    /// log/reference which scenario it belongs to without having to search every scenario's
    /// <see cref="Scenario.Activities"/> list back for it.</summary>
    public Scenario? SelectedScenario { get; private set; }
    public Activity? SelectedActivity { get; private set; }

    public ActivityPickerDialog(IReadOnlyList<Scenario> scenarios, Guid? preferredScenarioId)
    {
        Text = "加入活动";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 172);

        var scenarioLabel = new Label { Text = "方案：", Bounds = new Rectangle(12, 12, 296, 20) };
        _scenarioCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Bounds = new Rectangle(12, 36, 296, 24),
            DisplayMember = nameof(Scenario.Name),
        };

        var activityLabel = new Label { Text = "活动：", Bounds = new Rectangle(12, 68, 296, 20) };
        _activityCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Bounds = new Rectangle(12, 92, 296, 24),
            DisplayMember = nameof(Activity.Name),
        };

        _okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, Bounds = new Rectangle(140, 132, 80, 28) };
        _okButton.Click += (_, _) =>
        {
            SelectedScenario = _scenarioCombo.SelectedItem as Scenario;
            SelectedActivity = _activityCombo.SelectedItem as Activity;
        };
        var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(228, 132, 80, 28) };
        AcceptButton = _okButton;
        CancelButton = cancelButton;

        // Wired before populating/selecting below, so the very first PopulateActivityCombo() call
        // (triggered by setting _scenarioCombo.SelectedIndex further down) runs against a fully
        // constructed _activityCombo/_okButton rather than a still-null field.
        _scenarioCombo.SelectedIndexChanged += (_, _) => PopulateActivityCombo();

        foreach (var scenario in scenarios) _scenarioCombo.Items.Add(scenario);
        int preferredIndex = -1;
        for (int i = 0; i < scenarios.Count; i++)
        {
            if (scenarios[i].Id == preferredScenarioId) { preferredIndex = i; break; }
        }
        // Only assigns (and therefore only fires SelectedIndexChanged) when there's at least one
        // scenario to select — EnsureAtLeastOneScenario (ActivitiesPanel's constructor) guarantees
        // this in practice, but an empty list here would otherwise leave _activityCombo/_okButton
        // in their harmless "nothing selected, OK produces nulls" default state instead of throwing.
        if (_scenarioCombo.Items.Count > 0)
            _scenarioCombo.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;

        Controls.AddRange(new Control[] { scenarioLabel, _scenarioCombo, activityLabel, _activityCombo, _okButton, cancelButton });
        ModernUi.StyleDialog(this);
    }

    private void PopulateActivityCombo()
    {
        _activityCombo.Items.Clear();
        if (_scenarioCombo.SelectedItem is Scenario scenario)
        {
            foreach (var activity in scenario.Activities) _activityCombo.Items.Add(activity);
        }
        // A scenario with zero activities (a brand-new, never-populated one) has nothing valid to
        // add files into — disabling OK here is simpler and clearer than letting the caller receive
        // a null SelectedActivity back and have to guess why.
        _okButton.Enabled = _activityCombo.Items.Count > 0;
        if (_activityCombo.Items.Count > 0) _activityCombo.SelectedIndex = 0;
    }
}
