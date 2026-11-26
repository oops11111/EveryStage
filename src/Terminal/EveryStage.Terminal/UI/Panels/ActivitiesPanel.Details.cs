using EveryStage.Terminal.Data;
using EveryStage.Terminal.Documents;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI.Panels;

public sealed partial class ActivitiesPanel
{
    // ---- L-02 详情面板 ---------------------------------------------------------------
    // Until this existed, every per-file playback property lived behind its own small modal
    // dialog (播放方式.../音频属性.../淡入淡出.../停留时长.../完成后动作..., one per menu item),
    // so checking or tweaking several properties of one file meant opening and closing five
    // dialogs in a row. This right-hand panel shows the SAME properties in place, for whatever
    // the (hidden) tree selection currently points at, and edits them through the very same
    // Apply* methods the dialogs end in (ActivitiesPanel.Editing.cs) — dialog, detail panel and
    // 统一设置属性 therefore share one save/log path and cannot drift apart. 文档播放设置 is the
    // deliberate exception: it is a multi-section editor of its own, so the panel only carries a
    // button that opens the existing dialog (same conclusion the L-02 review reached).
    //
    // The editors are persistent controls, created once in BuildDetailPanel and re-filled by
    // RefreshFileDetails on every selection change — not rebuilt per selection, so an edit in
    // flight (e.g. a NumericUpDown the operator is typing in) is never disposed under them.
    // RefreshFileDetails fills under _detailSyncing so the fill itself doesn't fire the commit
    // handlers; commits happen on real user changes only (combos/checkboxes immediately,
    // seconds boxes on Leave/Enter — committing on every ValueChanged tick would save to disk
    // and write a log line per spinner step). When an Apply* fails, TrySave has already rolled
    // the store back to disk and RefreshFileDetails is called again so the panel re-fills from
    // the surviving state instead of showing the rejected value.
    //
    // With no selection the panel stays visible and shows a placeholder line rather than
    // collapsing away: a docked panel appearing/disappearing would keep reflowing the activity
    // list's width, and a constant "属性" home teaches operators where properties live.
    //
    // FL-01a/FL-02 (2026-11-14): two presentation fixes on this same panel. A successful
    // commit shows a「✓ 已保存」line under the kind label for 2 s (the Apply* methods now
    // return whether they actually saved, so a no-change commit shows nothing — nothing was
    // saved); and the visible surface is a content-height card inside the full-height docked
    // strip (see UpdateDetailCardHeight), so the lower half of the strip no longer reads as
    // a tall empty slab. Neither changes what is saved or how — only what the operator sees.

    private readonly Panel _detailPanel = new();

    /// <summary>FL-02: the visible card inside <see cref="_detailPanel"/> — the panel itself
    /// stays a full-height docked strip (so the rows keep their 300 px), but only this card
    /// is painted as a surface; below it the strip shows the page background, so the dead
    /// blank that used to fill the lower half is gone. Its height follows the content
    /// (see <see cref="UpdateDetailCardHeight"/>).</summary>
    private readonly Panel _detailCard = new();

    /// <summary>FL-02: the scrolling body inside the card (was a BuildDetailPanel local).</summary>
    private readonly FlowLayoutPanel _detailBody = new();

    /// <summary>FL-01a: the 「✓ 已保存」 line shown after a successful detail-panel commit,
    /// hidden again after 2 s (see <see cref="_detailSavedTimer"/>) or when the selection
    /// changes / the next edit begins.</summary>
    private readonly Label _detailSavedLabel = new();

    /// <summary>FL-01a: hides the saved hint after 2 s. A WinForms timer, not a sleep — the
    /// UI never blocks while the hint is up. Same pattern as the header's _saveFeedbackTimer.</summary>
    private readonly System.Windows.Forms.Timer _detailSavedTimer = new() { Interval = 2000 };

    /// <summary>FL-01b: explains why the stay-duration editors are disabled for Office
    /// documents (no auto-advance timer, so the value would never take effect).</summary>
    private readonly Label _detailStayNote = new();

    /// <summary>AP-11: the one visible trace of the detail panel while it is auto-hidden on a
    /// narrow window — a small 「属性」 button at the right end of the column-heading strip.
    /// Clicking it forces the panel open as a floating overlay (see
    /// <see cref="_detailPanelForced"/>); created in <see cref="BuildDetailPanel"/> and shown /
    /// positioned exclusively by <see cref="UpdateDetailPanelVisibility"/>.</summary>
    private readonly Button _detailToggleButton = new();

    /// <summary>AP-11: true while the operator has forced the detail panel open as an overlay
    /// on a window too narrow for its docked form. Pure UI state — never persisted. The three
    /// visibility states and their transitions live in
    /// <see cref="UpdateDetailPanelVisibility"/>.</summary>
    private bool _detailPanelForced;

    private readonly Label _detailTitle = new();
    private readonly Label _detailKindLabel = new();
    private readonly Label _detailPlaceholder = new();

    private readonly TableLayoutPanel _detailActivityPlayModeGroup = new();
    private readonly ComboBox _detailActivityPlayModeCombo = new();

    private readonly TableLayoutPanel _detailFilePlayModeGroup = new();
    private readonly CheckBox _detailPlayModeOverrideCheck = new();
    private readonly ComboBox _detailPlayModeCombo = new();

    private readonly TableLayoutPanel _detailCompletionGroup = new();
    private readonly ComboBox _detailCompletionCombo = new();

    /// <summary>DEV-04(5): shown when 循环放映 makes the item never complete, so 播完后 can
    /// never fire — explains why the combo above it is disabled.</summary>
    private readonly Label _detailCompletionNote = new();

    private readonly TableLayoutPanel _detailManualSkipGroup = new();
    private readonly CheckBox _detailAllowManualSkipCheck = new();

    private readonly TableLayoutPanel _detailStayGroup = new();
    private readonly CheckBox _detailStayCheck = new();
    private readonly NumericUpDown _detailStaySeconds = new();

    private readonly TableLayoutPanel _detailFadeGroup = new();
    private readonly CheckBox _detailFadeCheck = new();
    private readonly NumericUpDown _detailFadeSeconds = new();
    private readonly Label _detailFadeNote = new();

    private readonly TableLayoutPanel _detailAudioGroup = new();
    private readonly CheckBox _detailBgAudioCheck = new();
    private readonly Label _detailBgAudioCaveat = new();
    private readonly ComboBox _detailAudioVisualCombo = new();

    private readonly TableLayoutPanel _detailDocumentGroup = new();
    private readonly Button _detailDocumentButton = new();

    /// <summary>DEV-04(1): the presentation fields, in place — the same five the
    /// 文档播放设置 dialog edits （放映方式/起止/每张停留/保留原稿转场/循环放映), plus the
    /// DEV-05 jump to the global unified slide transition. Visible for presentations only:
    /// PDF zoom and the Word open mode stay in the dialog. Built once, re-filled by
    /// RefreshFileDetails like every other group; hiding it never writes anything (the
    /// fill only ever reads <see cref="MediaFile.Document"/>).</summary>
    private readonly TableLayoutPanel _detailDocGroup = new();
    private readonly ComboBox _detailDocModeCombo = new();
    private readonly NumericUpDown _detailDocStartPage = new();
    private readonly CheckBox _detailDocHasEndCheck = new();
    private readonly NumericUpDown _detailDocEndPage = new();
    private readonly CheckBox _detailDocLoopCheck = new();
    private readonly CheckBox _detailDocKeepCheck = new();
    private readonly CheckBox _detailDocAutoCheck = new();
    private readonly NumericUpDown _detailDocAutoSeconds = new();
    private readonly Label _detailDocNote = new();
    private readonly Button _detailSlideTransitionButton = new();

    /// <summary>DEV-05: jumps to the existing global 统一翻页过场 setting (设置 · PPT 翻页
    /// 过场). Wired by the host window; null in standalone/test use.</summary>
    public Action? ShowTransitionSettingsRequested { get; set; }

    /// <summary>DEV-04(2): the item-entry transition (进入这一项的过场), in place — the same
    /// four values the row menu offers. Entry only: the override still means exactly what
    /// the menu's value means, no policy change.</summary>
    private readonly TableLayoutPanel _detailTransitionGroup = new();
    private readonly ComboBox _detailTransitionCombo = new();

    /// <summary>DEV-04(2): the segment-entry music cue (环节进场音乐), in place — the same
    /// options the row menu offers （不改变/淡出停止/per-playlist). The cue belongs to the
    /// activity (segment); the combo shows the selected file's activity's cue, or the
    /// selected activity's when no file is selected.</summary>
    private readonly TableLayoutPanel _detailMusicGroup = new();
    private readonly ComboBox _detailMusicCombo = new();

    /// <summary>DEV-03: the 播出保护 strip — shown only while the selected file is the on-air
    /// item or the next one (see <see cref="IsDetailProtected"/>). Its button is the one
    /// explicit, non-modal unlock; the strip itself lives inside the docked detail panel, so
    /// it can never cover the on-air/next rows.</summary>
    private readonly TableLayoutPanel _detailProtectGroup = new();
    private readonly Label _detailProtectLabel = new();
    private readonly Button _detailUnlockButton = new();

    /// <summary>DEV-03: the file the operator explicitly unlocked (null: none). Unlocking is
    /// per file and ends when the file is no longer selected, when it goes on air, or when
    /// the operator re-locks it — it is never persisted and never survives the item leaving
    /// the protected states.</summary>
    private MediaFile? _detailUnlockedFile;

    /// <summary>What the detail editors currently apply to — refreshed by
    /// <see cref="RefreshFileDetails"/> from <see cref="GetSelection"/> on every selection
    /// change, so a commit handler never acts on a stale object after a failed save reloaded
    /// the store (see <see cref="TrySave"/>).</summary>
    private Scenario? _detailScenario;
    private Activity? _detailActivity;
    private MediaFile? _detailFile;

    /// <summary>True while <see cref="RefreshFileDetails"/> is filling the editors — every
    /// commit handler checks it first, so programmatic fills don't apply/save anything.</summary>
    private bool _detailSyncing;

    private sealed record DetailItem<T>(T Value, string Label)
    {
        public override string ToString() => Label; // what the ComboBox actually renders.
    }

    /// <summary>Creates the detail panel and its editors. Called once from the constructor,
    /// which then adds <see cref="_detailPanel"/> to the layout (dock order lives there, next
    /// to the other panels').</summary>
    private void BuildDetailPanel()
    {
        _detailPanel.Dock = DockStyle.Right;
        _detailPanel.Width = LogicalToDeviceUnits(300); // P-03: DPI-scaled like every other width here
        // FL-02: the strip itself keeps the page background — the visible surface is the card
        // below, so the half of the strip the content doesn't reach blends into the page
        // instead of showing as a tall empty Surface slab.
        ModernUi.SetBackgroundRole(_detailPanel, UiBackgroundRole.Background);
        _detailPanel.Padding = new Padding(0);

        // FL-02: the card that actually looks like the panel — Surface, padded, top-docked
        // inside the strip, its height set by UpdateDetailCardHeight (content height, capped).
        _detailCard.Dock = DockStyle.Top;
        ModernUi.SetBackgroundRole(_detailCard, UiBackgroundRole.Surface);
        _detailCard.Padding = new Padding(12, 10, 12, 10);
        // AP-11: while the panel floats as a forced overlay it needs an edge against the rows
        // it covers — the docked form gets that edge from the layout itself, so the line is
        // only drawn in the forced state. (The line lives on the card now: the card IS the
        // visible surface in both states.)
        _detailCard.Paint += (_, e) =>
        {
            if (!_detailPanelForced) return;
            using var pen = new Pen(ModernUi.Border);
            e.Graphics.DrawLine(pen, 0, 0, 0, _detailCard.Height - 1);
        };
        _detailCard.Resize += (_, _) => LayoutDetailGroups();

        _detailTitle.Dock = DockStyle.Top;
        _detailTitle.Height = 24;
        _detailTitle.AutoEllipsis = true;
        _detailTitle.Font = new Font(ModernUi.UiFontSemibold, 12F);
        _detailTitle.Text = "属性";
        ModernUi.SetForegroundRole(_detailTitle, UiForegroundRole.Text);
        _detailKindLabel.Dock = DockStyle.Top;
        _detailKindLabel.Height = 18;
        _detailKindLabel.Font = new Font(ModernUi.UiFont, 9F);
        ModernUi.SetForegroundRole(_detailKindLabel, UiForegroundRole.Muted);

        // FL-01a: the saved hint, docked between the kind line and the body; hidden it takes
        // no space at all, shown it adds one 20 px line and the card grows with it.
        _detailSavedLabel.Dock = DockStyle.Top;
        _detailSavedLabel.Height = 20;
        _detailSavedLabel.Font = new Font(ModernUi.UiFont, 9F);
        _detailSavedLabel.Text = "✓ 已保存";
        _detailSavedLabel.Visible = false;
        ModernUi.SetForegroundRole(_detailSavedLabel, UiForegroundRole.Success);
        _detailSavedTimer.Tick += (_, _) => HideDetailSavedHint();

        _detailBody.Dock = DockStyle.Fill;
        _detailBody.FlowDirection = FlowDirection.TopDown;
        _detailBody.WrapContents = false;
        _detailBody.AutoScroll = true;
        _detailBody.BackColor = Color.Transparent;

        _detailPlaceholder.Text = "选中一个文件查看并修改它的播放属性";
        _detailPlaceholder.AutoSize = false;
        _detailPlaceholder.Width = 256;
        _detailPlaceholder.Height = 40;
        ModernUi.SetForegroundRole(_detailPlaceholder, UiForegroundRole.Muted);

        // --- 活动默认播放方式 (activity selected, no file) ---
        _detailActivityPlayModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _detailActivityPlayModeCombo.Dock = DockStyle.Fill;
        ModernUi.StyleComboBox(_detailActivityPlayModeCombo);
        FillPlayModeItems(_detailActivityPlayModeCombo);
        _detailActivityPlayModeCombo.SelectedIndexChanged += (_, _) => CommitDetailActivityPlayMode();
        ConfigureGroup(_detailActivityPlayModeGroup);
        _detailActivityPlayModeGroup.Controls.Add(PromptLabel("播放方式："));
        _detailActivityPlayModeGroup.Controls.Add(_detailActivityPlayModeCombo);

        // --- 文件播放方式 (override + combo, same pair as PlayModeDialog) ---
        _detailPlayModeOverrideCheck.Text = "覆盖活动默认播放方式";
        _detailPlayModeOverrideCheck.AutoSize = true;
        _detailPlayModeOverrideCheck.CheckedChanged += (_, _) => CommitDetailPlayMode();
        _detailPlayModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _detailPlayModeCombo.Dock = DockStyle.Fill;
        ModernUi.StyleComboBox(_detailPlayModeCombo);
        FillPlayModeItems(_detailPlayModeCombo);
        _detailPlayModeCombo.SelectedIndexChanged += (_, _) => CommitDetailPlayMode();
        ConfigureGroup(_detailFilePlayModeGroup);
        _detailFilePlayModeGroup.Controls.Add(_detailPlayModeOverrideCheck);
        _detailFilePlayModeGroup.Controls.Add(PromptLabel("播放方式："));
        _detailFilePlayModeGroup.Controls.Add(_detailPlayModeCombo);

        // --- 完成后动作 ---
        _detailCompletionCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _detailCompletionCombo.Dock = DockStyle.Fill;
        ModernUi.StyleComboBox(_detailCompletionCombo);
        _detailCompletionCombo.Items.Add(new DetailItem<CompletionAction>(CompletionAction.NextItem, "自动下一项"));
        _detailCompletionCombo.Items.Add(new DetailItem<CompletionAction>(CompletionAction.Loop, "循环播放本文件"));
        _detailCompletionCombo.Items.Add(new DetailItem<CompletionAction>(CompletionAction.HoldOnLastFrame, "停留等待（保持在最后一帧/最后画面）"));
        _detailCompletionCombo.Items.Add(new DetailItem<CompletionAction>(CompletionAction.BlackScreen, "播完切黑"));
        _detailCompletionCombo.Items.Add(new DetailItem<CompletionAction>(CompletionAction.MainKeyVisual, "播完回主 KV"));
        _detailCompletionCombo.SelectedIndexChanged += (_, _) => CommitDetailCompletionAction();
        _detailCompletionNote.AutoSize = false;
        _detailCompletionNote.Width = 256;
        _detailCompletionNote.Height = 30;
        ModernUi.SetForegroundRole(_detailCompletionNote, UiForegroundRole.Muted);
        _detailCompletionNote.Text = "循环放映时放映不会结束，播完后不执行；关闭循环放映或取消自动推进后恢复。";
        ConfigureGroup(_detailCompletionGroup);
        _detailCompletionGroup.Controls.Add(PromptLabel("播放完成后："));
        _detailCompletionGroup.Controls.Add(_detailCompletionCombo);
        _detailCompletionGroup.Controls.Add(_detailCompletionNote);

        // --- 允许手动切换 (was previously only reachable via 统一设置属性) ---
        _detailAllowManualSkipCheck.Text = "允许手动切换（上一项/下一项按钮可以跳到此文件）";
        _detailAllowManualSkipCheck.AutoSize = false;
        _detailAllowManualSkipCheck.Dock = DockStyle.Fill;
        _detailAllowManualSkipCheck.Height = 38;
        _detailAllowManualSkipCheck.CheckedChanged += (_, _) => CommitDetailAllowManualSkip();
        ConfigureGroup(_detailManualSkipGroup);
        _detailManualSkipGroup.Controls.Add(_detailAllowManualSkipCheck);

        // --- 停留时长 (same checkbox+seconds shape as StayDurationDialog) ---
        _detailStayCheck.Text = "为此文件单独设置停留时长";
        _detailStayCheck.AutoSize = true;
        _detailStayCheck.CheckedChanged += (_, _) =>
        {
            _detailStaySeconds.Enabled = _detailStayCheck.Checked;
            CommitDetailStayDuration();
        };
        ConfigureSeconds(_detailStaySeconds, 1, 3600);
        _detailStaySeconds.Leave += (_, _) => CommitDetailStayDuration();
        _detailStaySeconds.KeyDown += OnDetailSecondsKeyDown;
        _detailStaySeconds.Enter += (_, _) => HideDetailSavedHint(); // FL-01a: editing begins — clear the old hint
        // DEV-04(3): the old copy ("Office 文档不支持自动翻页，停留时长不生效") was wrong
        // for the static path — see PlayPdfAsync: with no 每张停留, static slides fall back
        // to the stay duration (or the settings default), and only the native show runs to
        // its own end. The note now says exactly which path uses what; it changes no value
        // and no playback behaviour.
        _detailStayNote.Text = "每张停留（文档播放设置）控制自动翻页；未设置时，静态幻灯片按停留时长执行播完后动作（未单独设置时用设置里的默认停留时长），原生放映播到放映结束。";
        _detailStayNote.AutoSize = false;
        _detailStayNote.Width = 256;
        _detailStayNote.Height = 30;
        ModernUi.SetForegroundRole(_detailStayNote, UiForegroundRole.Muted);
        ConfigureGroup(_detailStayGroup);
        _detailStayGroup.Controls.Add(_detailStayCheck);
        _detailStayGroup.Controls.Add(SecondsRow(_detailStaySeconds));
        _detailStayGroup.Controls.Add(_detailStayNote);

        // --- 淡入淡出 (same checkbox+seconds shape and caveat as FadeDialog) ---
        _detailFadeCheck.Text = "为此文件启用淡入淡出（音量随渐变）";
        _detailFadeCheck.AutoSize = true;
        _detailFadeCheck.CheckedChanged += (_, _) =>
        {
            _detailFadeSeconds.Enabled = _detailFadeCheck.Checked;
            CommitDetailFade();
        };
        ConfigureSeconds(_detailFadeSeconds, 1, 60);
        _detailFadeSeconds.Leave += (_, _) => CommitDetailFade();
        _detailFadeSeconds.KeyDown += OnDetailSecondsKeyDown;
        _detailFadeSeconds.Enter += (_, _) => HideDetailSavedHint(); // FL-01a: editing begins — clear the old hint
        // Short form of FadeDialog's Danger note — same fact (fade-out rests on an unverified
        // duration query), compressed to fit a sidebar line or two.
        _detailFadeNote.Text = "⚠ 淡出依赖尚未真机验证的时长查询，对某些文件可能不生效；淡入总是可靠的。";
        _detailFadeNote.AutoSize = false;
        _detailFadeNote.Width = 256;
        _detailFadeNote.Height = 32;
        ModernUi.SetForegroundRole(_detailFadeNote, UiForegroundRole.Danger);
        ConfigureGroup(_detailFadeGroup);
        _detailFadeGroup.Controls.Add(_detailFadeCheck);
        _detailFadeGroup.Controls.Add(SecondsRow(_detailFadeSeconds));
        _detailFadeGroup.Controls.Add(_detailFadeNote);

        // --- 音频属性 (same checkbox/caveat/combo texts as AudioPropertiesDialog) ---
        _detailBgAudioCheck.Text = "作为背景音频叠加播放（不占用活动顺序位）";
        _detailBgAudioCheck.AutoSize = false;
        _detailBgAudioCheck.Dock = DockStyle.Fill;
        _detailBgAudioCheck.Height = 38;
        _detailBgAudioCheck.CheckedChanged += (_, _) => CommitDetailAudioProperties();
        _detailBgAudioCaveat.Text = "ⓘ 背景音轨会独立叠加播放，不受悬浮预览窗\"暂停\"影响，也没有单独的音量调节";
        _detailBgAudioCaveat.AutoSize = false;
        _detailBgAudioCaveat.Width = 256;
        _detailBgAudioCaveat.Height = 30;
        ModernUi.SetForegroundRole(_detailBgAudioCaveat, UiForegroundRole.Muted);
        _detailAudioVisualCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _detailAudioVisualCombo.Dock = DockStyle.Fill;
        ModernUi.StyleComboBox(_detailAudioVisualCombo);
        _detailAudioVisualCombo.Items.Add(new DetailItem<AudioVisual>(AudioVisual.Waveform, "波形动效（当前是占位：峰值电平表）"));
        _detailAudioVisualCombo.Items.Add(new DetailItem<AudioVisual>(AudioVisual.DefaultBackgroundImage, "默认背景图（当前是占位：纯色+文件名）"));
        _detailAudioVisualCombo.Items.Add(new DetailItem<AudioVisual>(AudioVisual.Black, "纯黑"));
        _detailAudioVisualCombo.SelectedIndexChanged += (_, _) => CommitDetailAudioProperties();
        ConfigureGroup(_detailAudioGroup);
        _detailAudioGroup.Controls.Add(_detailBgAudioCheck);
        _detailAudioGroup.Controls.Add(_detailBgAudioCaveat);
        _detailAudioGroup.Controls.Add(PromptLabel("投屏显示内容："));
        _detailAudioGroup.Controls.Add(_detailAudioVisualCombo);

        // --- 文档播放设置 stays a dialog (see the L-02 note at the top of this file) ---
        _detailDocumentButton.Text = "文档播放设置...";
        _detailDocumentButton.Dock = DockStyle.Fill;
        _detailDocumentButton.Height = 30;
        ModernUi.StyleButton(_detailDocumentButton);
        _detailDocumentButton.Click += (_, _) => OnEditDocumentSettings();
        ConfigureGroup(_detailDocumentGroup);
        _detailDocumentGroup.Controls.Add(_detailDocumentButton);

        // --- DEV-03 播出保护: lock strip + explicit unlock, first in the body so it is
        // visible without scrolling when it applies. The editors it gates are disabled by
        // ApplyDetailProtection, never hidden — the stored values stay on screen.
        _detailProtectLabel.AutoSize = false;
        _detailProtectLabel.Width = 256;
        _detailProtectLabel.Height = 30;
        ModernUi.SetForegroundRole(_detailProtectLabel, UiForegroundRole.Warning);
        _detailUnlockButton.Dock = DockStyle.Fill;
        _detailUnlockButton.Height = 30;
        ModernUi.StyleButton(_detailUnlockButton);
        _detailUnlockButton.Click += (_, _) => OnDetailProtectButtonClick();
        ConfigureGroup(_detailProtectGroup);
        _detailProtectGroup.Controls.Add(_detailProtectLabel);
        _detailProtectGroup.Controls.Add(_detailUnlockButton);

        // --- DEV-04(1) 演示文稿字段 (in place; the dialog keeps PDF zoom / Word open mode) ---
        _detailDocModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _detailDocModeCombo.Dock = DockStyle.Fill;
        ModernUi.StyleComboBox(_detailDocModeCombo);
        _detailDocModeCombo.Items.Add(new DetailItem<PresentationMode>(PresentationMode.Auto, "自动（有动画/转场/媒体时原生放映）"));
        _detailDocModeCombo.Items.Add(new DetailItem<PresentationMode>(PresentationMode.Native, "原生放映"));
        _detailDocModeCombo.Items.Add(new DetailItem<PresentationMode>(PresentationMode.Static, "静态幻灯片"));
        _detailDocModeCombo.SelectedIndexChanged += (_, _) => CommitDetailDocument(s => s.PresentationMode = SelectedDocMode());
        ConfigureSeconds(_detailDocStartPage, 1, 10000);
        _detailDocStartPage.Leave += (_, _) => CommitDetailDocument(s => s.StartPage = (int)_detailDocStartPage.Value);
        _detailDocStartPage.KeyDown += OnDetailSecondsKeyDown;
        _detailDocStartPage.Enter += (_, _) => HideDetailSavedHint();
        _detailDocHasEndCheck.Text = "指定结束幻灯片";
        _detailDocHasEndCheck.AutoSize = true;
        _detailDocHasEndCheck.CheckedChanged += (_, _) =>
        {
            _detailDocEndPage.Enabled = _detailDocHasEndCheck.Checked;
            CommitDetailDocument(s => s.EndPage = _detailDocHasEndCheck.Checked ? (int)_detailDocEndPage.Value : null);
        };
        ConfigureSeconds(_detailDocEndPage, 1, 100000);
        _detailDocEndPage.Leave += (_, _) => CommitDetailDocument(s => s.EndPage = _detailDocHasEndCheck.Checked ? (int)_detailDocEndPage.Value : null);
        _detailDocEndPage.KeyDown += OnDetailSecondsKeyDown;
        _detailDocEndPage.Enter += (_, _) => HideDetailSavedHint();
        _detailDocLoopCheck.Text = "循环放映";
        _detailDocLoopCheck.AutoSize = true;
        _detailDocLoopCheck.CheckedChanged += (_, _) => CommitDetailDocument(s => s.Loop = _detailDocLoopCheck.Checked);
        _detailDocKeepCheck.Text = "保留原稿转场（不用设置里的统一过场）";
        _detailDocKeepCheck.AutoSize = false;
        _detailDocKeepCheck.Dock = DockStyle.Fill;
        _detailDocKeepCheck.Height = 38;
        _detailDocKeepCheck.CheckedChanged += (_, _) => CommitDetailDocument(s => s.KeepOriginalTransitions = _detailDocKeepCheck.Checked);
        _detailDocAutoCheck.Text = "自动推进（每张停留）";
        _detailDocAutoCheck.AutoSize = true;
        _detailDocAutoCheck.CheckedChanged += (_, _) =>
        {
            _detailDocAutoSeconds.Enabled = _detailDocAutoCheck.Checked;
            CommitDetailDocument(s => s.SecondsPerPage = _detailDocAutoCheck.Checked ? (double)_detailDocAutoSeconds.Value : null);
        };
        ConfigureSeconds(_detailDocAutoSeconds, 1, 3600);
        _detailDocAutoSeconds.Leave += (_, _) => CommitDetailDocument(s => s.SecondsPerPage = _detailDocAutoCheck.Checked ? (double)_detailDocAutoSeconds.Value : null);
        _detailDocAutoSeconds.KeyDown += OnDetailSecondsKeyDown;
        _detailDocAutoSeconds.Enter += (_, _) => HideDetailSavedHint();
        _detailDocNote.AutoSize = false;
        _detailDocNote.Width = 256;
        _detailDocNote.Height = 30;
        ModernUi.SetForegroundRole(_detailDocNote, UiForegroundRole.Muted);
        _detailDocNote.Text = "静态幻灯片不保留动画、转场和嵌入音视频；未安装 PowerPoint/WPS 时自动使用静态模式。";
        _detailSlideTransitionButton.Text = "统一翻页过场设置...";
        _detailSlideTransitionButton.Dock = DockStyle.Fill;
        _detailSlideTransitionButton.Height = 30;
        ModernUi.StyleButton(_detailSlideTransitionButton);
        _detailSlideTransitionButton.Click += (_, _) => ShowTransitionSettingsRequested?.Invoke();
        ConfigureGroup(_detailDocGroup);
        _detailDocGroup.Controls.Add(PromptLabel("放映方式："));
        _detailDocGroup.Controls.Add(_detailDocModeCombo);
        _detailDocGroup.Controls.Add(PromptLabel("起始幻灯片："));
        _detailDocGroup.Controls.Add(_detailDocStartPage);
        _detailDocGroup.Controls.Add(_detailDocHasEndCheck);
        _detailDocGroup.Controls.Add(_detailDocEndPage);
        _detailDocGroup.Controls.Add(_detailDocLoopCheck);
        _detailDocGroup.Controls.Add(_detailDocKeepCheck);
        _detailDocGroup.Controls.Add(_detailDocAutoCheck);
        _detailDocGroup.Controls.Add(SecondsRow(_detailDocAutoSeconds));
        _detailDocGroup.Controls.Add(_detailDocNote);
        _detailDocGroup.Controls.Add(_detailSlideTransitionButton);

        // --- DEV-04(2) 进入这一项的过场 (in place; same values as the row menu) ---
        _detailTransitionCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _detailTransitionCombo.Dock = DockStyle.Fill;
        ModernUi.StyleComboBox(_detailTransitionCombo);
        _detailTransitionCombo.Items.Add(new DetailItem<TransitionStyle?>(null, "默认（设置里的大屏过场）"));
        _detailTransitionCombo.Items.Add(new DetailItem<TransitionStyle?>(TransitionStyle.Cut, "直切"));
        _detailTransitionCombo.Items.Add(new DetailItem<TransitionStyle?>(TransitionStyle.Dissolve, "叠化"));
        _detailTransitionCombo.Items.Add(new DetailItem<TransitionStyle?>(TransitionStyle.DipToBlack, "经黑场"));
        _detailTransitionCombo.SelectedIndexChanged += (_, _) => CommitDetailTransition();
        ConfigureGroup(_detailTransitionGroup);
        _detailTransitionGroup.Controls.Add(PromptLabel("进入这一项的过场："));
        _detailTransitionGroup.Controls.Add(_detailTransitionCombo);

        // --- DEV-04(2) 环节进场音乐 (in place; same options as the row menu) ---
        _detailMusicCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _detailMusicCombo.Dock = DockStyle.Fill;
        ModernUi.StyleComboBox(_detailMusicCombo);
        _detailMusicCombo.SelectedIndexChanged += (_, _) => CommitDetailMusicCue();
        ConfigureGroup(_detailMusicGroup);
        _detailMusicGroup.Controls.Add(PromptLabel("环节进场音乐："));
        _detailMusicGroup.Controls.Add(_detailMusicCombo);

        _detailBody.Controls.AddRange(new Control[]
        {
            _detailPlaceholder,
            _detailProtectGroup,
            _detailActivityPlayModeGroup, _detailFilePlayModeGroup, _detailCompletionGroup,
            _detailManualSkipGroup, _detailStayGroup, _detailFadeGroup, _detailAudioGroup,
            _detailDocumentGroup, _detailDocGroup, _detailTransitionGroup, _detailMusicGroup,
        });
        // Dock order inside the card: Fill body added first, Top title/kind/saved-hint last,
        // so the title owns the top strip and the body fills what remains below it.
        _detailCard.Controls.Add(_detailBody);
        _detailCard.Controls.Add(_detailKindLabel);
        _detailCard.Controls.Add(_detailSavedLabel);
        _detailCard.Controls.Add(_detailTitle);
        _detailPanel.Controls.Add(_detailCard);

        // AP-11: the narrow-window entry point. It belongs to no docked strip — the
        // column-heading strip is a constructor local in ActivitiesPanel.cs — so it is added
        // to the panel itself and floated at that strip's right end by
        // UpdateDetailPanelVisibility (Anchor Top|Right keeps it glued to the right edge
        // between the explicit repositions on every Resize). It starts hidden: the wide,
        // docked state is the default and needs no entry point.
        _detailToggleButton.Text = "属性";
        _detailToggleButton.Width = LogicalToDeviceUnits(56);
        _detailToggleButton.Height = LogicalToDeviceUnits(22);
        _detailToggleButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _detailToggleButton.Visible = false;
        ModernUi.StyleButton(_detailToggleButton);
        _detailToggleButton.Click += (_, _) => SetDetailPanelForced(!_detailPanelForced);
        _toolTip.SetToolTip(_detailToggleButton, "窗口太窄放不下属性面板时，点此以浮层打开");
        Controls.Add(_detailToggleButton);

        // OP-22: no Esc-to-close handler here anymore. Esc is owned by the form (MainWindow.
        // ProcessCmdKey): in a text box it yields to the box, everywhere else it stops output.
        // A handler that closed the overlay first would have made Esc do two different things
        // depending on which control had focus, and it was only ever reachable through a text
        // box's focus anyway. The overlay closes via the toggle button.

        // P-01: visibility is managed separately from content — RefreshFileDetails fills the
        // editors and never touches Visible; UpdateDetailPanelVisibility (below) only decides
        // whether the docked panel is shown at all, and never touches the editors.
        Resize += (_, _) => UpdateDetailPanelVisibility();
        UpdateDetailPanelVisibility();

        RefreshFileDetails();
    }

    /// <summary>Auto-collapses the detail panel on narrow windows (P-01). The activity rows
    /// have a minimum layout width of 320 logical px (see <see cref="LayoutRows"/>); if showing
    /// the 300-wide detail panel would leave the rows less than 340 logical px (the 320 minimum
    /// plus a small margin), the panel hides and the rows take the full width back. When the
    /// window widens again the panel reappears for whatever the current selection is — the
    /// editors were kept filled by <see cref="RefreshFileDetails"/> the whole time.
    ///
    /// AP-11 adds a third state to that rule, because an auto-hidden panel with no trace left
    /// operators thinking the properties were gone. The state machine, all of it driven from
    /// here (on every panel Resize and on every state change):
    /// - docked: wide enough — panel docked right, toggle hidden;
    /// - auto-hidden: too narrow and not forced — panel hidden, toggle shown (the only trace);
    /// - forced overlay: too narrow but the operator clicked the toggle — the SAME panel
    ///   control undocked and pinned over the rows' right edge (editors untouched, so nothing
    ///   about editing differs), toggle still shown so it can close it again.
    /// Forced ends by clicking the toggle again, by Esc inside the panel (see BuildDetailPanel),
    /// or by the window widening back into the docked state, which is checked first below.
    /// FL-02: in both states the panel is only as tall as its content (see
    /// <see cref="UpdateDetailCardHeight"/>) — the docked form keeps its full-height strip
    /// for layout, but only the card is painted, and the forced overlay is sized to the card
    /// instead of the old full-height slab.</summary>
    private void UpdateDetailPanelVisibility()
    {
        bool fits = ClientSize.Width - _detailPanel.Width >= LogicalToDeviceUnits(340);
        if (_detailPanelForced && fits) SetDetailPanelForced(false); // widened: back to docked
        UpdateDetailCardHeight();
        if (_detailPanelForced)
        {
            int width = Math.Min(_detailPanel.Width, ClientSize.Width);
            _detailPanel.Bounds = new Rectangle(
                ClientSize.Width - width, _activityRows.Top, width, _detailCard.Height);
            _detailPanel.Visible = true;
            _detailPanel.BringToFront();
        }
        else
        {
            _detailPanel.Visible = fits;
        }
        _detailToggleButton.Visible = _detailPanelForced || !fits;
        if (_detailToggleButton.Visible) PositionDetailToggleButton();
    }

    /// <summary>FL-02: sizes the visible card to its content. The height is the title/kind/
    /// saved-hint chrome plus the body's laid-out content height, capped at 70% of the page
    /// height (a 900-high window caps at ~630 logical px, about the 640 the FL-02 review
    /// named); beyond the cap the body scrolls internally (it is a FlowLayoutPanel with
    /// AutoScroll) instead of the page growing a scrollbar. The docked strip keeps its full
    /// height — only the card shrinks — so the rows' width never changes; the forced overlay
    /// (which has no strip to hide behind) is resized to the card here too.</summary>
    private void UpdateDetailCardHeight()
    {
        LayoutDetailGroups(); // widths + wrapped note heights first: the measurement below reads them
        int cap = Math.Max(LogicalToDeviceUnits(200), ClientSize.Height * 70 / 100);
        int chrome = _detailTitle.Height
            + (_detailKindLabel.Visible ? _detailKindLabel.Height : 0)
            + (_detailSavedLabel.Visible ? _detailSavedLabel.Height : 0)
            + _detailCard.Padding.Vertical;
        int bodyHeight = Math.Min(MeasureDetailBodyContentHeight(), Math.Max(0, cap - chrome));
        _detailCard.Height = chrome + bodyHeight;
        if (_detailPanelForced) _detailPanel.Height = _detailCard.Height;
    }

    /// <summary>U1-11: the groups and the note labels follow the card's width instead of the
    /// fixed 256 px they were built with — at high DPI the panel is wider than 256 and the
    /// notes wrapped at the wrong width (or clipped). The notes wrap to their measured
    /// height (AutoSize + MaximumSize), so the group's AutoSize height follows the text.
    /// Called from <see cref="UpdateDetailCardHeight"/> (before it measures) and from the
    /// card's Resize; a no-op when the card has no width yet (BuildDetailPanel time).</summary>
    private void LayoutDetailGroups()
    {
        int contentWidth = _detailCard.ClientSize.Width - _detailCard.Padding.Horizontal;
        if (contentWidth <= 0) return;
        foreach (var group in new[]
        {
            _detailProtectGroup,
            _detailActivityPlayModeGroup, _detailFilePlayModeGroup, _detailCompletionGroup,
            _detailManualSkipGroup, _detailStayGroup, _detailFadeGroup, _detailAudioGroup,
            _detailDocumentGroup, _detailDocGroup, _detailTransitionGroup, _detailMusicGroup,
        })
            group.Width = contentWidth;
        LayoutDetailNote(_detailProtectLabel, contentWidth);
        LayoutDetailNote(_detailCompletionNote, contentWidth);
        LayoutDetailNote(_detailDocNote, contentWidth);
        LayoutDetailNote(_detailStayNote, contentWidth);
        LayoutDetailNote(_detailFadeNote, contentWidth);
        LayoutDetailNote(_detailBgAudioCaveat, contentWidth);
        LayoutDetailNote(_detailPlaceholder, contentWidth);
    }

    private static void LayoutDetailNote(Label note, int width)
    {
        note.MaximumSize = new Size(width, 0);
        note.AutoSize = true;
    }

    /// <summary>FL-02: the body's content height — the bottom edge of its lowest visible
    /// child after a layout pass, so margin collapsing (or not) never has to be guessed at.</summary>
    private int MeasureDetailBodyContentHeight()
    {
        _detailBody.PerformLayout();
        int bottom = 0;
        foreach (Control child in _detailBody.Controls)
        {
            if (!child.Visible) continue;
            bottom = Math.Max(bottom, child.Bottom + child.Margin.Bottom);
        }
        return bottom;
    }

    /// <summary>FL-01a: shows the 「✓ 已保存」 hint after a successful commit and arms the
    /// 2 s timer that hides it again. Called only when the Apply* actually saved — a
    /// no-change commit shows nothing, because nothing was saved. When the edited file is
    /// the one on air, the hint says the edit applies next time it airs (OP-02): the engine
    /// snapshots the stay duration, fade and document timing at Take, so editing the airing
    /// item cannot change the current播出 — the note says so instead of pretending otherwise,
    /// and no timer/fade is re-armed live.</summary>
    private void ShowDetailSavedHint(bool appliesNextTime = false)
    {
        _detailSavedLabel.Text = appliesNextTime ? "✓ 已保存 · 下次播出时生效" : "✓ 已保存";
        _detailSavedLabel.Visible = true;
        _detailSavedTimer.Stop();
        _detailSavedTimer.Start();
        UpdateDetailCardHeight();
    }

    /// <summary>FL-01a: clears the saved hint (selection change, next edit beginning, timer
    /// expiry). A no-op when it is already hidden, so RefreshFileDetails can call it
    /// unconditionally. Stops the timer first even on a disposed panel, so a form closed
    /// inside the 2 s window never fires back into disposed controls.</summary>
    private void HideDetailSavedHint()
    {
        _detailSavedTimer.Stop();
        if (IsDisposed || !_detailSavedLabel.Visible) return;
        _detailSavedLabel.Visible = false;
        UpdateDetailCardHeight();
    }

    /// <summary>AP-11: enters/leaves the forced-overlay state. Forcing undocks the panel;
    /// <see cref="UpdateDetailPanelVisibility"/> then pins it over the rows and keeps it there
    /// across resizes until the state ends. Unforcing re-docks it and hands visibility back to
    /// the width rule in the same call.</summary>
    private void SetDetailPanelForced(bool forced)
    {
        _detailPanelForced = forced;
        _detailPanel.Dock = forced ? DockStyle.None : DockStyle.Right;
        UpdateDetailPanelVisibility();
    }

    /// <summary>AP-11: keeps the 「属性」 toggle at the right end of the column-heading strip.
    /// The strip panel is a constructor local (ActivitiesPanel.cs), so its geometry is derived
    /// from the rows host instead: the strip occupies the 26 logical px directly above
    /// <see cref="_activityRows"/>, whose live Top already reflects the header above it.</summary>
    private void PositionDetailToggleButton()
    {
        if (_activityRows.Top <= 0) return; // not laid out yet — the first real Resize repositions
        int stripHeight = LogicalToDeviceUnits(26);
        _detailToggleButton.Location = new Point(
            ClientSize.Width - _detailToggleButton.Width - LogicalToDeviceUnits(8),
            _activityRows.Top - stripHeight + (stripHeight - _detailToggleButton.Height) / 2);
        _detailToggleButton.BringToFront();
    }

    private static void ConfigureGroup(TableLayoutPanel group)
    {
        group.AutoSize = true;
        group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        group.ColumnCount = 1;
        group.Width = 256;
        group.Margin = new Padding(0, 0, 0, 12);
        group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    }

    private static Label PromptLabel(string text)
    {
        var label = new Label { Text = text, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        ModernUi.SetForegroundRole(label, UiForegroundRole.Muted);
        return label;
    }

    private static void FillPlayModeItems(ComboBox combo)
    {
        combo.Items.Add(new DetailItem<PlayMode>(PlayMode.SequentialAuto, "顺序自动播放"));
        combo.Items.Add(new DetailItem<PlayMode>(PlayMode.ManualSelect, "手动点选"));
    }

    private static void ConfigureSeconds(NumericUpDown upDown, decimal minimum, decimal maximum)
    {
        upDown.Minimum = minimum;
        upDown.Maximum = maximum;
        upDown.Width = 80;
    }

    private static FlowLayoutPanel SecondsRow(NumericUpDown upDown)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(16, 2, 0, 0),
        };
        row.Controls.Add(upDown);
        row.Controls.Add(new Label { Text = "秒", AutoSize = true, Margin = new Padding(4, 4, 0, 0) });
        return row;
    }

    private static void SelectDetailItem<T>(ComboBox combo, T value)
    {
        foreach (var item in combo.Items)
            if (item is DetailItem<T> detailItem && EqualityComparer<T>.Default.Equals(detailItem.Value, value))
            {
                combo.SelectedItem = item;
                return;
            }
    }

    /// <summary>Re-fills the detail panel from the current selection. Called from the tree's
    /// AfterSelect (alongside UpdateButtonStates/RefreshCardStyles) and again after every
    /// detail-panel commit, so a value <see cref="TrySave"/> rejected is re-read from the store
    /// instead of lingering on screen. Because <see cref="RefreshTree"/> rebuilds the nodes and
    /// re-selects by Tag, edits that refresh the tree (audio properties) also land back here via
    /// AfterSelect.</summary>
    private void RefreshFileDetails()
    {
        HideDetailSavedHint(); // FL-01a: selection changed — clear the old hint.
        var (scenario, activity, file) = GetSelection();
        _detailScenario = scenario;
        _detailActivity = activity;
        _detailFile = file;
        // DEV-03: an unlock belongs to the file it was granted for — selecting anything
        // else ends it (commits for the same file below keep it).
        if (_detailUnlockedFile != null && !ReferenceEquals(_detailUnlockedFile, file)) _detailUnlockedFile = null;

        _detailSyncing = true;
        try
        {
            _detailPlaceholder.Visible = activity == null;
            _detailTitle.Text = file != null ? Path.GetFileName(file.SourcePath) : activity?.Name ?? "属性";
            _detailKindLabel.Text = file != null ? DetailKindText(file) : activity != null ? "环节" : "";
            _detailKindLabel.Visible = activity != null;

            _detailActivityPlayModeGroup.Visible = file == null && activity != null;
            if (activity != null) SelectDetailItem(_detailActivityPlayModeCombo, activity.DefaultPlayMode);

            bool hasFile = file != null;
            _detailFilePlayModeGroup.Visible = hasFile;
            _detailCompletionGroup.Visible = hasFile;
            _detailManualSkipGroup.Visible = hasFile;
            bool stayApplicable = file != null && IsStayDurationApplicable(file);
            // FL-01b: Office documents keep the group (disabled, with the note) instead of
            // having it hidden; video/audio keep the old hidden behaviour.
            bool isOfficeDocument = file != null && file.Kind == MediaKind.Document && !stayApplicable;
            _detailStayGroup.Visible = stayApplicable || isOfficeDocument;
            _detailStayNote.Visible = isOfficeDocument;
            _detailFadeGroup.Visible = file != null && file.Kind is MediaKind.Video or MediaKind.Audio;
            _detailAudioGroup.Visible = file != null && file.Kind == MediaKind.Audio;
            _detailDocumentGroup.Visible = file != null && file.Kind == MediaKind.Document;
            // DEV-04(1): the in-place presentation fields only; other documents keep the
            // dialog button above. DEV-04(2): transition for any file, music cue for the
            // selected file's activity (or the selected activity itself).
            bool isPresentation = file != null && file.Kind == MediaKind.Document
                && DocumentFormats.Of(file.SourcePath) == DocumentFormat.Presentation;
            _detailDocGroup.Visible = isPresentation;
            _detailTransitionGroup.Visible = hasFile;
            _detailMusicGroup.Visible = activity != null;
            // Filled here, not below: with an activity (but no file) selected the fill
            // below returns early, and the visible music combo must not show the previous
            // selection's cue.
            FillDetailMusicCombo(activity);
            if (file == null) return;

            _detailPlayModeOverrideCheck.Checked = file.PlayModeOverride.HasValue;
            SelectDetailItem(_detailPlayModeCombo, file.PlayModeOverride ?? PlayMode.SequentialAuto);
            _detailPlayModeCombo.Enabled = file.PlayModeOverride.HasValue;

            SelectDetailItem(_detailCompletionCombo, file.OnCompletion);
            _detailAllowManualSkipCheck.Checked = file.AllowManualSkip;

            _detailStayCheck.Checked = file.StayDuration.HasValue;
            _detailStayCheck.Enabled = stayApplicable; // FL-01b: Office docs show the stored value but can't edit it
            // Same preselect defaults the dialogs use when the nullable value is unset
            // (StayDurationDialog: 10 s, FadeDialog: 3 s), clamped to each box's own range.
            _detailStaySeconds.Value = Math.Clamp((decimal)(file.StayDuration ?? TimeSpan.FromSeconds(10)).TotalSeconds, 1m, 3600m);
            _detailStaySeconds.Enabled = stayApplicable && file.StayDuration.HasValue;

            _detailFadeCheck.Checked = file.VolumeFollowsFade;
            _detailFadeSeconds.Value = Math.Clamp((decimal)(file.FadeDuration ?? TimeSpan.FromSeconds(3)).TotalSeconds, 1m, 60m);
            _detailFadeSeconds.Enabled = file.VolumeFollowsFade;

            _detailBgAudioCheck.Checked = file.IsBackgroundAudio;
            _detailBgAudioCaveat.Visible = file.IsBackgroundAudio;
            SelectDetailItem(_detailAudioVisualCombo, file.BackgroundAudioVisual);

            // DEV-04(1): read the stored settings — or a fresh defaults object when the
            // file has none — without writing anything back. A file with no Document keeps
            // showing defaults until the operator changes a control (the first commit then
            // creates and saves the settings object).
            var doc = file.Document ?? new DocumentPlaybackSettings();
            SelectDetailItem(_detailDocModeCombo, doc.PresentationMode);
            _detailDocStartPage.Value = Math.Clamp((decimal)doc.StartPage, 1m, 10000m);
            _detailDocHasEndCheck.Checked = doc.EndPage.HasValue;
            _detailDocEndPage.Value = Math.Clamp((decimal)(doc.EndPage ?? 1), 1m, 100000m);
            _detailDocEndPage.Enabled = doc.EndPage.HasValue;
            _detailDocLoopCheck.Checked = doc.Loop;
            _detailDocKeepCheck.Checked = doc.KeepOriginalTransitions;
            _detailDocAutoCheck.Checked = doc.SecondsPerPage is > 0;
            _detailDocAutoSeconds.Value = Math.Clamp((decimal)(doc.SecondsPerPage ?? 10), 1m, 3600m);
            _detailDocAutoSeconds.Enabled = doc.SecondsPerPage is > 0;

            SelectDetailItem(_detailTransitionCombo, file.TransitionOverride);
        }
        finally
        {
            _detailSyncing = false;
            // DEV-03: protection is re-evaluated on every fill (selection change, commit,
            // re-lock) — the early return above must not skip it either.
            ApplyDetailProtection();
            // FL-02: group visibility changed the content height — here, not after the
            // try/finally, so the file == null early return above doesn't skip it.
            UpdateDetailCardHeight();
        }
    }

    /// <summary>DEV-03: true while <paramref name="file"/> is the item on the big screen or
    /// the next one within the live section, during local-media output. Extends beyond
    /// <see cref="ProgramIsOnAir"/> (which sees only the live item) to the next item, whose
    /// attrs would otherwise be editable right up to the moment it goes on air. With
    /// nothing on air — or a device cast holding Program — nothing is protected: preview
    /// browsing alone never locks the panel.</summary>
    private bool IsDetailProtected(MediaFile file)
    {
        if (_stateMachine.State != OutputState.Active || _stateMachine.Source != ProgramSource.LocalMedia) return false;
        var order = RunOrder();
        if (LivePosition(order) < 0) return false;
        if (ProgramIsOnAir(file)) return true;
        int next = NextPosition(order);
        return next >= 0 && ReferenceEquals(order[next].Activity.Files[order[next].Index], file);
    }

    /// <summary>DEV-03: protected and not explicitly unlocked — the dangerous editors must
    /// refuse writes (see the commit guards), not just look disabled.</summary>
    private bool DetailEditLocked(MediaFile file) => IsDetailProtected(file) && !ReferenceEquals(_detailUnlockedFile, file);

    /// <summary>DEV-03: applies the lock to every dangerous editor and shows/hides the
    /// protect strip. Called on every fill and on every playback/state change without a
    /// selection change (<see cref="RefreshDetailProtection"/>) — enablement is always
    /// recomputed from the live run state, never cached. Non-dangerous editors (play mode,
    /// manual skip, audio properties) are deliberately untouched.</summary>
    private void ApplyDetailProtection()
    {
        var file = _detailFile;
        bool prot = file != null && IsDetailProtected(file);
        bool locked = prot && !ReferenceEquals(_detailUnlockedFile, file);
        _detailProtectGroup.Visible = prot;
        if (prot)
        {
            // DEV-03: no effect-timing promise in any of these wordings. Whether an unlocked
            // edit affects the current airing depends on the attribute (HandleCompletion
            // re-reads 播完后 live; stay/fade/document values are read at play time), so the
            // strip states the lock and the re-lock rule only — never "下次播出时生效" as
            // if it were the protection.
            _detailProtectLabel.Text = locked
                ? ProgramIsOnAir(file!) ? "正在大屏播出：播完后、停留、淡入淡出已锁定。"
                    : "下一项：播完后、停留、淡入淡出已锁定，开始播出时自动重新锁定。"
                : ProgramIsOnAir(file!) ? "已解锁编辑：改动立即保存（播完后改动会影响本次播出）。"
                    : "已解锁编辑：此项开始播出时自动重新锁定，未提交的修改会被丢弃。";
            _detailUnlockButton.Text = locked ? "解锁编辑" : "重新锁定";
        }
        bool loopBlocks = file != null && CompletionBlockedByLoop(file);
        _detailCompletionCombo.Enabled = file != null && !locked && !loopBlocks;
        _detailCompletionNote.Visible = loopBlocks;
        _detailStayCheck.Enabled = file != null && !locked && IsStayDurationApplicable(file);
        _detailStaySeconds.Enabled = file != null && !locked && IsStayDurationApplicable(file) && file.StayDuration.HasValue;
        _detailFadeCheck.Enabled = file != null && !locked;
        _detailFadeSeconds.Enabled = file != null && !locked && file.VolumeFollowsFade;
        _detailDocumentButton.Enabled = file != null && !locked;
        // DEV-04 sidebar groups obey the same lock: the presentation fields and the
        // transition override edit this file; the music cue edits its activity, so it
        // locks while the activity holds a protected file.
        bool docLocked = locked;
        _detailDocModeCombo.Enabled = file != null && !docLocked;
        _detailDocStartPage.Enabled = file != null && !docLocked;
        _detailDocHasEndCheck.Enabled = file != null && !docLocked;
        _detailDocEndPage.Enabled = file != null && !docLocked && _detailDocHasEndCheck.Checked;
        _detailDocLoopCheck.Enabled = file != null && !docLocked;
        _detailDocKeepCheck.Enabled = file != null && !docLocked;
        _detailDocAutoCheck.Enabled = file != null && !docLocked;
        _detailDocAutoSeconds.Enabled = file != null && !docLocked && _detailDocAutoCheck.Checked;
        _detailTransitionCombo.Enabled = file != null && !locked;
        bool musicLocked = _detailActivity != null && _detailActivity.Files.Any(DetailEditLocked);
        _detailMusicCombo.Enabled = _detailActivity != null && !musicLocked;
    }

    /// <summary>DEV-04(5): a presentation whose 循环放映 is on AND which auto-advances
    /// （每张停留 set) never completes — the static path wraps last→first without
    /// HandleCompletion and the native show loops inside the suite — so 播完后 can never
    /// fire and the combo is disabled with an explanation. With no auto-advance the stay
    /// timer still fires HandleCompletion, so 播完后 applies and is left enabled. The
    /// native-loop half is a code reading (Loop is passed to the suite); whether a given
    /// suite really never reports the end needs a real-machine check.</summary>
    private static bool CompletionBlockedByLoop(MediaFile file) =>
        file.Kind == MediaKind.Document
        && DocumentFormats.Of(file.SourcePath) == DocumentFormat.Presentation
        && file.Document?.Loop == true
        && file.Document?.SecondsPerPage is > 0;

    /// <summary>DEV-03: re-evaluates protection when playback or output state changes
    /// without a selection change. An unlocked NEXT item that just went on air re-locks
    /// immediately: the fill discards anything typed but never committed, and the commit
    /// guards below guarantee nothing is written to the airing item — not even by a
    /// focus-loss (Leave) commit racing the re-lock.</summary>
    internal void RefreshDetailProtection()
    {
        var file = _detailFile;
        if (file != null && ReferenceEquals(_detailUnlockedFile, file) && ProgramIsOnAir(file))
        {
            _detailUnlockedFile = null;
            RefreshFileDetails();
            return;
        }
        ApplyDetailProtection();
    }

    private void OnDetailProtectButtonClick()
    {
        var file = _detailFile;
        if (file == null || !IsDetailProtected(file)) return;
        if (ReferenceEquals(_detailUnlockedFile, file))
        {
            _detailUnlockedFile = null;
            RefreshFileDetails(); // re-lock: discard unsubmitted edits with the fill.
        }
        else
        {
            _detailUnlockedFile = file;
            ApplyDetailProtection();
        }
    }

    /// <summary>DEV-04(4): documents carry their pipeline tag, not the generic "文档" —
    /// 演示文稿 / PDF / Word / 表格, from the same <see cref="DocumentFormats"/> list
    /// playback uses. "Word" (not DisplayName's "Word 文档") is the backlog's tag.</summary>
    private static string DetailKindText(MediaFile file)
    {
        if (file.Kind != MediaKind.Document) return file.Kind switch
        {
            MediaKind.Image => "图片",
            MediaKind.Video => "视频",
            MediaKind.Audio => "音频",
            _ => file.Kind.ToString(),
        };
        return DocumentFormats.Of(file.SourcePath) switch
        {
            DocumentFormat.Presentation => "演示文稿",
            DocumentFormat.Pdf => "PDF",
            DocumentFormat.Word => "Word",
            DocumentFormat.Spreadsheet => "表格",
            _ => "文档",
        };
    }

    // ---- Commits: every one ends in the same Apply* the dialogs use (Editing.cs), then
    // re-fills, so a save failure (TrySave already rolled back and reported) shows the stored
    // value again rather than the rejected edit. ----

    private void CommitDetailActivityPlayMode()
    {
        if (_detailSyncing || _detailScenario is not { } scenario || _detailActivity is not { } activity) return;
        HideDetailSavedHint(); // FL-01a: the next edit begins — clear the old hint.
        if (_detailActivityPlayModeCombo.SelectedItem is not DetailItem<PlayMode> item) return;
        bool saved = ApplyActivityPlayMode(scenario, activity, item.Value);
        RefreshFileDetails();
        if (saved) ShowDetailSavedHint();
    }

    private void CommitDetailPlayMode()
    {
        if (_detailSyncing || _detailFile is not { } file) return;
        HideDetailSavedHint(); // FL-01a
        _detailPlayModeCombo.Enabled = _detailPlayModeOverrideCheck.Checked;
        PlayMode? value = null;
        if (_detailPlayModeOverrideCheck.Checked)
        {
            if (_detailPlayModeCombo.SelectedItem is not DetailItem<PlayMode> item) return;
            value = item.Value;
        }
        bool saved = ApplyPlayModeOverride(file, value);
        RefreshFileDetails();
        if (saved) ShowDetailSavedHint(ProgramIsOnAir(file));
    }

    private void CommitDetailCompletionAction()
    {
        if (_detailSyncing || _detailFile is not { } file) return;
        // DEV-03: a locked editor refuses the write — this also covers a focus-loss commit
        // racing a re-lock (the re-fill discards the uncommitted value).
        if (DetailEditLocked(file)) { RefreshFileDetails(); return; }
        HideDetailSavedHint(); // FL-01a
        if (_detailCompletionCombo.SelectedItem is not DetailItem<CompletionAction> item) return;
        bool saved = ApplyCompletionAction(file, item.Value);
        RefreshFileDetails();
        // DEV-03: plain "✓ 已保存" — never the "下次播出时生效" variant. HandleCompletion
        // re-reads the live file object's OnCompletion at completion time, so an unlocked
        // edit to the airing item's 播完后 affects THIS airing, and the next-play wording
        // would be false. (Stay/fade/document/transition values are read at play/Take time,
        // so their commits keep the next-play hint honestly.)
        if (saved) ShowDetailSavedHint();
    }

    private void CommitDetailAllowManualSkip()
    {
        if (_detailSyncing || _detailFile is not { } file) return;
        HideDetailSavedHint(); // FL-01a
        bool saved = ApplyAllowManualSkip(file, _detailAllowManualSkipCheck.Checked);
        RefreshFileDetails();
        if (saved) ShowDetailSavedHint(ProgramIsOnAir(file));
    }

    private void CommitDetailStayDuration()
    {
        if (_detailSyncing || _detailFile is not { } file) return;
        if (DetailEditLocked(file)) { RefreshFileDetails(); return; } // DEV-03: refuse, discard.
        HideDetailSavedHint(); // FL-01a
        bool saved = ApplyStayDuration(file, _detailStayCheck.Checked ? TimeSpan.FromSeconds((double)_detailStaySeconds.Value) : null);
        RefreshFileDetails();
        if (saved) ShowDetailSavedHint(ProgramIsOnAir(file));
    }

    private void CommitDetailFade()
    {
        if (_detailSyncing || _detailFile is not { } file) return;
        if (DetailEditLocked(file)) { RefreshFileDetails(); return; } // DEV-03: refuse, discard.
        HideDetailSavedHint(); // FL-01a
        bool saved = ApplyFade(file, _detailFadeCheck.Checked,
            _detailFadeCheck.Checked ? TimeSpan.FromSeconds((double)_detailFadeSeconds.Value) : null);
        RefreshFileDetails();
        if (saved) ShowDetailSavedHint(ProgramIsOnAir(file));
    }

    private void CommitDetailAudioProperties()
    {
        if (_detailSyncing || _detailFile is not { } file) return;
        HideDetailSavedHint(); // FL-01a
        _detailBgAudioCaveat.Visible = _detailBgAudioCheck.Checked;
        // FL-01c: never silently drop the commit when the visual combo has no selection.
        // That only happens when the stored value is corrupted out of the enum (the fill
        // always selects a valid stored value); fall back to the stored value when it is
        // valid, else to the first visual — and RefreshFileDetails below re-fills both
        // controls from what was actually saved, so the panel can no longer display a
        // checkbox state that never reached the store.
        AudioVisual visual;
        if (_detailAudioVisualCombo.SelectedItem is DetailItem<AudioVisual> item) visual = item.Value;
        else if (Enum.IsDefined(file.BackgroundAudioVisual)) visual = file.BackgroundAudioVisual;
        else if (_detailAudioVisualCombo.Items[0] is DetailItem<AudioVisual> first) visual = first.Value;
        else visual = AudioVisual.Waveform; // unreachable: the combo always carries its three items
        bool saved = ApplyAudioProperties(file, _detailBgAudioCheck.Checked, visual);
        RefreshFileDetails();
        if (saved) ShowDetailSavedHint(ProgramIsOnAir(file));
    }

    /// <summary>DEV-04(1): the mode combo's selection, falling back to the stored value
    /// when it has none (FL-01c pattern — only a corrupted stored value leaves it
    /// unselected, and the fill below re-selects the stored value after the commit).</summary>
    private PresentationMode SelectedDocMode() =>
        _detailDocModeCombo.SelectedItem is DetailItem<PresentationMode> item ? item.Value
        : _detailFile?.Document?.PresentationMode ?? PresentationMode.Auto;

    /// <summary>DEV-04(1): one commit path for every in-place presentation control — the
    /// dialog's own shape (clone, mutate, assign, save with rollback, log, re-fill), so
    /// the sidebar and the dialog cannot drift apart on what a presentation setting is.
    /// Unchecked auto-advance / end-page controls write null, exactly like the dialog's
    /// Collect. DEV-03 locked: refuse and discard, like the trio's commits.</summary>
    private void CommitDetailDocument(Action<DocumentPlaybackSettings> mutate)
    {
        if (_detailSyncing || _detailFile is not { } file) return;
        if (DetailEditLocked(file)) { RefreshFileDetails(); return; }
        HideDetailSavedHint(); // FL-01a
        var old = file.Document;
        var updated = file.Document?.Clone() ?? new DocumentPlaybackSettings();
        mutate(updated);
        file.Document = updated;
        if (!TrySave()) { file.Document = old; RefreshFileDetails(); return; }
        _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.Document), null, DocumentSettingsDialog.Summarize(file));
        RefreshFileDetails();
        ShowDetailSavedHint(ProgramIsOnAir(file));
    }

    /// <summary>DEV-04(2): the item-entry transition override, in place — the menu's exact
    /// save/log/rollback shape (<see cref="FillTransitionMenu"/>), DEV-03 locked like the
    /// trio.</summary>
    private void CommitDetailTransition()
    {
        if (_detailSyncing || _detailFile is not { } file) return;
        if (DetailEditLocked(file)) { RefreshFileDetails(); return; }
        HideDetailSavedHint(); // FL-01a
        if (_detailTransitionCombo.SelectedItem is not DetailItem<TransitionStyle?> item) return;
        var old = file.TransitionOverride;
        file.TransitionOverride = item.Value;
        if (!TrySave()) { file.TransitionOverride = old; RefreshFileDetails(); return; }
        _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.TransitionOverride), old?.ToString(), item.Value?.ToString());
        RefreshFileDetails();
        ShowDetailSavedHint(ProgramIsOnAir(file));
    }

    /// <summary>DEV-04(2): rebuilds the music-cue combo's options from the current
    /// playlists (same set as <see cref="FillMusicCueMenu"/>) and selects the activity's
    /// cue. A cue pointing at a playlist that no longer exists gets a plainly-worded
    /// stand-in item holding the same id, so the combo never shows a blank selection for
    /// a saved value.</summary>
    private void FillDetailMusicCombo(Activity? activity)
    {
        _detailMusicCombo.Items.Clear();
        _detailMusicCombo.Items.Add(new DetailItem<(Guid?, bool)>((null, false), "不改变（继续当前音乐）"));
        _detailMusicCombo.Items.Add(new DetailItem<(Guid?, bool)>((null, true), "淡出停止"));
        foreach (var playlist in Playlists?.Invoke() ?? Array.Empty<Playlist>())
            _detailMusicCombo.Items.Add(new DetailItem<(Guid?, bool)>((playlist.Id, false), $"切换到歌单：{playlist.Name}"));
        if (activity == null) return;
        var current = (activity.MusicPlaylistId, activity.MusicFadeOut);
        bool known = false;
        foreach (var item in _detailMusicCombo.Items)
            if (item is DetailItem<(Guid?, bool)> choice && EqualityComparer<(Guid?, bool)>.Default.Equals(choice.Value, current)) { known = true; break; }
        if (!known) _detailMusicCombo.Items.Add(new DetailItem<(Guid?, bool)>(current, "（原歌单已不存在）"));
        SelectDetailItem(_detailMusicCombo, current);
    }

    /// <summary>DEV-04(2): the segment-entry music cue, in place — the menu's exact
    /// save/log/rollback shape. The cue belongs to the activity: it locks (DEV-03) while
    /// the activity holds the on-air or next item, since changing it would change the
    /// imminent section's music mid-show.</summary>
    private void CommitDetailMusicCue()
    {
        if (_detailSyncing || _detailActivity is not { } activity || _detailScenario is not { } scenario) return;
        if (_detailActivity.Files.Any(DetailEditLocked)) { RefreshFileDetails(); return; }
        HideDetailSavedHint(); // FL-01a
        if (_detailMusicCombo.SelectedItem is not DetailItem<(Guid?, bool)> item) return;
        var (playlistId, fadeOut) = item.Value;
        if (activity.MusicPlaylistId == playlistId && activity.MusicFadeOut == fadeOut) return; // no change.
        var (oldId, oldFade) = (activity.MusicPlaylistId, activity.MusicFadeOut);
        activity.MusicPlaylistId = playlistId;
        activity.MusicFadeOut = fadeOut;
        if (!TrySave()) { (activity.MusicPlaylistId, activity.MusicFadeOut) = (oldId, oldFade); RefreshFileDetails(); return; }
        _fileOpLog.LogActivityModified(scenario.Id, activity.Id, activity.Name);
        RefreshFileDetails();
        ShowDetailSavedHint();
    }

    /// <summary>Enter in a seconds box commits it (and is swallowed, so it doesn't also beep or
    /// trigger the panel's default-button handling); the value is otherwise committed on Leave —
    /// see the L-02 note at the top of this file for why ValueChanged is deliberately not used.</summary>
    private void OnDetailSecondsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;
        if (sender == _detailStaySeconds) CommitDetailStayDuration();
        else if (sender == _detailFadeSeconds) CommitDetailFade();
        else if (sender == _detailDocStartPage) CommitDetailDocument(s => s.StartPage = (int)_detailDocStartPage.Value);
        else if (sender == _detailDocEndPage) CommitDetailDocument(s => s.EndPage = _detailDocHasEndCheck.Checked ? (int)_detailDocEndPage.Value : null);
        else if (sender == _detailDocAutoSeconds) CommitDetailDocument(s => s.SecondsPerPage = _detailDocAutoCheck.Checked ? (double)_detailDocAutoSeconds.Value : null);
    }
}
