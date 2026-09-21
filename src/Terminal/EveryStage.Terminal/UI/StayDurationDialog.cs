namespace EveryStage.Terminal.UI;

/// <summary>
/// Edits a single <c>MediaFile.StayDuration</c> — PLANNING.md §6's "停留时长（图片/文档）" per-file
/// override. Until this dialog existed, <c>MediaFile.StayDuration</c> was read by
/// <c>PlaybackEngine.ArmStayDurationTimer</c> and cloned by <c>MediaFile.Clone</c>, but had
/// no editing entry point anywhere — worse than simply missing, <c>SettingsPanel</c>'s own "默认停留
/// 时长" help text already claimed "单个文件自己设置的停留时长（活动面板里配置）始终优先于这里的
/// 默认值", describing a feature that didn't actually exist yet. Same checkbox-enables-a-control
/// convention <c>SettingsPanel</c>'s own "启用默认停留时长" control pair already established, applied
/// here to the per-file override instead of the global default.
/// </summary>
public sealed class StayDurationDialog : Form
{
    private readonly CheckBox _overrideEnabledCheckbox;
    private readonly NumericUpDown _secondsUpDown;

    /// <summary>Null means "no per-file override — inherit whatever the 设置 面板's global default
    /// says (or hold indefinitely if that isn't configured either)", mirroring
    /// <c>MediaFile.StayDuration</c>'s own nullable meaning exactly.</summary>
    public TimeSpan? SelectedStayDuration =>
        _overrideEnabledCheckbox.Checked ? TimeSpan.FromSeconds((double)_secondsUpDown.Value) : null;

    public StayDurationDialog(string title, TimeSpan? currentStayDuration)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 148);

        _overrideEnabledCheckbox = new CheckBox
        {
            Text = "为此文件单独设置停留时长",
            AutoSize = true,
            Location = new Point(12, 12),
            Checked = currentStayDuration.HasValue,
        };
        _secondsUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 3600,
            // 1m/3600m (not bare int literals): Math.Clamp<T> infers T from all three arguments
            // together, and the first is already decimal — mixing in int literals here risks the
            // compiler picking a different (or failing to find a common) T, unlike SettingsPanel's
            // own Math.Clamp(int, 1, 3600) call where every argument is already int.
            Value = Math.Clamp((decimal)(currentStayDuration ?? TimeSpan.FromSeconds(10)).TotalSeconds, 1m, 3600m),
            Location = new Point(32, 40),
            Width = 80,
            Enabled = currentStayDuration.HasValue,
        };
        var secondsLabel = new Label { Text = "秒", AutoSize = true, Location = new Point(118, 43) };
        _overrideEnabledCheckbox.CheckedChanged += (_, _) => _secondsUpDown.Enabled = _overrideEnabledCheckbox.Checked;

        var noteLabel = new Label
        {
            Text = "关闭时跟随 设置 面板里的全局默认停留时长（如果那边也没配置，则一直停留直到手动切换）。",
            ForeColor = Color.DimGray,
            AutoSize = false,
            Bounds = new Rectangle(12, 72, 296, 34),
        };

        var okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, Bounds = new Rectangle(140, 112, 80, 28) };
        var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(228, 112, 80, 28) };
        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[]
        {
            _overrideEnabledCheckbox, _secondsUpDown, secondsLabel, noteLabel, okButton, cancelButton,
        });
        ModernUi.StyleDialog(this);
    }
}
