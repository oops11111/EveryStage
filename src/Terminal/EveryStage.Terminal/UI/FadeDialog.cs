namespace EveryStage.Terminal.UI;

/// <summary>
/// Edits a single <c>MediaFile.FadeDuration</c>/<c>VolumeFollowsFade</c> pair — PLANNING.md §6's
/// per-file "淡入/淡出时长 + 音量是否随渐变（视频/音频）". Same checkbox-enables-a-control convention
/// <see cref="StayDurationDialog"/> already established for its own nullable <c>TimeSpan</c> field,
/// reused here: <see cref="VolumeFollowsFade"/> is the on/off switch (matches its own name and
/// <c>PlaybackEngine.FadeInDurationFor</c>'s reading of it — see that method's doc comment), and the
/// duration field is only meaningful/enabled while it's on.
///
/// Added once <c>PlaybackEngine.FadeInDurationFor</c>/<see cref="ContentEngine.AudioContentController"/>/
/// <see cref="ContentEngine.VideoContentController"/> gave these two fields real behavior (this
/// project's README risk #109), following the same "先做行为、再做UI" order
/// <see cref="AudioPropertiesDialog"/>/<see cref="StayDurationDialog"/> already established — except,
/// unlike those two, the behavior here is only half-finished (fade-IN only, no fade-OUT yet), so this
/// dialog carries the same kind of honest in-progress caveat <see cref="AudioPropertiesDialog"/>'s red
/// warning label does for background-audio overlay, rather than waiting for 100% completion before
/// exposing anything editable — see this project's README risk #109(c) for why exposing the working
/// half now, with a caveat about the missing half, was judged better than leaving both fields
/// unreachable from any UI a second round in a row.
/// </summary>
public sealed class FadeDialog : Form
{
    private readonly CheckBox _enabledCheckbox;
    private readonly NumericUpDown _secondsUpDown;

    /// <summary>Maps directly onto <c>MediaFile.VolumeFollowsFade</c> — see that field's own doc
    /// comment and <c>PlaybackEngine.FadeInDurationFor</c> for why this is the master on/off switch,
    /// not just a label on the checkbox.</summary>
    public bool VolumeFollowsFade => _enabledCheckbox.Checked;

    /// <summary>Maps onto <c>MediaFile.FadeDuration</c> — null while <see cref="VolumeFollowsFade"/>
    /// is off, mirroring <see cref="StayDurationDialog.SelectedStayDuration"/>'s same
    /// checkbox-off-means-null convention.</summary>
    public TimeSpan? FadeDuration =>
        _enabledCheckbox.Checked ? TimeSpan.FromSeconds((double)_secondsUpDown.Value) : null;

    public FadeDialog(string title, bool currentVolumeFollowsFade, TimeSpan? currentFadeDuration)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(360, 182);

        _enabledCheckbox = new CheckBox
        {
            Text = "为此文件启用淡入（音量随渐变）",
            AutoSize = true,
            Location = new Point(12, 12),
            Checked = currentVolumeFollowsFade,
        };
        _secondsUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 60,
            // 1m/60m, matching StayDurationDialog's own reasoning: Math.Clamp<T> infers T from all
            // three arguments together, and the first is already decimal here.
            Value = Math.Clamp((decimal)(currentFadeDuration ?? TimeSpan.FromSeconds(3)).TotalSeconds, 1m, 60m),
            Location = new Point(32, 40),
            Width = 80,
            Enabled = currentVolumeFollowsFade,
        };
        var secondsLabel = new Label { Text = "秒", AutoSize = true, Location = new Point(118, 43) };
        _enabledCheckbox.CheckedChanged += (_, _) => _secondsUpDown.Enabled = _enabledCheckbox.Checked;

        // Honest about a real, current limitation instead of letting the checkbox imply a fully
        // finished feature — same "don't claim more than what's actually true" reasoning as
        // AudioPropertiesDialog's own red caveat label. Unlike that label, this one isn't a "does
        // nothing at all" warning: fade-in genuinely works (see this project's README risk #109);
        // only the fade-OUT half at end of playback is still missing.
        var noteLabel = new Label
        {
            Text = "⚠ 目前只有淡入生效（从静音渐变到正常音量）——播放结束前的淡出还没有实现，\n" +
                   "需要先知道文件总时长才能算出淡出的起点，这个仓库的解码封装目前不提供这个信息。",
            ForeColor = Color.DarkRed,
            AutoSize = false,
            Bounds = new Rectangle(12, 72, 336, 44),
        };

        var okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, Bounds = new Rectangle(180, 146, 80, 28) };
        var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(268, 146, 80, 28) };
        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[]
        {
            _enabledCheckbox, _secondsUpDown, secondsLabel, noteLabel, okButton, cancelButton,
        });
    }
}
