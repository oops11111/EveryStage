using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

/// <summary>
/// Edits <c>MediaFile.IsBackgroundAudio</c> + <c>MediaFile.BackgroundAudioVisual</c> — the two
/// audio-only properties <c>PlaybackEngine.PlayStandaloneAudio</c>/<c>ApplyAudioVisual</c> gave real
/// behavior to first (this project's README risk #61, the non-background half), and
/// <c>PlaybackEngine.PlayBackgroundAudio</c>/<c>StartOrUpdateBackgroundAudio</c> gave the
/// <c>IsBackgroundAudio == true</c> half real overlay-playback behavior in a later round (README
/// risk #116) — see that pair's own doc comments for the specific product decisions made where
/// PLANNING.md doesn't specify (what <c>OnCompletion</c> means for an overlay track, and why
/// <c>PlaybackEngine.Pause</c>/<c>Resume</c> deliberately don't affect it). Same minimal-dialog
/// style as <see cref="PlayModeDialog"/>/<see cref="TextInputDialog"/>. Only ever opened for a
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

        // Updated once PlaybackEngine.PlayBackgroundAudio gave this checkbox real behavior (README
        // risk #116) — no longer a "this does nothing yet" warning like the label this replaced, but
        // still an honest caveat about what's genuinely NOT covered: no independent volume control
        // (always plays at full volume — see PlaybackEngine.BackgroundAudioController's own doc
        // comment), and PlaybackEngine.Pause/Resume (悬浮预览窗的"暂停") deliberately don't affect
        // it at all — pausing the foreground content does not pause this file.
        _backgroundCaveatLabel = new Label
        {
            Text = "ⓘ 背景音轨会独立叠加播放，不受悬浮预览窗\"暂停\"影响，也没有单独的音量调节",
            ForeColor = Color.DimGray,
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
        ModernUi.StyleDialog(this);
    }

    private sealed record ComboItem(AudioVisual Value, string Label)
    {
        public override string ToString() => Label; // what the ComboBox actually renders.
    }
}
