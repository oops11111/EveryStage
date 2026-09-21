using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

/// <summary>
/// Edits <c>MediaFile.OnCompletion</c> — PLANNING.md §6's "播放完成后动作：自动下一项/循环/停留
/// 等待". <c>PlaybackEngine.HandleCompletion</c> has read and correctly acted on this field since the
/// round that first gave <see cref="PlaybackEngine"/> any real playback behavior at all, and
/// <see cref="MediaFile.Clone"/> has always deep-copied it — but, like <c>MediaFile.StayDuration</c>
/// before this dialog existed (see this project's README "已知风险"), nothing ever let anyone actually
/// change it away from its default (<see cref="CompletionAction.NextItem"/>) for a specific file.
/// Non-nullable, unlike <see cref="PlayModeDialog"/>'s per-file case — <c>OnCompletion</c> has no
/// "inherit the activity's default" concept in the data model at all, so this is the simpler,
/// no-override-checkbox layout <see cref="PlayModeDialog"/> itself already uses for
/// <c>Activity.DefaultPlayMode</c> (<c>allowInherit: false</c>).
/// </summary>
public sealed class CompletionActionDialog : Form
{
    private readonly ComboBox _comboBox;

    public CompletionAction SelectedCompletionAction => ((ComboItem)_comboBox.SelectedItem!).Value;

    public CompletionActionDialog(string title, CompletionAction initialValue)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 120);

        var promptLabel = new Label { Text = "播放完成后：", Bounds = new Rectangle(12, 12, 296, 20) };
        _comboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(12, 36, 296, 24) };
        _comboBox.Items.Add(new ComboItem(CompletionAction.NextItem, "自动下一项"));
        _comboBox.Items.Add(new ComboItem(CompletionAction.Loop, "循环播放本文件"));
        _comboBox.Items.Add(new ComboItem(CompletionAction.HoldOnLastFrame, "停留等待（保持在最后一帧/最后画面）"));
        _comboBox.SelectedIndex = initialValue switch
        {
            CompletionAction.NextItem => 0,
            CompletionAction.Loop => 1,
            CompletionAction.HoldOnLastFrame => 2,
            _ => 0,
        };

        var okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, Bounds = new Rectangle(140, 76, 80, 28) };
        var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(228, 76, 80, 28) };
        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[] { promptLabel, _comboBox, okButton, cancelButton });
        ModernUi.StyleDialog(this);
    }

    private sealed record ComboItem(CompletionAction Value, string Label)
    {
        public override string ToString() => Label; // what the ComboBox actually renders.
    }
}
