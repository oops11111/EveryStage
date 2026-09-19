using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

/// <summary>
/// PLANNING.md's "统一设置属性（批量）" — edits a chosen subset of playback properties across several
/// <see cref="MediaFile"/>s selected at once in <see cref="Panels.ActivitiesPanel"/> (via Ctrl+Click
/// multi-select on file nodes — see that class's own doc comment on <c>_multiSelectedFileNodes</c>),
/// instead of opening each single-file dialog (<see cref="PlayModeDialog"/>,
/// <see cref="CompletionActionDialog"/>, <see cref="StayDurationDialog"/>, <see cref="FadeDialog"/>,
/// <see cref="AudioPropertiesDialog"/>) once per file.
///
/// Design confirmed with the user before writing any code, because PLANNING.md doesn't specify how a
/// batch editor should represent "these files may currently disagree on this property's value":
/// EVERY property row here defaults to unchecked ("修改此项") — an unchecked row leaves every selected
/// file's own existing value for that property completely untouched, whatever it currently is. Only a
/// row the operator explicitly checks gets written, with the single value shown next to it, to every
/// selected file. This sidesteps needing to compute or display a "mixed current value" UI state
/// entirely — the alternative design offered (and not chosen) was restricting batch selection to a
/// single <see cref="MediaKind"/> at a time.
///
/// Property rows are further filtered to the ones PLANNING.md says are meaningful for the actual
/// selection's <see cref="MediaKind"/>s ("跨类型选中时屏蔽不适用的属性项"): <see cref="PlayMode"/>,
/// <see cref="CompletionAction"/> and <c>AllowManualSkip</c> apply to every kind and are always shown;
/// <c>StayDuration</c> only when every selected file is Image or Document; <c>FadeDuration</c>/
/// <c>VolumeFollowsFade</c> only when every selected file is Video or Audio; <c>IsBackgroundAudio</c>/
/// <c>BackgroundAudioVisual</c> only when every selected file is Audio — same three groupings the
/// single-file buttons in <see cref="Panels.ActivitiesPanel"/> already gate on
/// (<c>UpdateButtonStates</c>), just requiring ALL selected files to qualify instead of just one.
///
/// Each applicable row reuses the exact same checkbox-enables-a-control / nullable-means-inherit
/// conventions the single-file dialogs above already established, rather than inventing new ones —
/// see each row's own construction comment below for which single-file dialog it mirrors. Manual
/// <see cref="Bounds"/>/<see cref="Location"/> layout like every other dialog in this project; total
/// height is computed at construction time (rather than hardcoded like the fixed-row-count dialogs
/// above) since the number of visible rows here varies with the selection's <see cref="MediaKind"/>s.
/// </summary>
public sealed class BatchPropertiesDialog : Form
{
    private const int Width = 420;
    private const int ContentWidth = Width - 12 - 12 - 16; // ClientSize width minus left/right margins and a little slack for the border.

    // --- 播放方式 (always applicable) ---
    private readonly CheckBox _playModeCheckbox;
    private readonly ComboBox _playModeCombo;

    // --- 完成后动作 (always applicable) ---
    private readonly CheckBox _completionCheckbox;
    private readonly ComboBox _completionCombo;

    // --- 允许手动切换 (always applicable) ---
    private readonly CheckBox _allowSkipCheckbox;
    private readonly CheckBox _allowSkipValueCheckbox;

    // --- 停留时长 (only when every file is Image/Document) ---
    private readonly bool _stayDurationApplicable;
    private readonly CheckBox? _stayDurationCheckbox;
    private readonly CheckBox? _stayDurationEnabledCheckbox;
    private readonly NumericUpDown? _stayDurationSeconds;

    // --- 淡入淡出 (only when every file is Video/Audio) ---
    private readonly bool _fadeApplicable;
    private readonly CheckBox? _fadeCheckbox;
    private readonly CheckBox? _fadeEnabledCheckbox;
    private readonly NumericUpDown? _fadeSeconds;

    // --- 背景音频叠加播放 (only when every file is Audio) ---
    private readonly bool _backgroundAudioApplicable;
    private readonly CheckBox? _backgroundAudioCheckbox;
    private readonly CheckBox? _backgroundAudioValueCheckbox;
    private readonly ComboBox? _backgroundAudioVisualCombo;

    public bool ChangePlayMode => _playModeCheckbox.Checked;
    public PlayMode? PlayModeValue => ((PlayModeComboItem)_playModeCombo.SelectedItem!).Value;

    public bool ChangeOnCompletion => _completionCheckbox.Checked;
    public CompletionAction OnCompletionValue => ((CompletionComboItem)_completionCombo.SelectedItem!).Value;

    public bool ChangeAllowManualSkip => _allowSkipCheckbox.Checked;
    public bool AllowManualSkipValue => _allowSkipValueCheckbox.Checked;

    public bool ChangeStayDuration => _stayDurationApplicable && _stayDurationCheckbox!.Checked;
    public TimeSpan? StayDurationValue =>
        _stayDurationEnabledCheckbox!.Checked ? TimeSpan.FromSeconds((double)_stayDurationSeconds!.Value) : null;

    public bool ChangeFade => _fadeApplicable && _fadeCheckbox!.Checked;
    public bool VolumeFollowsFadeValue => _fadeEnabledCheckbox!.Checked;
    public TimeSpan? FadeDurationValue =>
        _fadeEnabledCheckbox!.Checked ? TimeSpan.FromSeconds((double)_fadeSeconds!.Value) : null;

    public bool ChangeBackgroundAudio => _backgroundAudioApplicable && _backgroundAudioCheckbox!.Checked;
    public bool IsBackgroundAudioValue => _backgroundAudioValueCheckbox!.Checked;
    public AudioVisual BackgroundAudioVisualValue => ((VisualComboItem)_backgroundAudioVisualCombo!.SelectedItem!).Value;

    public BatchPropertiesDialog(IReadOnlyCollection<MediaFile> files)
    {
        _stayDurationApplicable = files.All(f => f.Kind is MediaKind.Image or MediaKind.Document);
        _fadeApplicable = files.All(f => f.Kind is MediaKind.Video or MediaKind.Audio);
        _backgroundAudioApplicable = files.All(f => f.Kind == MediaKind.Audio);

        Text = $"统一设置属性 — 已选中 {files.Count} 个文件";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;

        var controls = new List<Control>();
        int y = 12;

        var introLabel = new Label
        {
            Text = "只有勾选了\"修改此项\"的属性才会应用到全部选中文件；未勾选的属性保持每个\n文件各自原有的设置不变。",
            ForeColor = Color.DimGray,
            AutoSize = false,
            Bounds = new Rectangle(12, y, ContentWidth, 34),
        };
        controls.Add(introLabel);
        y += 42;

        // --- 播放方式：同 PlayModeDialog 的三选一语义，多出的第三项显式代表"改为跟随活动默认"
        // （PlayModeOverride = null），而不是像 PlayModeDialog 那样用另一个复选框控制 null ——
        // 这里外层"修改此项"复选框已经承担了同样的门控作用，不需要第二层。
        (_playModeCheckbox, _playModeCombo) = AddComboRow(
            controls, ref y, "修改此项：播放方式",
            combo =>
            {
                combo.Items.Add(new PlayModeComboItem(PlayMode.SequentialAuto, "顺序自动播放"));
                combo.Items.Add(new PlayModeComboItem(PlayMode.ManualSelect, "手动点选"));
                combo.Items.Add(new PlayModeComboItem(null, "跟随活动默认（不覆盖）"));
                combo.SelectedIndex = 0;
            });

        // --- 完成后动作：同 CompletionActionDialog 的三选一，没有 inherit 概念。
        (_completionCheckbox, _completionCombo) = AddComboRow(
            controls, ref y, "修改此项：完成后动作",
            combo =>
            {
                combo.Items.Add(new CompletionComboItem(CompletionAction.NextItem, "自动下一项"));
                combo.Items.Add(new CompletionComboItem(CompletionAction.Loop, "循环播放本文件"));
                combo.Items.Add(new CompletionComboItem(CompletionAction.HoldOnLastFrame, "停留等待（保持在最后一帧/最后画面）"));
                combo.SelectedIndex = 0;
            });

        // --- 允许手动切换：没有单独的单文件对话框（原本只在 PlayModeDialog 附近以外没有 UI），
        // 这里用一个"值"复选框代替下拉框，同 AudioPropertiesDialog 的 IsBackgroundAudio 复选框一样
        // 直接就是布尔值本身。
        _allowSkipCheckbox = new CheckBox
        {
            Text = "修改此项：允许手动切换",
            AutoSize = true,
            Bounds = new Rectangle(12, y, ContentWidth, 20),
        };
        _allowSkipValueCheckbox = new CheckBox
        {
            Text = "允许手动切换（上一项/下一项按钮可以跳到此文件）",
            AutoSize = true,
            Bounds = new Rectangle(32, y + 24, ContentWidth - 20, 20),
            Checked = true,
            Enabled = false,
        };
        _allowSkipCheckbox.CheckedChanged += (_, _) => _allowSkipValueCheckbox.Enabled = _allowSkipCheckbox.Checked;
        controls.Add(_allowSkipCheckbox);
        controls.Add(_allowSkipValueCheckbox);
        y += 56;

        // --- 停留时长：只在全部选中文件都是图片/文档时出现，语义同 StayDurationDialog
        // （内层复选框未勾选 = 显式设为 null，即跟随 设置 面板的全局默认）。
        if (_stayDurationApplicable)
        {
            _stayDurationCheckbox = new CheckBox
            {
                Text = "修改此项：停留时长（图片/文档）",
                AutoSize = true,
                Bounds = new Rectangle(12, y, ContentWidth, 20),
            };
            _stayDurationEnabledCheckbox = new CheckBox
            {
                Text = "为这些文件单独设置停留时长",
                AutoSize = true,
                Bounds = new Rectangle(32, y + 24, ContentWidth - 20, 20),
                Enabled = false,
            };
            _stayDurationSeconds = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 3600,
                Value = 10m,
                Location = new Point(52, y + 48),
                Width = 80,
                Enabled = false,
            };
            var stayDurationSecondsLabel = new Label { Text = "秒", AutoSize = true, Location = new Point(138, y + 51) };
            _stayDurationCheckbox.CheckedChanged += (_, _) => _stayDurationEnabledCheckbox.Enabled = _stayDurationCheckbox.Checked;
            _stayDurationEnabledCheckbox.CheckedChanged += (_, _) => _stayDurationSeconds.Enabled = _stayDurationEnabledCheckbox.Checked;
            controls.AddRange(new Control[] { _stayDurationCheckbox, _stayDurationEnabledCheckbox, _stayDurationSeconds, stayDurationSecondsLabel });
            y += 80;
        }

        // --- 淡入淡出：只在全部选中文件都是视频/音频时出现，语义同 FadeDialog
        // （内层复选框映射 VolumeFollowsFade，是否启用淡入淡出的总开关）。
        if (_fadeApplicable)
        {
            _fadeCheckbox = new CheckBox
            {
                Text = "修改此项：淡入淡出（视频/音频）",
                AutoSize = true,
                Bounds = new Rectangle(12, y, ContentWidth, 20),
            };
            _fadeEnabledCheckbox = new CheckBox
            {
                Text = "启用淡入淡出（音量随渐变）",
                AutoSize = true,
                Bounds = new Rectangle(32, y + 24, ContentWidth - 20, 20),
                Enabled = false,
            };
            _fadeSeconds = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 60,
                Value = 3m,
                Location = new Point(52, y + 48),
                Width = 80,
                Enabled = false,
            };
            var fadeSecondsLabel = new Label { Text = "秒", AutoSize = true, Location = new Point(138, y + 51) };
            _fadeCheckbox.CheckedChanged += (_, _) => _fadeEnabledCheckbox.Enabled = _fadeCheckbox.Checked;
            _fadeEnabledCheckbox.CheckedChanged += (_, _) => _fadeSeconds.Enabled = _fadeEnabledCheckbox.Checked;
            controls.AddRange(new Control[] { _fadeCheckbox, _fadeEnabledCheckbox, _fadeSeconds, fadeSecondsLabel });
            y += 80;
        }

        // --- 背景音频叠加播放：只在全部选中文件都是音频时出现，语义同 AudioPropertiesDialog。
        if (_backgroundAudioApplicable)
        {
            _backgroundAudioCheckbox = new CheckBox
            {
                Text = "修改此项：背景音频叠加播放",
                AutoSize = true,
                Bounds = new Rectangle(12, y, ContentWidth, 20),
            };
            _backgroundAudioValueCheckbox = new CheckBox
            {
                Text = "作为背景音频叠加播放（不占用活动顺序位）",
                AutoSize = true,
                Bounds = new Rectangle(32, y + 24, ContentWidth - 20, 20),
                Enabled = false,
            };
            _backgroundAudioVisualCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Bounds = new Rectangle(52, y + 48, ContentWidth - 40, 24),
                Enabled = false,
            };
            _backgroundAudioVisualCombo.Items.Add(new VisualComboItem(AudioVisual.Waveform, "波形动效（当前是占位：峰值电平表）"));
            _backgroundAudioVisualCombo.Items.Add(new VisualComboItem(AudioVisual.DefaultBackgroundImage, "默认背景图（当前是占位：纯色+文件名）"));
            _backgroundAudioVisualCombo.Items.Add(new VisualComboItem(AudioVisual.Black, "纯黑"));
            _backgroundAudioVisualCombo.SelectedIndex = 1;
            _backgroundAudioCheckbox.CheckedChanged += (_, _) => _backgroundAudioValueCheckbox.Enabled = _backgroundAudioCheckbox.Checked;
            _backgroundAudioValueCheckbox.CheckedChanged += (_, _) => _backgroundAudioVisualCombo.Enabled = _backgroundAudioValueCheckbox.Checked;
            controls.AddRange(new Control[] { _backgroundAudioCheckbox, _backgroundAudioValueCheckbox, _backgroundAudioVisualCombo });
            y += 80;
        }

        y += 8;
        var okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, Bounds = new Rectangle(Width - 12 - 80 - 88, y, 80, 28) };
        var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(Width - 12 - 80, y, 80, 28) };
        AcceptButton = okButton;
        CancelButton = cancelButton;
        controls.Add(okButton);
        controls.Add(cancelButton);
        y += 40;

        ClientSize = new Size(Width, y);
        Controls.AddRange(controls.ToArray());
    }

    /// <summary>Shared layout for the three always-applicable rows that are a plain "outer checkbox
    /// gates a combo box" (播放方式/完成后动作) — pulled out since those two rows are otherwise
    /// identical apart from which items <paramref name="populate"/> adds. The 允许手动切换/停留时长/
    /// 淡入淡出/背景音频 rows below have an extra nested checkbox layer instead of a bare combo, so
    /// they're built inline rather than through this helper.</summary>
    private static (CheckBox outerCheckbox, ComboBox combo) AddComboRow(
        List<Control> controls, ref int y, string checkboxText, Action<ComboBox> populate)
    {
        var outerCheckbox = new CheckBox
        {
            Text = checkboxText,
            AutoSize = true,
            Bounds = new Rectangle(12, y, ContentWidth, 20),
        };
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Bounds = new Rectangle(32, y + 24, ContentWidth - 20, 24),
            Enabled = false,
        };
        populate(combo);
        outerCheckbox.CheckedChanged += (_, _) => combo.Enabled = outerCheckbox.Checked;
        controls.Add(outerCheckbox);
        controls.Add(combo);
        y += 56;
        return (outerCheckbox, combo);
    }

    private sealed record PlayModeComboItem(PlayMode? Value, string Label)
    {
        public override string ToString() => Label; // what the ComboBox actually renders.
    }

    private sealed record CompletionComboItem(CompletionAction Value, string Label)
    {
        public override string ToString() => Label; // what the ComboBox actually renders.
    }

    private sealed record VisualComboItem(AudioVisual Value, string Label)
    {
        public override string ToString() => Label; // what the ComboBox actually renders.
    }
}
