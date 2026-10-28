using EveryStage.Terminal.Data;
using EveryStage.Terminal.Documents;

namespace EveryStage.Terminal.UI;

/// <summary>
/// Edits a document item's <see cref="DocumentPlaybackSettings"/> (activity entries and library files):
/// <list type="bullet">
/// <item>PDF: reading mode, start page and position inside it, end page, zoom, live sync, automatic
///   scrolling / page interval.</item>
/// <item>Word: editor (WPS / Microsoft Word / read-only preview), allow editing, live sync, initial scroll.</item>
/// <item>Presentation: native / static / automatic, start and end slide, automatic advance, loop, live sync.</item>
/// </list>
/// The completion action stays the item's own 完成后动作 setting.
/// </summary>
public sealed class DocumentSettingsDialog : Form
{
    private readonly DocumentFormat _format;
    private readonly DocumentPlaybackSettings _settings;
    private readonly ComboBox? _readingMode, _zoomMode, _openMode, _presentationMode;
    private readonly NumericUpDown? _startPage, _startOffset, _endPage, _customZoom, _scroll;
    private readonly CheckBox? _hasEnd, _allowEdit, _loop;
    private readonly CheckBox _liveSync, _auto;
    private readonly NumericUpDown _autoSeconds;

    public DocumentPlaybackSettings Result { get; private set; }

    public DocumentSettingsDialog(MediaFile file)
    {
        _format = DocumentFormats.Of(file.SourcePath);
        _settings = file.Document?.Clone() ?? new DocumentPlaybackSettings();
        Result = _settings;
        Text = $"文档播放设置 — {Path.GetFileName(file.SourcePath)}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;

        var grid = new TableLayoutPanel { Location = new Point(12, 12), AutoSize = true, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        void Row(string label, Control control)
        {
            control.Margin = new Padding(3, 4, 3, 4);
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
            grid.Controls.Add(control);
        }
        ComboBox Combo(params string[] items)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
            combo.Items.AddRange(items);
            return combo;
        }
        NumericUpDown Number(decimal min, decimal max, decimal value, int decimals = 0) => new()
        {
            Minimum = min, Maximum = max, DecimalPlaces = decimals, Width = 90,
            Value = Math.Clamp(value, min, max), Increment = decimals > 0 ? 0.1m : 1m,
        };

        switch (_format)
        {
            case DocumentFormat.Pdf:
                _readingMode = Combo("连续滚动", "单页翻页");
                _readingMode.SelectedIndex = _settings.ReadingMode == PdfDisplayMode.SinglePage ? 1 : 0;
                Row("阅读模式", _readingMode);
                _startPage = Number(1, 100000, _settings.StartPage);
                Row("起始页", _startPage);
                _startOffset = Number(0, 99, (decimal)Math.Round(_settings.StartOffset * 100));
                Row("起始页内位置 (%)", _startOffset);
                (_hasEnd, _endPage) = EndRow(Row, "播放到第几页结束");
                _zoomMode = Combo("适合宽度", "适合页面", "实际大小", "自定义");
                _zoomMode.SelectedIndex = (int)_settings.ZoomMode;
                Row("缩放", _zoomMode);
                _customZoom = Number(25, 400, (decimal)(_settings.CustomZoom * 100));
                _customZoom.Enabled = _settings.ZoomMode == PdfZoomMode.Custom;
                _zoomMode.SelectedIndexChanged += (_, _) => _customZoom.Enabled = _zoomMode.SelectedIndex == 3;
                Row("自定义比例 (%)", _customZoom);
                break;
            case DocumentFormat.Word:
                _openMode = Combo("自动（优先 WPS）", "WPS 文字", "Microsoft Word", "只读预览（不打开编辑器）");
                _openMode.SelectedIndex = (int)_settings.OpenMode;
                Row("打开方式", _openMode);
                _allowEdit = new CheckBox { Text = "允许在投屏时编辑", AutoSize = true, Checked = _settings.AllowEdit };
                Row("编辑", _allowEdit);
                _scroll = Number(0, 100, (decimal)_settings.InitialScrollPercent);
                Row("初始滚动位置 (%)", _scroll);
                break;
            case DocumentFormat.Presentation:
                _presentationMode = Combo("自动（有动画/转场/媒体时原生放映）", "原生放映", "静态幻灯片");
                _presentationMode.SelectedIndex = (int)_settings.PresentationMode;
                Row("放映方式", _presentationMode);
                _startPage = Number(1, 10000, _settings.StartPage);
                Row("起始幻灯片", _startPage);
                (_hasEnd, _endPage) = EndRow(Row, "播放到第几张结束");
                _loop = new CheckBox { Text = "循环放映", AutoSize = true, Checked = _settings.Loop };
                Row("循环", _loop);
                break;
        }

        _auto = new CheckBox
        {
            Text = _format == DocumentFormat.Pdf ? "自动滚动 / 自动翻页" : _format == DocumentFormat.Presentation ? "自动推进" : "自动推进（不适用）",
            AutoSize = true, Checked = _settings.SecondsPerPage is > 0, Enabled = _format != DocumentFormat.Word,
        };
        _autoSeconds = Number(1, 3600, (decimal)(_settings.SecondsPerPage ?? 10));
        _autoSeconds.Enabled = _auto.Checked && _auto.Enabled;
        _auto.CheckedChanged += (_, _) => _autoSeconds.Enabled = _auto.Checked;
        Row("推进", _auto);
        Row(_format == DocumentFormat.Presentation ? "每张停留 (秒)" : "每页用时 (秒)", _autoSeconds);
        _liveSync = new CheckBox { Text = "大屏实时跟随预览 / 编辑", AutoSize = true, Checked = _settings.LiveSync };
        Row("同步", _liveSync);

        var note = new Label
        {
            Text = _format switch
            {
                DocumentFormat.Presentation => "静态幻灯片不保留动画、转场和嵌入音视频；未安装 PowerPoint/WPS 时自动使用静态模式。播放结束后执行此项的“完成后动作”。",
                DocumentFormat.Word => "编辑器窗口留在操作员屏幕，大屏显示其文档区域的画面。关闭实时同步后，修改只在点“更新大屏”时上屏。",
                _ => "连续滚动按“每页用时”平滑滚动；单页模式按此间隔翻页。播放到结束页后执行此项的“完成后动作”。",
            },
            ForeColor = Color.DimGray, AutoSize = false, Size = new Size(360, 48), Margin = new Padding(3, 6, 3, 3),
        };
        grid.Controls.Add(note);
        grid.SetColumnSpan(note, 2);

        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Size = new Size(80, 28) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(80, 28) };
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(grid);
        Controls.Add(ok);
        Controls.Add(cancel);
        var size = grid.GetPreferredSize(Size.Empty);
        ClientSize = new Size(size.Width + 24, size.Height + 64);
        ok.Location = new Point(ClientSize.Width - 184, ClientSize.Height - 40);
        cancel.Location = new Point(ClientSize.Width - 96, ClientSize.Height - 40);
        ok.Click += (_, _) => Result = Collect();
        ModernUi.StyleDialog(this);
    }

    private (CheckBox, NumericUpDown) EndRow(Action<string, Control> row, string label)
    {
        var hasEnd = new CheckBox { Text = "指定结束", AutoSize = true, Checked = _settings.EndPage.HasValue };
        var end = new NumericUpDown { Minimum = 1, Maximum = 100000, Width = 90, Value = Math.Max(1, _settings.EndPage ?? 1), Enabled = hasEnd.Checked };
        hasEnd.CheckedChanged += (_, _) => end.Enabled = hasEnd.Checked;
        row(label, hasEnd);
        row("", end);
        return (hasEnd, end);
    }

    private DocumentPlaybackSettings Collect()
    {
        var s = _settings.Clone();
        if (_readingMode != null) s.ReadingMode = _readingMode.SelectedIndex == 1 ? PdfDisplayMode.SinglePage : PdfDisplayMode.Continuous;
        if (_startPage != null) s.StartPage = (int)_startPage.Value;
        if (_startOffset != null) s.StartOffset = (double)_startOffset.Value / 100;
        if (_hasEnd != null && _endPage != null) s.EndPage = _hasEnd.Checked ? (int)_endPage.Value : null;
        if (_zoomMode != null) s.ZoomMode = (PdfZoomMode)_zoomMode.SelectedIndex;
        if (_customZoom != null) s.CustomZoom = (double)_customZoom.Value / 100;
        if (_openMode != null) s.OpenMode = (OfficeOpenMode)_openMode.SelectedIndex;
        if (_allowEdit != null) s.AllowEdit = _allowEdit.Checked;
        if (_scroll != null) s.InitialScrollPercent = (double)_scroll.Value;
        if (_presentationMode != null) s.PresentationMode = (PresentationMode)_presentationMode.SelectedIndex;
        if (_loop != null) s.Loop = _loop.Checked;
        s.SecondsPerPage = _auto.Checked && _auto.Enabled ? (double)_autoSeconds.Value : null;
        s.LiveSync = _liveSync.Checked;
        return s;
    }

    /// <summary>One-line summary for the activity row ("连续滚动 · 第 3 页起 · 自动 8 秒/页").</summary>
    public static string Summarize(MediaFile file)
    {
        var s = file.Document ?? new DocumentPlaybackSettings();
        string auto = s.SecondsPerPage is > 0 ? $" · 自动 {s.SecondsPerPage:0.#} 秒/{(DocumentFormats.Of(file.SourcePath) == DocumentFormat.Presentation ? "张" : "页")}" : " · 手动";
        string range = (s.StartPage > 1 ? $" · 第 {s.StartPage} 起" : "") + (s.EndPage is { } end ? $" 至 {end}" : "");
        return DocumentFormats.Of(file.SourcePath) switch
        {
            DocumentFormat.Pdf => (s.ReadingMode == PdfDisplayMode.SinglePage ? "单页翻页" : "连续滚动") + range + auto,
            DocumentFormat.Word => s.OpenMode switch
            {
                OfficeOpenMode.ReadOnlyPreview => "只读预览",
                OfficeOpenMode.MicrosoftOffice => "Word 编辑",
                OfficeOpenMode.Wps => "WPS 编辑",
                _ => "WPS/Word 编辑",
            } + (s.AllowEdit ? "" : " · 只读") + (s.LiveSync ? "" : " · 手动更新大屏"),
            DocumentFormat.Presentation => s.PresentationMode switch
            {
                PresentationMode.Native => "原生放映",
                PresentationMode.Static => "静态幻灯片",
                _ => "自动选择放映方式",
            } + range + auto + (s.Loop ? " · 循环" : ""),
            _ => "封面预览",
        };
    }
}
