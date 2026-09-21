using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

/// <summary>"Pick SequentialAuto or ManualSelect" dialog for both <c>Activity.DefaultPlayMode</c>
/// (a required value — <paramref name="allowInherit"/> false) and <c>MediaFile.PlayModeOverride</c>
/// (nullable, "null = inherit the owning activity's default" — <paramref name="allowInherit"/> true,
/// which adds the "覆盖活动默认播放方式" checkbox gating the combo box, the same
/// checkbox-enables-a-control convention <c>SettingsPanel</c>'s "启用默认停留时长" already
/// established). This project's first UI for <see cref="PlayMode"/> at either level — added the same
/// round <c>PlaybackEngine.EffectivePlayMode</c>/<c>TryAdvance</c> first gave the enum any actual
/// runtime effect at all (see that class's doc comment); the per-file override half was deliberately
/// deferred one extra round past the activity-level half (see this project's README "已知风险" for
/// why) and lands here now that it has somewhere real to plug in. Same minimal-dialog style as
/// <see cref="TextInputDialog"/>, just with a <see cref="ComboBox"/> in place of a
/// <see cref="TextBox"/> since this picks between two fixed choices rather than free text.
/// </summary>
public sealed class PlayModeDialog : Form
{
    private readonly CheckBox? _overrideCheckbox;
    private readonly ComboBox _comboBox;

    /// <summary>Null only when constructed with <c>allowInherit: true</c> and the user left the
    /// override checkbox unchecked — meaning "no override, inherit the activity's default". Never
    /// null when this dialog was constructed with <c>allowInherit: false</c> (the activity-level
    /// case, which has no "inherit" concept to fall back to).</summary>
    public PlayMode? SelectedPlayMode =>
        _overrideCheckbox != null && !_overrideCheckbox.Checked ? null : ((ComboItem)_comboBox.SelectedItem!).Value;

    /// <param name="initialValue">Current value to preselect. For the non-nullable activity-level
    /// case this is never null in practice (<c>Activity.DefaultPlayMode</c> itself isn't nullable);
    /// for the per-file case null means "currently inheriting".</param>
    /// <param name="allowInherit">True for editing <c>MediaFile.PlayModeOverride</c> (adds the
    /// override checkbox so the result can come back null); false for
    /// <c>Activity.DefaultPlayMode</c>, which has no "inherit" state to offer.</param>
    public PlayModeDialog(string title, PlayMode? initialValue, bool allowInherit = false)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, allowInherit ? 148 : 120);

        // Two non-overlapping layouts rather than one arithmetic formula shared between them —
        // easier to see at a glance that neither case's controls collide.
        int promptY = 12, comboY = 36, buttonY = 76;
        if (allowInherit)
        {
            _overrideCheckbox = new CheckBox
            {
                Text = "覆盖活动默认播放方式",
                AutoSize = true,
                Bounds = new Rectangle(12, 12, 296, 20),
                Checked = initialValue.HasValue,
            };
            promptY = 40;
            comboY = 64;
            buttonY = 104;
        }

        var promptLabel = new Label { Text = "播放方式：", Bounds = new Rectangle(12, promptY, 296, 20) };
        _comboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Bounds = new Rectangle(12, comboY, 296, 24),
            Enabled = _overrideCheckbox?.Checked ?? true,
        };
        _comboBox.Items.Add(new ComboItem(PlayMode.SequentialAuto, "顺序自动播放"));
        _comboBox.Items.Add(new ComboItem(PlayMode.ManualSelect, "手动点选"));
        _comboBox.SelectedIndex = (initialValue ?? PlayMode.SequentialAuto) == PlayMode.SequentialAuto ? 0 : 1;

        if (_overrideCheckbox != null)
            _overrideCheckbox.CheckedChanged += (_, _) => _comboBox.Enabled = _overrideCheckbox.Checked;

        var okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, Bounds = new Rectangle(140, buttonY, 80, 28) };
        var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(228, buttonY, 80, 28) };
        AcceptButton = okButton;
        CancelButton = cancelButton;

        var controls = new List<Control> { promptLabel, _comboBox, okButton, cancelButton };
        if (_overrideCheckbox != null) controls.Add(_overrideCheckbox);
        Controls.AddRange(controls.ToArray());
        ModernUi.StyleDialog(this);
    }

    private sealed record ComboItem(PlayMode Value, string Label)
    {
        public override string ToString() => Label; // what the ComboBox actually renders.
    }
}
