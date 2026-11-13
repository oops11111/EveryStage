using EveryStage.Terminal.Data;

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
        ConfigureGroup(_detailCompletionGroup);
        _detailCompletionGroup.Controls.Add(PromptLabel("播放完成后："));
        _detailCompletionGroup.Controls.Add(_detailCompletionCombo);

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
        // FL-01b: shown (group visible, editors disabled) for Office documents instead of
        // hiding the whole group — the value exists in the store but the engine never acts on
        // it, and hiding it made operators read that as "the panel does nothing".
        _detailStayNote.Text = "Office 文档不支持自动翻页，停留时长不生效。";
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

        _detailBody.Controls.AddRange(new Control[]
        {
            _detailPlaceholder,
            _detailActivityPlayModeGroup, _detailFilePlayModeGroup, _detailCompletionGroup,
            _detailManualSkipGroup, _detailStayGroup, _detailFadeGroup, _detailAudioGroup,
            _detailDocumentGroup,
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
        _detailToggleButton.KeyDown += OnDetailEscapeKeyDown;
        _toolTip.SetToolTip(_detailToggleButton, "窗口太窄放不下属性面板时，点此以浮层打开");
        Controls.Add(_detailToggleButton);

        // AP-11: Esc leaves the forced overlay. ProcessCmdKey already belongs to the panel's
        // selection keys (ActivitiesPanel.cs) and cannot be overridden a second time, so the
        // overlay listens on the detail panel's own control tree instead — every editor is
        // created in this method, so one recursive walk here covers them all. (An Esc that
        // closes an open combo drop-down is consumed by the combo itself; the next Esc then
        // reaches this handler.)
        foreach (var control in SelfAndDescendants(_detailPanel))
            control.KeyDown += OnDetailEscapeKeyDown;

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
        int cap = Math.Max(LogicalToDeviceUnits(200), ClientSize.Height * 70 / 100);
        int chrome = _detailTitle.Height
            + (_detailKindLabel.Visible ? _detailKindLabel.Height : 0)
            + (_detailSavedLabel.Visible ? _detailSavedLabel.Height : 0)
            + _detailCard.Padding.Vertical;
        int bodyHeight = Math.Min(MeasureDetailBodyContentHeight(), Math.Max(0, cap - chrome));
        _detailCard.Height = chrome + bodyHeight;
        if (_detailPanelForced) _detailPanel.Height = _detailCard.Height;
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
    /// no-change commit shows nothing, because nothing was saved.</summary>
    private void ShowDetailSavedHint()
    {
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

    /// <summary>AP-11: Esc while the forced overlay is open closes it (wired to the toggle
    /// button and, in BuildDetailPanel, to every control inside the detail panel).</summary>
    private void OnDetailEscapeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Escape || !_detailPanelForced) return;
        e.SuppressKeyPress = true;
        SetDetailPanelForced(false);
    }

    private static IEnumerable<Control> SelfAndDescendants(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (var descendant in SelfAndDescendants(child))
                yield return descendant;
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

        _detailSyncing = true;
        try
        {
            _detailPlaceholder.Visible = activity == null;
            _detailTitle.Text = file != null ? Path.GetFileName(file.SourcePath) : activity?.Name ?? "属性";
            _detailKindLabel.Text = file != null ? DetailKindText(file.Kind) : activity != null ? "环节" : "";
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
        }
        finally
        {
            _detailSyncing = false;
            // FL-02: group visibility changed the content height — here, not after the
            // try/finally, so the file == null early return above doesn't skip it.
            UpdateDetailCardHeight();
        }
    }

    private static string DetailKindText(MediaKind kind) => kind switch
    {
        MediaKind.Image => "图片",
        MediaKind.Video => "视频",
        MediaKind.Audio => "音频",
        MediaKind.Document => "文档",
        _ => kind.ToString(),
    };

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
        if (saved) ShowDetailSavedHint();
    }

    private void CommitDetailCompletionAction()
    {
        if (_detailSyncing || _detailFile is not { } file) return;
        HideDetailSavedHint(); // FL-01a
        if (_detailCompletionCombo.SelectedItem is not DetailItem<CompletionAction> item) return;
        bool saved = ApplyCompletionAction(file, item.Value);
        RefreshFileDetails();
        if (saved) ShowDetailSavedHint();
    }

    private void CommitDetailAllowManualSkip()
    {
        if (_detailSyncing || _detailFile is not { } file) return;
        HideDetailSavedHint(); // FL-01a
        bool saved = ApplyAllowManualSkip(file, _detailAllowManualSkipCheck.Checked);
        RefreshFileDetails();
        if (saved) ShowDetailSavedHint();
    }

    private void CommitDetailStayDuration()
    {
        if (_detailSyncing || _detailFile is not { } file) return;
        HideDetailSavedHint(); // FL-01a
        bool saved = ApplyStayDuration(file, _detailStayCheck.Checked ? TimeSpan.FromSeconds((double)_detailStaySeconds.Value) : null);
        RefreshFileDetails();
        if (saved) ShowDetailSavedHint();
    }

    private void CommitDetailFade()
    {
        if (_detailSyncing || _detailFile is not { } file) return;
        HideDetailSavedHint(); // FL-01a
        bool saved = ApplyFade(file, _detailFadeCheck.Checked,
            _detailFadeCheck.Checked ? TimeSpan.FromSeconds((double)_detailFadeSeconds.Value) : null);
        RefreshFileDetails();
        if (saved) ShowDetailSavedHint();
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
        if (saved) ShowDetailSavedHint();
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
    }
}
