using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

/// <summary>Minimal "pick SequentialAuto or ManualSelect" dialog for <c>Activity.DefaultPlayMode</c>
/// — this project's first UI for <see cref="PlayMode"/>, added the same round
/// <c>PlaybackEngine.EffectivePlayMode</c>/<c>TryAdvance</c> first gave the enum any actual runtime
/// effect (see that class's doc comment). Same minimal-dialog style as <see cref="TextInputDialog"/>,
/// just with a <see cref="ComboBox"/> in place of a <see cref="TextBox"/> since this picks between
/// two fixed choices rather than free text.
///
/// Deliberately only edits <c>Activity.DefaultPlayMode</c>, not the per-file
/// <c>MediaFile.PlayModeOverride</c> that can override it — <see cref="ActivitiesPanel"/> has no UI
/// entry point for the per-file override yet, so a file's <c>PlayModeOverride</c> stays whatever it
/// was set to at construction (null, i.e. "inherit the activity's default") until a future round
/// adds one; see this project's README "已知风险" for this being a recorded, intentional gap rather
/// than an oversight.
/// </summary>
public sealed class PlayModeDialog : Form
{
    private readonly ComboBox _comboBox;

    public PlayMode SelectedPlayMode => ((ComboItem)_comboBox.SelectedItem!).Value;

    public PlayModeDialog(string title, PlayMode initialValue)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 120);

        var promptLabel = new Label { Text = "播放方式：", Bounds = new Rectangle(12, 12, 296, 20) };
        _comboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(12, 36, 296, 24) };
        _comboBox.Items.Add(new ComboItem(PlayMode.SequentialAuto, "顺序自动播放"));
        _comboBox.Items.Add(new ComboItem(PlayMode.ManualSelect, "手动点选"));
        _comboBox.SelectedIndex = initialValue == PlayMode.SequentialAuto ? 0 : 1;

        var okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, Bounds = new Rectangle(140, 76, 80, 28) };
        var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(228, 76, 80, 28) };
        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[] { promptLabel, _comboBox, okButton, cancelButton });
    }

    private sealed record ComboItem(PlayMode Value, string Label)
    {
        public override string ToString() => Label; // what the ComboBox actually renders.
    }
}
