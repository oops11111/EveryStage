using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

/// <summary>
/// Edits <c>MediaFile.IsBackgroundAudio</c> + <c>MediaFile.BackgroundAudioVisual</c> — the two
/// audio-only properties <c>PlaybackEngine.PlayStandaloneAudio</c>/<c>ApplyAudioVisual</c> finally
/// gave real behavior to (this project's README risk #61). Same minimal-dialog style as
/// <see cref="PlayModeDialog"/>/<see cref="TextInputDialog"/>. Only ever opened for a
/// <c>MediaFile</c> whose <c>Kind == MediaKind.Audio</c> — <c>ActivitiesPanel</c> gates the button
/// that opens this on that, the same way it already gates "播放方式..." on a selection existing.
/// </summary>
public sealed class AudioPropertiesDialog : Form
{
    private readonly CheckBox _backgroundCheckbox;
    private readonly Label _backgroundCaveatLabel;
    private readonly ComboBox _visualCombo;

    public bool IsBackgroundAudio => _backgroundCheckbox.Checked;
    public AudioVisual BackgroundAudioVisual => ((ComboItem)_visualCombo.SelectedItem!).Value;

    public AudioPropertiesDialog(string title, bool initialIsBackgroundAudio, AudioVisual initialVisual)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(360, 172);

        _backgroundCheckbox = new CheckBox
        {
            Text = "作为背景音频叠加播放（不占用活动顺序位）",
            AutoSize = true,
            Bounds = new Rectangle(12, 12, 336, 20),
            Checked = initialIsBackgroundAudio,
        };

        // Honest about a real, current limitation instead of letting the checkbox imply a feature
        // that doesn't exist yet — same "don't claim more than what's actually true" reasoning as
        // the Caster project's "确认≠健康" caveat (see that project's README). PlaybackEngine.PlayFile's
        // IsBackgroundAudio branch is still a documented no-op (see its own doc comment and this
        // project's README risk #61): checking this box currently means the file simply stops
        // playing at all, not that it starts playing as a background overlay.
        _backgroundCaveatLabel = new Label
        {
            Text = "⚠ 背景音轨叠加播放尚未实现——勾选后这个文件将不会播放，直到该功能真正做完",
            ForeColor = Color.DarkRed,
            AutoSize = false,
            Bounds = new Rectangle(12, 34, 336, 34),
            Visible = initialIsBackgroundAudio,
        };
        _backgroundCheckbox.CheckedChanged += (_, _) => _backgroundCaveatLabel.Visible = _backgroundCheckbox.Checked;

        var visualLabel = new Label { Text = "投屏显示内容：", Bounds = new Rectangle(12, 76, 336, 20) };
        _visualCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Bounds = new Rectangle(12, 100, 336, 24),
        };
        // Labels call out that Waveform/DefaultBackgroundImage are placeholders rather than implying
        // a finished product visual — see AudioVisualRenderer's own doc comment for why (no bundled
        // image assets exist in this repo at all, and Waveform is a single-bar peak meter, not a
        // true scrolling waveform).
        _visualCombo.Items.Add(new ComboItem(AudioVisual.Waveform, "波形动效（当前是占位：峰值电平表）"));
        _visualCombo.Items.Add(new ComboItem(AudioVisual.DefaultBackgroundImage, "默认背景图（当前是占位：纯色+文件名）"));
        _visualCombo.Items.Add(new ComboItem(AudioVisual.Black, "纯黑"));
        _visualCombo.SelectedIndex = initialVisual switch
        {
            AudioVisual.Waveform => 0,
            AudioVisual.DefaultBackgroundImage => 1,
            _ => 2,
        };

        var okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, Bounds = new Rectangle(180, 136, 80, 28) };
        var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(268, 136, 80, 28) };
        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[]
        {
            _backgroundCheckbox, _backgroundCaveatLabel, visualLabel, _visualCombo, okButton, cancelButton,
        });
    }

    private sealed record ComboItem(AudioVisual Value, string Label)
    {
        public override string ToString() => Label; // what the ComboBox actually renders.
    }
}
