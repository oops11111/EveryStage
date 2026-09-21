namespace EveryStage.Terminal.UI;

/// <summary>
/// Edits a single <c>MediaFile.FadeDuration</c>/<c>VolumeFollowsFade</c> pair — PLANNING.md §6's
/// per-file "淡入/淡出时长 + 音量是否随渐变（视频/音频）". Same checkbox-enables-a-control convention
/// <see cref="StayDurationDialog"/> already established for its own nullable <c>TimeSpan</c> field,
/// reused here: <see cref="VolumeFollowsFade"/> is the on/off switch (matches its own name and
/// <c>PlaybackEngine.FadeDurationFor</c>'s reading of it — see that method's doc comment), and the
/// duration field is only meaningful/enabled while it's on.
///
/// Added once <c>PlaybackEngine.FadeDurationFor</c>/<see cref="ContentEngine.AudioContentController"/>/
/// <see cref="ContentEngine.VideoContentController"/> gave these two fields real behavior (this
/// project's README risk #109), following the same "先做行为、再做UI" order
/// <see cref="AudioPropertiesDialog"/>/<see cref="StayDurationDialog"/> already established. Fade-out
/// was added a round after fade-in (see this project's README) and carries the single biggest
/// unverified-code risk in this whole codebase — <c>AudioContentController</c>'s class doc comment
/// explains why — so this dialog's warning label reflects "fade-out is attempted but may silently
/// not happen for some files" rather than claiming it as a fully reliable feature.
/// </summary>
public sealed class FadeDialog : Form
{
    private readonly CheckBox _enabledCheckbox;
    private readonly NumericUpDown _secondsUpDown;

    /// <summary>Maps directly onto <c>MediaFile.VolumeFollowsFade</c> — see that field's own doc
    /// comment and <c>PlaybackEngine.FadeDurationFor</c> for why this is the master on/off switch,
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
            Text = "为此文件启用淡入淡出（音量随渐变）",
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
        // reliable feature — same "don't claim more than what's actually true" reasoning as
        // AudioPropertiesDialog's own red caveat label. Fade-in is fully reliable (needs only
        // playback position, always available); fade-out needs the file's total duration in
        // advance, queried through this codebase's single least-verified Media Foundation call (see
        // AudioContentController's class doc comment) — it may silently not happen for some files
        // rather than throwing anything visible, so this says so rather than promising it works.
        var noteLabel = new Label
        {
            Text = "⚠ 淡出依赖一个尚未在真机验证过的时长查询——对某些文件可能不生效（只有淡入\n" +
                   "生效），不会报错，只是播放结束前不会渐弱。淡入本身不受此影响，总是可靠的。",
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
        ModernUi.StyleDialog(this);
    }
}
