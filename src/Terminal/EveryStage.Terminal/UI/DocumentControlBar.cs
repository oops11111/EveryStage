using EveryStage.Terminal.Data;
using EveryStage.Terminal.Documents;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI;

/// <summary>
/// The document row of the "本地预览 / 播控窗口" card, shown while a document plays. Its buttons match the
/// document type and mode:
/// <list type="bullet">
/// <item>PDF (and Word read-only pages): 连续/单页 · 上一页 · 下一页 · 页码 · 缩放 · 实时同步 · 冻结大屏 · 更新大屏</item>
/// <item>Word live: 打开编辑 · 保存 · 另存为 · 适应宽度 · 适应页面 · 实时同步 · 冻结大屏 · 更新大屏 · 停止投屏 (· 重新连接)</item>
/// <item>Presentation, native show: 静态 · 上一步 · 下一步 · 上一张 · 下一张 · 页码 · 黑屏 · 白屏 · 暂停 · 结束/开始放映 · 编辑 · 重新载入 · sync</item>
/// <item>Presentation, static slides: 原生 · 上一张 · 下一张 · 页码 · sync</item>
/// </list>
/// Every button's enabled state comes from the current state (nothing loaded, window disconnected, Program
/// not showing this document…), refreshed with the dock. Buttons that do not fit move into a "⋯" menu.
/// The bar acts on <see cref="DocumentProgramSync.Target"/>; the sync buttons act on Program.
/// </summary>
internal sealed class DocumentControlBar : Panel
{
    private sealed record Item(Control Control, string Text, Func<bool> Enabled, Func<bool>? Checked, Action? Run);

    /// <summary>Reload decisions (DEV-06): the notice text and the action labels; returns the chosen
    /// action's index, or null when dismissed without choosing (cancel). Unset falls back to the
    /// previous modal dialogs (standalone/test use).</summary>
    public Func<string, string[], Task<int?>>? ConfirmAsync { get; set; }

    /// <summary>DEV-21: the no-frame prompt after 结束放映 (no captured frame to freeze) goes
    /// through the host's non-modal toast. Unset in standalone/test use: the prompt is dropped.</summary>
    public Action<string>? NotifyToast { get; set; }

    private bool _reloadDecisionPending;

    /// <summary>DEV-20: downgrade notices the operator has accepted (file id + downgrade count),
    /// so an accepted notice never comes back for the same downgrade — while a fresh downgrade
    /// (new count) always shows again.</summary>
    private readonly HashSet<(Guid, int)> _acknowledgedDowngrades = new();

    private readonly DocumentProgramSync _sync;
    private readonly ToolTip _toolTip;
    private readonly int _buttonHeight;
    private readonly List<Item> _items = new();
    private readonly RoundedActionButton _more;
    private readonly ContextMenuStrip _moreMenu;
    private readonly ContextMenuStrip _zoomMenu;
    private TextBox? _pageBox;
    private Label? _pageCount;
    private string _layoutKey = "";

    /// <summary>"停止投屏": take Program off air.</summary>
    public event Action? StopProgramRequested;

    /// <summary>A document command finished or failed; the dock refreshes its status line.</summary>
    public event Action? Changed;

    public DocumentControlBar(DocumentProgramSync sync, ToolTip toolTip, int buttonHeight)
    {
        _sync = sync;
        _toolTip = toolTip;
        _buttonHeight = buttonHeight;
        BackColor = Color.Transparent;
        Margin = Padding.Empty;
        Padding = new Padding(0, 4, 0, 2);
        _moreMenu = Menu();
        _more = MakeButton("⋯", "更多文档控制");
        _more.Click += (_, _) => ShowMoreMenu();
        _zoomMenu = Menu();
        foreach (var (text, mode, zoom) in new (string, PdfZoomMode, double)[]
                 {
                     ("适合宽度", PdfZoomMode.FitWidth, 1), ("适合页面", PdfZoomMode.FitPage, 1), ("实际大小", PdfZoomMode.ActualSize, 1),
                     ("50%", PdfZoomMode.Custom, 0.5), ("75%", PdfZoomMode.Custom, 0.75), ("125%", PdfZoomMode.Custom, 1.25),
                     ("150%", PdfZoomMode.Custom, 1.5), ("200%", PdfZoomMode.Custom, 2),
                 })
            _zoomMenu.Items.Add(text, null, (_, _) => Act(() => View?.SetZoom(mode, zoom)));
    }

    private ContextMenuStrip Menu()
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Renderer = new ThemedMenuRenderer(() => ModernUi.PaletteFor(this)),
        };
        menu.Opening += (_, _) =>
        {
            var palette = ModernUi.PaletteFor(this);
            menu.BackColor = palette.Surface;
            menu.ForeColor = palette.Text;
            foreach (ToolStripItem item in menu.Items) item.ForeColor = palette.Text;
        };
        return menu;
    }

    private PlaybackEngine? Target => _sync.Target;
    private PdfDocumentView? View => Target?.PdfView;
    private DocumentStatus? Status => Target?.Document;
    private SlideShowSession? SlideShow => Target?.OfficeSession as SlideShowSession;
    private bool SessionReady => Target?.OfficeSession?.Status == OfficeSessionStatus.Ready;
    private bool ProgramLive => _sync.Program?.State is PlaybackChannelState.Playing or PlaybackChannelState.Paused;

    /// <summary>True when there is a document to control (the dock shows the bar only then).</summary>
    public bool HasDocument => Status is { Mode: not (DocumentPlaybackMode.None or DocumentPlaybackMode.Cover) };

    public void RefreshState()
    {
        var palette = ModernUi.PaletteFor(this);
        var status = Status;
        // DEV-20: the layout key carries the downgrade notice state, so accepting the notice
        // rebuilds the bar without it (and a fresh downgrade rebuilds it back in).
        string key = status == null ? "" : $"{status.Format}|{status.Mode}"
            + (Target?.DocumentDowngraded == true && !IsDowngradeAcked() ? "|downgraded" : "");
        if (key != _layoutKey)
        {
            _layoutKey = key;
            Rebuild(status);
        }
        foreach (var item in _items)
        {
            bool enabled;
            try { enabled = item.Enabled(); } catch (Exception) { enabled = false; }
            if (item.Control.Enabled != enabled) item.Control.Enabled = enabled;
            if (item.Checked != null && item.Control is RoundedActionButton button)
            {
                bool on = item.Checked();
                var color = on ? palette.AccentFill : palette.Surface;
                if (button.BackColor != color) { button.BackColor = color; button.Invalidate(); }
                button.ForeColor = on ? palette.AccentFillForeground : palette.Text;
            }
        }
        if (_pageBox != null && !_pageBox.Focused && status != null)
        {
            string page = status.Page > 0 ? status.Page.ToString() : "";
            if (_pageBox.Text != page) _pageBox.Text = page;
            string count = status.PageCount > 0 ? $"/ {status.PageCount}" : "";
            if (_pageCount!.Text != count) _pageCount.Text = count;
        }
        UpdateEndShowText();
    }

    private void UpdateEndShowText()
    {
        bool ended = SlideShow?.Status == OfficeSessionStatus.Ended;
        foreach (var item in _items)
        {
            if (item.Control is RoundedActionButton { Tag: "endshow" } button)
            {
                // DEV-21: the caption says what the button does and does not do — it only ends
                // the show and freezes the last frame; it never runs the item's 播完后, and it
                // is not the F12 full stop.
                string text = ended ? "开始放映" : "结束放映";
                if (button.Text != text) { button.Text = text; PerformLayout(); }
                string tip = ended ? "重新开始放映" : "只结束放映，不执行播完后（大屏保持最后画面）";
                _toolTip.SetToolTip(button, tip);
            }
            else if (item.Control is Label { Tag: "endshow-note" } note)
            {
                string text = ended ? "已结束放映，点“开始放映”重新开始" : "只结束放映，不执行播完后";
                if (note.Text != text) { note.Text = text; PerformLayout(); }
            }
        }
    }

    /// <summary>DEV-20: whether the current downgrade's notice was accepted (file id + the
    /// engine's downgrade count — a later downgrade of the same file is a new count).</summary>
    private bool IsDowngradeAcked()
    {
        var target = Target;
        return target?.CurrentFile != null && _acknowledgedDowngrades.Contains((target.CurrentFile.Id, target.DocumentDowngradeCount));
    }

    /// <summary>DEV-20: 接受静态 — the operator confirms the static picture; the notice goes
    /// away for this downgrade only. The saved mode is untouched (the retry button is what
    /// changes it), and the downgrade stays recorded in the engine's runtime state.</summary>
    private void AcknowledgeDowngrade()
    {
        var target = Target;
        if (target?.CurrentFile != null) _acknowledgedDowngrades.Add((target.CurrentFile.Id, target.DocumentDowngradeCount));
        Changed?.Invoke(); // re-runs RefreshState: the layout key drops "|downgraded".
    }

    /// <summary>DEV-21: ends the show, freezing the last captured frame. When no frame was ever
    /// captured there is nothing to freeze: an honest non-modal prompt says so instead of
    /// leaving a black screen unexplained. The frame check reads the state at press time —
    /// a frame landing during the end command does not retroactively change the answer.</summary>
    private async Task<bool> EndShowWithFrameCheckAsync()
    {
        var target = Target;
        if (target == null) return false;
        bool hadFrame = target.OfficeFrameSequence >= 0;
        bool ok = await target.EndShowAsync();
        if (ok && !hadFrame) NotifyToast?.Invoke("没有可保留的画面：放映尚未输出过画面，大屏将保持黑屏。");
        return ok;
    }

    private void Rebuild(DocumentStatus? status)
    {
        SuspendLayout();
        foreach (var item in _items) { Controls.Remove(item.Control); item.Control.Dispose(); }
        _items.Clear();
        Controls.Remove(_more);
        _pageBox = null;
        _pageCount = null;
        if (status != null)
        {
            switch (status.Mode)
            {
                case DocumentPlaybackMode.Pdf:
                case DocumentPlaybackMode.ReadOnlyPages:
                    AddToggle("连续", "连续滚动 / 单页翻页", () => View?.State.DisplayMode == PdfDisplayMode.Continuous,
                        () => View?.SetDisplayMode(View.State.DisplayMode == PdfDisplayMode.Continuous ? PdfDisplayMode.SinglePage : PdfDisplayMode.Continuous), () => View != null);
                    AddButton(" 上一页", "上一页（PageUp 翻屏）", () => View?.PreviousPage(), () => View != null);
                    AddButton("下一页 ", "下一页", () => View?.NextPage(), () => View != null);
                    AddPageBox();
                    AddButton("缩放 ▾", "适合宽度 / 适合页面 / 实际大小 / 自定义比例", null, () => View != null, zoomMenu: true);
                    break;
                case DocumentPlaybackMode.StaticSlides:
                    // DEV-20: the downgrade notice comes first (it is the reason this row is
                    // static at all): a plain warning label — no success color, no ✓ — stating
                    // the failure and what the static picture lacks, plus 重试原生 (the
                    // existing mode switch) and 接受静态 (dismisses this downgrade's notice).
                    // It never claims the item went up natively, and it never re-pushes the
                    // picture after an e-stop: it only appears while this engine holds the
                    // downgraded document.
                    if (Target?.DocumentDowngraded == true && !IsDowngradeAcked())
                    {
                        AddNoteLabel("原生放映失败，已改为静态幻灯片（不含动画、转场和嵌入媒体）", UiForegroundRole.Warning);
                        AddButton("重试原生", "再次尝试原生放映（保留动画、转场和嵌入媒体）",
                            () => Target?.SwitchPresentationMode(PresentationMode.Native),
                            () => Target != null && OfficeInstallation.HasPresentation);
                        AddButton("接受静态", "确认以静态幻灯片播出，不再提示此次降级",
                            AcknowledgeDowngrade, () => true);
                    }
                    AddButton("大屏原生放映", "投到大屏时用原生放映（保留动画、转场和嵌入媒体）；放映窗口只在扩展屏上，预览仍为静态幻灯片",
                        () => Target?.SwitchPresentationMode(PresentationMode.Native),
                        () => Target != null && OfficeInstallation.HasPresentation);
                    AddButton(" 上一张", "上一张", () => View?.PreviousPage(), () => View != null);
                    AddButton("下一张 ", "下一张", () => View?.NextPage(), () => View != null);
                    AddPageBox();
                    break;
                case DocumentPlaybackMode.WriterLive:
                    AddButton("打开编辑", "把 WPS/Word 编辑窗口切到前台（在操作员屏幕上编辑）", () => Target?.OpenDocumentEditor(), () => SessionReady);
                    AddButton("保存", "保存文档", () => RunAsync(Target!.SaveDocumentAsync()), () => SessionReady && Status?.ReadOnly != true);
                    AddButton("另存为", "另存为新文件", SaveAs, () => SessionReady);
                    AddButton("适应宽度", "编辑器按页宽显示", () => RunAsync(Target!.SetWriterPageFitAsync(false)), () => SessionReady);
                    AddButton("适应页面", "编辑器显示整页", () => RunAsync(Target!.SetWriterPageFitAsync(true)), () => SessionReady);
                    AddButton("重新连接", "文档窗口断开后重新连接", () => RunAsync(Target!.ReconnectDocumentAsync()),
                        () => Target?.OfficeSession?.Status == OfficeSessionStatus.Disconnected);
                    break;
                case DocumentPlaybackMode.NativeShow:
                    AddButton("静态幻灯片", "切换为静态幻灯片（不含动画、转场和嵌入媒体）", () => Target?.SwitchPresentationMode(PresentationMode.Static), () => Target != null);
                    AddButton(" 上一步", "上一个动画 / 上一张", () => Slide(s => s.PreviousStepAsync(), "上一步", SlideMove.PreviousStep), () => SessionReady);
                    AddButton("下一步 ", "下一个动画；当前页动画播完后进入下一张", () => Slide(s => s.NextStepAsync(), "下一步", SlideMove.NextStep), () => SessionReady);
                    AddButton("上一张", "上一张幻灯片", () => Slide(s => s.PreviousSlideAsync(), "上一张", SlideMove.PreviousSlide), () => SessionReady);
                    AddButton("下一张", "下一张幻灯片（跳过剩余动画）", () => Slide(s => s.NextSlideAsync(), "下一张", SlideMove.NextSlide), () => SessionReady);
                    AddPageBox();
                    AddToggle("黑屏", "黑屏 / 恢复", () => SlideShow?.Position.Screen == SlideShowScreen.Black,
                        () => Slide(s => s.ToggleScreenAsync(SlideShowScreen.Black), "黑屏", SlideMove.Screen), () => SessionReady);
                    AddToggle("白屏", "白屏 / 恢复", () => SlideShow?.Position.Screen == SlideShowScreen.White,
                        () => Slide(s => s.ToggleScreenAsync(SlideShowScreen.White), "白屏", SlideMove.Screen), () => SessionReady);
                    AddToggle("暂停", "暂停自动放映 / 继续", () => SlideShow?.Position.Screen == SlideShowScreen.Paused,
                        () => Slide(s => s.ToggleScreenAsync(SlideShowScreen.Paused), "暂停"), () => SessionReady);
                    var end = AddButton("结束放映", "只结束放映，不执行播完后（大屏保持最后画面）/ 重新开始放映", () =>
                    {
                        if (SlideShow?.Status == OfficeSessionStatus.Ended) RunAsync(Target!.RestartShowAsync());
                        else RunAsync(EndShowWithFrameCheckAsync());
                    }, () => Target?.OfficeSession?.Status is OfficeSessionStatus.Ready or OfficeSessionStatus.Ended);
                    end.Tag = "endshow";
                    // DEV-21: the caption next to the button states the boundary in words — it
                    // only ends the show, it does not run 播完后. (Static-downgraded items have
                    // no end-show button: their completion follows the static path's 播完后.)
                    var endNote = AddNoteLabel("只结束放映，不执行播完后", UiForegroundRole.Muted);
                    endNote.Tag = "endshow-note";
                    AddButton("编辑", "结束放映并在编辑器中修改（大屏保持最后画面）", EditPresentation, () => SessionReady);
                    AddButton("重新载入放映", "保存修改后重新开始放映，回到原幻灯片", ReloadPresentation,
                        () => Target?.OfficeSession?.Status is OfficeSessionStatus.Editing or OfficeSessionStatus.Disconnected);
                    break;
            }
            AddToggle("实时同步", "大屏实时跟随预览 / 编辑（关闭后需点“更新大屏”）", () => _sync.LiveSync,
                () => _sync.SetLiveSync(!_sync.LiveSync), () => _sync.ProgramShowsSameDocument);
            AddToggle("冻结大屏", "大屏保持当前画面；预览中可以继续翻页、修改", () => _sync.Frozen, _sync.ToggleFreeze, () => _sync.ProgramShowsSameDocument);
            AddButton("更新大屏", "把预览的当前位置 / 最新画面送到大屏", _sync.UpdateProgram,
                () => _sync.ProgramShowsSameDocument && (!_sync.LiveSync || _sync.Frozen));
            if (status.Format == DocumentFormat.Word)
                // OP-08: the old tooltip promised the preview/editor would be kept — the
                // button is wired to the full StopProgramOutput (both windows stop), so the
                // copy now says exactly that. Behaviour deliberately unchanged.
                AddButton("停止投屏", "停止全部输出", () => StopProgramRequested?.Invoke(), () => ProgramLive);
        }
        Controls.Add(_more);
        ResumeLayout(true);
    }

    private RoundedActionButton MakeButton(string text, string tip)
    {
        var button = new RoundedActionButton { Text = text, Height = _buttonHeight, Margin = Padding.Empty, Padding = new Padding(6, 0, 6, 0) };
        ModernUi.StyleButton(button);
        ModernUi.ApplyButtonRole(button, ModernUi.PaletteFor(this));
        button.Font = new Font("Microsoft YaHei UI", 8.5F);
        if (text.Contains('') || text.Contains('')) button.Text = text.Replace(" ", "‹ ").Replace(" ", " ›");
        button.Width = TextRenderer.MeasureText(button.Text, button.Font).Width + 18;
        _toolTip.SetToolTip(button, tip);
        return button;
    }

    /// <summary>A short explanatory label in the bar (DEV-20 downgrade notice, DEV-21
    /// end-show caption). Labels have no Run, so the overflow menu skips them — the wording
    /// is kept short enough to fit, and the full explanation rides the neighbouring button's
    /// tooltip / the caption tooltip.</summary>
    private Label AddNoteLabel(string text, UiForegroundRole role)
    {
        var label = new Label
        {
            AutoSize = true, Text = text, Margin = Padding.Empty, BackColor = Color.Transparent,
            Font = new Font(ModernUi.UiFont, 8.5F),
        };
        ModernUi.SetForegroundRole(label, role);
        _items.Add(new Item(label, text, () => true, null, null));
        Controls.Add(label);
        return label;
    }

    private RoundedActionButton AddButton(string text, string tip, Action? run, Func<bool> enabled, bool zoomMenu = false)
    {
        var button = MakeButton(text, tip);
        if (zoomMenu) button.Click += (_, _) => _zoomMenu.Show(button, new Point(0, button.Height));
        else if (run != null) button.Click += (_, _) => Act(run);
        _items.Add(new Item(button, button.Text, enabled, null, zoomMenu ? () => _zoomMenu.Show(this, new Point(Width - 120, Height)) : run));
        Controls.Add(button);
        return button;
    }

    private void AddToggle(string text, string tip, Func<bool> isOn, Action run, Func<bool> enabled)
    {
        var button = MakeButton(text, tip);
        button.Click += (_, _) => Act(run);
        _items.Add(new Item(button, text, enabled, isOn, run));
        Controls.Add(button);
    }

    private void AddPageBox()
    {
        _pageBox = new TextBox
        {
            Width = 38, Margin = Padding.Empty, TextAlign = HorizontalAlignment.Center, BorderStyle = BorderStyle.FixedSingle,
            BackColor = ModernUi.PaletteFor(this).Surface, ForeColor = ModernUi.PaletteFor(this).Text, Font = new Font(ModernUi.UiFont, 9F),
            AccessibleName = "页码 / 幻灯片序号",
        };
        ModernUi.SetBackgroundRole(_pageBox, UiBackgroundRole.Surface);
        ModernUi.SetForegroundRole(_pageBox, UiForegroundRole.Text);
        _toolTip.SetToolTip(_pageBox, "输入页码 / 幻灯片序号后按回车跳转");
        var box = _pageBox;
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            if (int.TryParse(box.Text.Trim(), out int page)) Act(() => GoTo(page));
            Parent?.Focus();
        };
        _items.Add(new Item(box, "跳转页码", () => Status is { PageCount: > 0 }, null, null));
        Controls.Add(box);
        _pageCount = new Label { AutoSize = true, ForeColor = ModernUi.PaletteFor(this).Muted, Margin = Padding.Empty, Font = new Font(ModernUi.UiFont, 8.5F), BackColor = Color.Transparent };
        ModernUi.SetForegroundRole(_pageCount, UiForegroundRole.Muted);
        _items.Add(new Item(_pageCount, "", () => true, null, null));
        Controls.Add(_pageCount);
    }

    private void GoTo(int page)
    {
        if (View is { } view) view.GoToPage(page);
        else if (SlideShow != null) Slide(s => s.GoToSlideAsync(page), "跳转", SlideMove.Jump);
    }

    private void Slide(Func<SlideShowSession, Task> command, string what, SlideMove move = SlideMove.None) =>
        RunAsync(Target!.SlideCommandAsync(command, what, move));

    private void Act(Action action)
    {
        try { action(); }
        catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, "文档控制", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        Changed?.Invoke();
    }

    private async void RunAsync(Task<bool> task)
    {
        try { await task; } catch (Exception) { }
        if (!IsDisposed) Changed?.Invoke();
    }

    private void SaveAs()
    {
        if (Status is not { } status) return;
        using var dialog = new SaveFileDialog
        {
            Title = "另存为",
            FileName = Path.GetFileName(status.File.SourcePath),
            InitialDirectory = Path.GetDirectoryName(status.File.SourcePath),
            Filter = "Word 文档|*.docx|Word 97-2003 文档|*.doc|所有文件|*.*",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) RunAsync(Target!.SaveDocumentAsAsync(dialog.FileName));
    }

    private void EditPresentation()
    {
        if (MessageBox.Show(FindForm(),
                "将结束放映并打开演示文稿编辑窗口。\n大屏保持当前最后画面，修改保存后点“重新载入放映”回到原幻灯片。",
                "编辑演示文稿", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
            RunAsync(Target!.EditPresentationAsync());
    }

    private async void ReloadPresentation()
    {
        // DEV-06: one pending reload decision at a time — a second click while the notice is up
        // is ignored instead of stacking a second decision.
        if (SlideShow is not { } show || _reloadDecisionPending) return;
        _reloadDecisionPending = true;
        try
        {
            bool dirty = await show.EditorIsDirtyAsync();
            bool save = false;
            if (dirty)
            {
                int? choice = await AskReloadChoiceAsync(
                    "演示文稿有未保存的修改，重新载入前是否保存？\n（重新载入后大屏将显示放映画面）",
                    ["保存", "丢弃"], MessageBoxButtons.YesNoCancel);
                if (choice == null) return; // dismissed: lose nothing, switch nothing
                save = choice == 0;
            }
            else if (_sync.ProgramShowsSameDocument)
            {
                int? choice = await AskReloadChoiceAsync("重新载入后大屏将显示放映画面，是否继续？",
                    ["继续", "取消"], MessageBoxButtons.OKCancel);
                if (choice != 0) return;
            }
            RunAsync(Target!.ReloadPresentationAsync(save));
        }
        finally { _reloadDecisionPending = false; }
    }

    /// <summary>Asks the reload decision on the non-modal notice surface when wired (DEV-06);
    /// otherwise the previous modal dialogs. Maps the dialog answer onto the option index.</summary>
    private async Task<int?> AskReloadChoiceAsync(string message, string[] options, MessageBoxButtons fallbackButtons)
    {
        if (ConfirmAsync != null) return await ConfirmAsync(message, options).ConfigureAwait(true);
        var answer = MessageBox.Show(FindForm(), message, "重新载入放映", fallbackButtons, MessageBoxIcon.Question);
        return answer switch
        {
            DialogResult.Yes or DialogResult.OK => 0,
            DialogResult.No => 1,
            _ => null,
        };
    }

    // ── Layout with overflow ─────────────────────────────────────────────────────────────────────

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_more == null) return; // layout raised from the base constructor / Padding setter, before setup
        int gap = 4, x = 0, top = Padding.Top;
        int available = ClientSize.Width;
        int moreWidth = _more.Width + gap;
        int total = _items.Sum(i => PreferredWidth(i.Control) + gap);
        bool overflow = total > available;
        foreach (var item in _items)
        {
            int width = PreferredWidth(item.Control);
            bool fits = x + width <= available - (overflow ? moreWidth : 0);
            item.Control.Visible = fits;
            if (fits)
            {
                int y = item.Control is Label ? top + (_buttonHeight - item.Control.Height) / 2
                    : item.Control is TextBox ? top + (_buttonHeight - item.Control.Height) / 2 : top;
                item.Control.Location = new Point(x, y);
                x += width + gap;
            }
            else overflow = true;
        }
        _more.Visible = overflow;
        if (overflow) _more.Location = new Point(Math.Min(x, available - _more.Width), top);
    }

    private static int PreferredWidth(Control control) => control is Label label ? label.PreferredWidth : control.Width;

    private void ShowMoreMenu()
    {
        _moreMenu.Items.Clear();
        foreach (var item in _items.Where(i => !i.Control.Visible && i.Run != null))
        {
            var menuItem = new ToolStripMenuItem(item.Text) { Enabled = item.Enabled(), ForeColor = ModernUi.PaletteFor(this).Text, Checked = item.Checked?.Invoke() == true };
            var run = item.Run!;
            menuItem.Click += (_, _) => Act(run);
            _moreMenu.Items.Add(menuItem);
        }
        if (_items.Any(i => !i.Control.Visible && i.Control is TextBox))
        {
            var jump = new ToolStripMenuItem("跳转到页码…") { ForeColor = ModernUi.PaletteFor(this).Text, Enabled = Status is { PageCount: > 0 } };
            jump.Click += (_, _) =>
            {
                using var dialog = new TextInputDialog("跳转", $"页码 / 幻灯片序号（1 - {Status?.PageCount}）：", "");
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK && int.TryParse(dialog.Value.Trim(), out int page)) Act(() => GoTo(page));
            };
            _moreMenu.Items.Add(jump);
        }
        _moreMenu.Show(_more, new Point(0, _more.Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _moreMenu.Dispose(); _zoomMenu.Dispose(); }
        base.Dispose(disposing);
    }
}
