namespace EveryStage.Caster.UI;

/// <summary>Base for the right-hand pages: a page-coloured panel laid out in 96-DPI units.</summary>
internal abstract class CasterView : ThemedSurface
{
    protected int S(int value) => LogicalToDeviceUnits(value);

    protected static ThemedLabel NewLabel(string text, TextRole role = TextRole.Text, float size = 10F, bool semibold = false,
        ContentAlignment align = ContentAlignment.TopLeft) => new ThemedLabel
        {
            Text = text,
            Font = new Font(semibold ? ModernUi.UiFontSemibold : ModernUi.UiFont, size),
            TextAlign = align,
            AutoSize = false,
        }.With(role);

    protected static ActionButton NewButton(string text, ButtonKind kind, ButtonGlyph glyph = ButtonGlyph.None, float size = 10F) => new()
    {
        Text = text,
        Kind = kind,
        Glyph = glyph,
        Font = new Font(kind is ButtonKind.Primary or ButtonKind.Danger ? ModernUi.UiFontSemibold : ModernUi.UiFont, size),
    };

    /// <summary>Called after the palette changed, for colours stock controls cannot pick up themselves.</summary>
    public virtual void OnThemeChanged() { }
}

/// <summary>Nothing selected yet (or nothing found).</summary>
internal sealed class EmptyView : CasterView
{
    private readonly ThemedLabel _title = NewLabel("选择左侧的终端开始投屏", TextRole.Text, 14F, semibold: true, ContentAlignment.MiddleCenter);
    private readonly ThemedLabel _hint = NewLabel("", TextRole.Muted, 10F, align: ContentAlignment.TopCenter);

    public EmptyView()
    {
        Controls.AddRange(new Control[] { _title, _hint });
        SetFound(false);
    }

    public void SetFound(bool any) => _hint.Text = any
        ? "左侧列出了这个局域网里能投的终端。"
        : "还没有发现终端。请确认终端机已经打开，并且和这台电脑在同一个局域网。";

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int w = ClientSize.Width - S(64);
        _title.Bounds = new Rectangle(S(32), ClientSize.Height / 2 - S(44), w, S(36));
        _hint.Bounds = new Rectangle(S(32), ClientSize.Height / 2, w, S(48));
    }
}

/// <summary>One display the operator can cast.</summary>
internal sealed record MonitorChoice(int OutputIndex, string Label, string Badge, string? DeviceName);

/// <summary>The selected terminal: which screen to cast, sound on/off, 开始投屏.</summary>
internal sealed class StandbyView : CasterView
{
    private readonly ThemedLabel _name = NewLabel("", TextRole.Text, 16F, semibold: true);
    private readonly ThemedLabel _status = NewLabel("", TextRole.Muted, 10F);
    private readonly ActionButton _removePairing = NewButton("移除配对", ButtonKind.Ghost, size: 9.5F);
    private readonly CardPanel _monitorCard = new();
    private readonly ThemedLabel _monitorTitle = NewLabel("投哪块屏幕", TextRole.Text, 10F, semibold: true);
    private readonly FlowLayoutPanel _tiles = new() { WrapContents = true, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly CheckBox _audio = new() { Text = "同时投送电脑声音", AutoSize = false, Font = new Font(ModernUi.UiFont, 10F), UseMnemonic = false };
    private readonly ThemedLabel _hint = NewLabel("", TextRole.Muted, 9.5F);
    private readonly ThemedLabel _privacy = NewLabel("投送的是整块屏幕。画面经设备认证但不加密，请只在受信任的局域网中使用。", TextRole.Muted, 9F, align: ContentAlignment.MiddleLeft);
    private readonly ActionButton _start = NewButton("开始投屏", ButtonKind.Primary, ButtonGlyph.Cast, 11F);
    private bool _busy;
    private bool _canStart;

    public event Action? StartClicked;
    public event Action? RemovePairingClicked;
    public event Action<bool>? AudioChanged;

    public StandbyView()
    {
        _monitorCard.Controls.AddRange(new Control[] { _monitorTitle, _tiles, _audio });
        Controls.AddRange(new Control[] { _name, _status, _removePairing, _monitorCard, _hint, _privacy, _start });
        _start.Click += (_, _) => StartClicked?.Invoke();
        _removePairing.Click += (_, _) => RemovePairingClicked?.Invoke();
        _audio.CheckedChanged += (_, _) => AudioChanged?.Invoke(_audio.Checked);
        _start.AccessibleName = "开始投屏";
        OnThemeChanged();
    }

    public bool IncludeAudio
    {
        get => _audio.Checked;
        set => _audio.Checked = value;
    }

    public int SelectedOutputIndex => _tiles.Controls.OfType<ChoiceTile>().FirstOrDefault(t => t.Checked)?.Tag is MonitorChoice choice ? choice.OutputIndex : 0;
    public string SelectedMonitorLabel => _tiles.Controls.OfType<ChoiceTile>().FirstOrDefault(t => t.Checked)?.Tag is MonitorChoice choice ? choice.Label : "";
    private string? SelectedDevice => _tiles.Controls.OfType<ChoiceTile>().FirstOrDefault(t => t.Checked)?.Tag is MonitorChoice choice ? choice.DeviceName : null;

    public void SetTerminal(string name, string status, bool canStart, bool canRemovePairing, string hint)
    {
        _name.Text = name;
        _status.Text = status;
        _canStart = canStart;
        _removePairing.Visible = canRemovePairing;
        _hint.Text = hint;
        UpdateStart();
    }

    /// <summary>Shows 「正在连接…」 on the button while a start is in flight.</summary>
    public void SetBusy(bool busy, string text = "正在连接…")
    {
        _busy = busy;
        _start.Text = busy ? text : "开始投屏";
        UpdateStart();
    }

    private void UpdateStart() => _start.Enabled = _canStart && !_busy;

    /// <summary>Re-lists the displays, keeping the chosen one by device name (output indices shift when
    /// a display is unplugged).</summary>
    public void SetMonitors(IReadOnlyList<MonitorChoice> monitors)
    {
        string? previous = SelectedDevice;
        _tiles.SuspendLayout();
        foreach (Control old in _tiles.Controls.Cast<Control>().ToList()) old.Dispose();
        _tiles.Controls.Clear();
        ChoiceTile? toCheck = null;
        foreach (var monitor in monitors)
        {
            var tile = new ChoiceTile { Text = monitor.Label, Badge = monitor.Badge, Tag = monitor, Font = new Font(ModernUi.UiFont, 10F), Margin = new Padding(0, 0, S(8), S(8)) };
            _tiles.Controls.Add(tile);
            if (toCheck == null || monitor.DeviceName == previous && previous != null) toCheck = tile;
        }
        if (toCheck != null) toCheck.Checked = true;
        _tiles.ResumeLayout();
        PerformLayout();
    }

    public override void OnThemeChanged()
    {
        _tiles.BackColor = ModernUi.Palette.Surface;
        _audio.BackColor = ModernUi.Palette.Surface;
        _audio.ForeColor = ModernUi.Palette.Text;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = S(28), w = ClientSize.Width - pad * 2, y = S(22);
        _removePairing.Bounds = new Rectangle(ClientSize.Width - pad - S(92), y + S(4), S(92), S(30));
        _name.Bounds = new Rectangle(pad, y, w - (_removePairing.Visible ? S(100) : 0), S(36));
        y += S(38);
        _status.Bounds = new Rectangle(pad, y, w, S(22));
        y += S(34);

        int inner = w - S(32), tileW = S(210), tileH = S(48), gap = S(8);
        int perRow = Math.Max(1, (inner + gap) / (tileW + gap));
        int rows = Math.Max(1, (_tiles.Controls.Count + perRow - 1) / perRow);
        foreach (Control tile in _tiles.Controls)
        {
            tile.Size = new Size(tileW, tileH);
            tile.Margin = new Padding(0, 0, gap, gap);
        }
        int tilesH = rows * (tileH + gap);
        _monitorTitle.Bounds = new Rectangle(S(16), S(14), inner, S(22));
        _tiles.Bounds = new Rectangle(S(16), S(42), inner, tilesH);
        _audio.Bounds = new Rectangle(S(16), S(42) + tilesH, inner, S(28));
        int cardH = S(42) + tilesH + S(28) + S(12);
        _monitorCard.Bounds = new Rectangle(pad, y, w, cardH);
        y += cardH + S(12);
        _hint.Bounds = new Rectangle(pad, y, w, S(44));

        int buttonW = S(156), buttonH = S(46), bottom = ClientSize.Height - S(24) - buttonH;
        _start.Bounds = new Rectangle(ClientSize.Width - pad - buttonW, bottom, buttonW, buttonH);
        _privacy.Bounds = new Rectangle(pad, bottom, Math.Max(S(40), w - buttonW - S(16)), buttonH);
    }
}

/// <summary>First cast to a terminal: the six-digit code to compare on the terminal's screen.</summary>
internal sealed class PairingView : CasterView
{
    private readonly ThemedLabel _title = NewLabel("", TextRole.Muted, 11F, align: ContentAlignment.MiddleCenter);
    private readonly ThemedLabel _code = NewLabel("--- ---", TextRole.Text, 40F, semibold: true, ContentAlignment.MiddleCenter);
    private readonly ThemedLabel _instructions = NewLabel(
        "到终端机跟前，核对屏幕上弹出的是同一组数字，点「接受」。\n接受后这里自动开始投屏，以后不用再配对。\n终端机就在这台电脑上的话，确认框可能被挡住：在任务栏点「投屏设备配对请求」。",
        TextRole.Text, 10.5F, align: ContentAlignment.TopCenter);
    private readonly ThemedLabel _waiting = NewLabel("正在向终端机发送配对请求…", TextRole.Warning, 10F, align: ContentAlignment.MiddleCenter);
    private readonly ActionButton _cancel = NewButton("取消", ButtonKind.Secondary);

    public event Action? CancelClicked;

    public PairingView()
    {
        Controls.AddRange(new Control[] { _title, _code, _instructions, _waiting, _cancel });
        _cancel.Click += (_, _) => CancelClicked?.Invoke();
    }

    public void Begin(string terminalName)
    {
        _title.Text = $"首次投到「{terminalName}」，需要在终端机上确认";
        _code.Text = "--- ---";
        _waiting.Text = "正在向终端机发送配对请求…";
    }

    public void SetCode(string code)
    {
        _code.Text = code.Length == 6 ? code.Insert(3, " ") : code;
        _waiting.Text = "等待终端机确认";
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int w = ClientSize.Width - S(56), x = S(28);
        int y = Math.Max(S(24), ClientSize.Height / 2 - S(150));
        _title.Bounds = new Rectangle(x, y, w, S(28)); y += S(40);
        _code.Bounds = new Rectangle(x, y, w, S(76)); y += S(88);
        _instructions.Bounds = new Rectangle(x, y, w, S(76)); y += S(84);
        _waiting.Bounds = new Rectangle(x, y, w, S(24)); y += S(40);
        _cancel.Bounds = new Rectangle((ClientSize.Width - S(110)) / 2, y, S(110), S(38));
    }
}

/// <summary>What went wrong, in plain words, and what to do about it.</summary>
internal sealed record Problem(string Title, string Reason, IReadOnlyList<string> Steps, string? Raw, bool CanRetry = true);

internal sealed class ProblemView : CasterView
{
    private readonly CardPanel _alert = new() { Kind = CardKind.Danger };
    private readonly Panel _alertIcon = new() { BackColor = Color.Transparent };
    private readonly ThemedLabel _title = NewLabel("", TextRole.DangerText, 12F, semibold: true);
    private readonly ThemedLabel _reason = NewLabel("", TextRole.Text, 10.5F);
    private readonly CardPanel _stepsCard = new();
    private readonly ThemedLabel _stepsTitle = NewLabel("可以这样解决", TextRole.Text, 10F, semibold: true);
    private readonly ThemedLabel _steps = NewLabel("", TextRole.Text, 10.5F);
    private readonly ActionButton _rawToggle = NewButton("原始错误信息（发给技术支持）", ButtonKind.Ghost, ButtonGlyph.ChevronDown, 9.5F);
    private readonly TextBox _raw = new() { ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, Visible = false, Font = new Font(ModernUi.UiFont, 9F) };
    private readonly ActionButton _back = NewButton("返回", ButtonKind.Secondary);
    private readonly ActionButton _retry = NewButton("重试", ButtonKind.Primary);

    public event Action? BackClicked;
    public event Action? RetryClicked;

    public ProblemView()
    {
        _alertIcon.Paint += (_, e) => Glyphs.Draw(e.Graphics, ButtonGlyph.Warning, new Rectangle(0, 0, _alertIcon.Width, _alertIcon.Width), ModernUi.Palette.Danger);
        _alert.Controls.AddRange(new Control[] { _alertIcon, _title, _reason });
        _stepsCard.Controls.AddRange(new Control[] { _stepsTitle, _steps });
        Controls.AddRange(new Control[] { _alert, _stepsCard, _rawToggle, _raw, _back, _retry });
        _rawToggle.TextAlign = ContentAlignment.MiddleLeft;
        _rawToggle.Click += (_, _) =>
        {
            _raw.Visible = !_raw.Visible;
            _rawToggle.Glyph = _raw.Visible ? ButtonGlyph.ChevronUp : ButtonGlyph.ChevronDown;
            PerformLayout();
        };
        _back.Click += (_, _) => BackClicked?.Invoke();
        _retry.Click += (_, _) => RetryClicked?.Invoke();
        OnThemeChanged();
    }

    public void Show(Problem problem)
    {
        _title.Text = problem.Title;
        _reason.Text = problem.Reason;
        _steps.Text = string.Join("\n", problem.Steps.Select((step, i) => problem.Steps.Count > 1 ? $"{i + 1}. {step}" : step));
        _stepsCard.Visible = problem.Steps.Count > 0;
        _raw.Text = problem.Raw ?? "";
        _raw.Visible = false;
        _rawToggle.Glyph = ButtonGlyph.ChevronDown;
        _rawToggle.Visible = !string.IsNullOrWhiteSpace(problem.Raw);
        _retry.Visible = problem.CanRetry;
        PerformLayout();
    }

    public override void OnThemeChanged()
    {
        _raw.BackColor = ModernUi.Palette.Background;
        _raw.ForeColor = ModernUi.Palette.Muted;
    }

    private int TextHeight(Label label, int width)
    {
        var size = TextRenderer.MeasureText(label.Text.Length == 0 ? " " : label.Text, label.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak);
        return size.Height + S(4);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = S(28), w = ClientSize.Width - pad * 2, y = S(24);
        int textW = w - S(64);
        int reasonH = TextHeight(_reason, textW);
        _alertIcon.Bounds = new Rectangle(S(16), S(16), S(24), S(24));
        _title.Bounds = new Rectangle(S(50), S(14), textW, S(26));
        _reason.Bounds = new Rectangle(S(50), S(42), textW, reasonH);
        _alert.Bounds = new Rectangle(pad, y, w, S(42) + reasonH + S(14));
        y = _alert.Bottom + S(14);

        if (_stepsCard.Visible)
        {
            int stepsH = TextHeight(_steps, w - S(32));
            _stepsTitle.Bounds = new Rectangle(S(16), S(12), w - S(32), S(22));
            _steps.Bounds = new Rectangle(S(16), S(38), w - S(32), stepsH);
            _stepsCard.Bounds = new Rectangle(pad, y, w, S(38) + stepsH + S(12));
            y = _stepsCard.Bottom + S(10);
        }
        _rawToggle.Bounds = new Rectangle(pad, y, w, S(34));
        y += S(38);
        int buttonsTop = ClientSize.Height - S(24) - S(40);
        _raw.Bounds = new Rectangle(pad + S(12), y, w - S(24), Math.Max(S(24), Math.Min(S(90), buttonsTop - y - S(10))));
        _retry.Bounds = new Rectangle(ClientSize.Width - pad - S(100), buttonsTop, S(100), S(40));
        _back.Bounds = new Rectangle(_retry.Visible ? _retry.Left - S(10) - S(100) : ClientSize.Width - pad - S(100), buttonsTop, S(100), S(40));
    }
}

internal enum CheckStatus { Pending, Running, Pass, Warn, Fail }

internal sealed record CheckResult(string Name, CheckStatus Status, string Detail);

/// <summary>One line of the 一键检查 list.</summary>
internal sealed class CheckRow : Control
{
    public CheckResult Result { get; private set; }

    public CheckRow(CheckResult result)
    {
        Result = result;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = new Font(ModernUi.UiFont, 10F);
        AccessibleRole = AccessibleRole.StaticText;
        Update(result);
    }

    public void Update(CheckResult result)
    {
        Result = result;
        AccessibleName = $"{result.Name}：{StatusText(result.Status)} {result.Detail}";
        Invalidate();
    }

    private static string StatusText(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "通过",
        CheckStatus.Warn => "注意",
        CheckStatus.Fail => "失败",
        CheckStatus.Running => "检查中",
        _ => "未检查",
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = ModernUi.Palette;
        var g = e.Graphics;
        using (var back = new SolidBrush(p.Surface)) g.FillRectangle(back, ClientRectangle);
        using (var line = new Pen(p.Border)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        int s(int v) => LogicalToDeviceUnits(v);
        var icon = new Rectangle(s(14), (Height - s(18)) / 2, s(18), s(18));
        (ButtonGlyph glyph, Color color) = Result.Status switch
        {
            CheckStatus.Pass => (ButtonGlyph.Check, p.Success),
            CheckStatus.Warn => (ButtonGlyph.Warning, p.Warning),
            CheckStatus.Fail => (ButtonGlyph.Cross, p.Danger),
            CheckStatus.Running => (ButtonGlyph.Clock, p.Accent),
            _ => (ButtonGlyph.Clock, p.Faint),
        };
        Glyphs.Draw(g, glyph, icon, color);
        int nameW = s(130);
        TextRenderer.DrawText(g, Result.Name, Font, new Rectangle(icon.Right + s(12), 0, nameW, Height), p.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        using var small = new Font(ModernUi.UiFont, 9F);
        Color detailColor = Result.Status switch { CheckStatus.Warn => p.Warning, CheckStatus.Fail => p.Danger, _ => p.Muted };
        TextRenderer.DrawText(g, Result.Detail, small, new Rectangle(icon.Right + s(12) + nameW, 0, Width - icon.Right - s(24) - nameW, Height), detailColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>诊断: one button that checks the whole chain, the individual self-tests folded below, and
/// a report to send to support.</summary>
internal sealed class DiagnosticsView : CasterView
{
    private readonly ActionButton _back = NewButton("返回", ButtonKind.Ghost, ButtonGlyph.Back);
    private readonly ThemedLabel _heading = NewLabel("诊断", TextRole.Text, 14F, semibold: true);
    private readonly Panel _scroll = new() { AutoScroll = true };
    private readonly CardPanel _checkCard = new();
    private readonly ThemedLabel _checkTitle = NewLabel("一键检查这台电脑能不能投屏", TextRole.Text, 11F, semibold: true);
    private readonly ThemedLabel _checkHint = NewLabel("依次检查屏幕采集、视频编码、声音采集和编码、网络和本地数据，约 15 秒。检查时不要投屏。", TextRole.Muted, 9.5F);
    private readonly ActionButton _run = NewButton("开始检查", ButtonKind.Primary);
    private readonly CardPanel _resultsCard = new() { Radius = 8 };
    private readonly List<CheckRow> _rows = new();
    private readonly ActionButton _detailsToggle = NewButton("单项自检（9 项，分别定位哪一步出了问题）", ButtonKind.Ghost, ButtonGlyph.ChevronDown, 9.5F);
    private readonly FlowLayoutPanel _details = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false, AutoSize = false };
    private readonly ActionButton _export = NewButton("导出诊断报告（发给技术支持）", ButtonKind.Secondary);

    public event Action? BackClicked;
    public event Action? RunClicked;
    public event Action? ExportClicked;

    public DiagnosticsView(IEnumerable<Control> selfTestControls)
    {
        _checkCard.Controls.AddRange(new Control[] { _checkTitle, _checkHint, _run });
        _resultsCard.Visible = false;
        foreach (var control in selfTestControls)
        {
            _details.Controls.Add(control);
            if (control is Label) control.TextChanged += (_, _) => { if (_details.Visible) PerformLayout(); };
        }
        _scroll.Controls.AddRange(new Control[] { _checkCard, _resultsCard, _detailsToggle, _details, _export });
        Controls.AddRange(new Control[] { _back, _heading, _scroll });
        _detailsToggle.TextAlign = ContentAlignment.MiddleLeft;
        _back.Click += (_, _) => BackClicked?.Invoke();
        _run.Click += (_, _) => RunClicked?.Invoke();
        _export.Click += (_, _) => ExportClicked?.Invoke();
        _detailsToggle.Click += (_, _) =>
        {
            _details.Visible = !_details.Visible;
            _detailsToggle.Glyph = _details.Visible ? ButtonGlyph.ChevronUp : ButtonGlyph.ChevronDown;
            PerformLayout();
        };
        OnThemeChanged();
    }

    public void SetRunning(bool running)
    {
        _run.Enabled = !running;
        _run.Text = running ? "检查中…" : "开始检查";
        _export.Enabled = !running;
    }

    public void SetResults(IReadOnlyList<CheckResult> results)
    {
        if (_rows.Count != results.Count)
        {
            foreach (var row in _rows) row.Dispose();
            _rows.Clear();
            foreach (var result in results)
            {
                var row = new CheckRow(result);
                _rows.Add(row);
                _resultsCard.Controls.Add(row);
            }
        }
        else
        {
            for (int i = 0; i < results.Count; i++) _rows[i].Update(results[i]);
        }
        _resultsCard.Visible = results.Count > 0;
        PerformLayout();
    }

    public override void OnThemeChanged()
    {
        _scroll.BackColor = ModernUi.Palette.Background;
        _details.BackColor = ModernUi.Palette.Background;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = S(24);
        _back.Bounds = new Rectangle(S(16), S(16), S(84), S(32));
        _heading.Bounds = new Rectangle(S(108), S(16), S(200), S(32));
        _scroll.Bounds = new Rectangle(0, S(60), ClientSize.Width, Math.Max(S(40), ClientSize.Height - S(60)));

        int scrollbar = _scroll.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
        int w = _scroll.ClientSize.Width - pad * 2 - (scrollbar > 0 ? 0 : 0);
        var offset = _scroll.AutoScrollPosition;
        int y = S(4);
        _checkTitle.Bounds = new Rectangle(S(16), S(14), w - S(32), S(24));
        _checkHint.Bounds = new Rectangle(S(16), S(40), w - S(32), S(40));
        _run.Bounds = new Rectangle(S(16), S(86), S(140), S(38));
        _checkCard.Bounds = new Rectangle(pad + offset.X, y + offset.Y, w, S(138));
        y += S(138) + S(12);

        if (_resultsCard.Visible)
        {
            int rowH = S(40);
            for (int i = 0; i < _rows.Count; i++) _rows[i].Bounds = new Rectangle(S(1), S(1) + i * rowH, w - S(2), rowH);
            _resultsCard.Bounds = new Rectangle(pad + offset.X, y + offset.Y, w, _rows.Count * rowH + S(2));
            y += _resultsCard.Height + S(12);
        }

        _detailsToggle.Bounds = new Rectangle(pad + offset.X, y + offset.Y, w, S(34));
        y += S(38);
        if (_details.Visible)
        {
            int detailsH = 0;
            foreach (Control control in _details.Controls)
            {
                control.Width = w - S(8);
                if (control is Label label)
                {
                    var size = TextRenderer.MeasureText(label.Text.Length == 0 ? " " : label.Text, label.Font, new Size(control.Width, int.MaxValue), TextFormatFlags.WordBreak);
                    control.Height = label.Text.Length == 0 ? S(4) : size.Height + S(6);
                }
                else control.Height = S(34);
                control.Margin = new Padding(0, 0, 0, S(4));
                detailsH += control.Height + S(4);
            }
            _details.Bounds = new Rectangle(pad + offset.X, y + offset.Y, w, detailsH);
            y += detailsH + S(8);
        }
        _export.Bounds = new Rectangle(pad + offset.X, y + offset.Y, w, S(40));
        y += S(40) + S(20);
        _scroll.AutoScrollMinSize = new Size(0, y);
    }
}
