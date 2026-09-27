using System.Drawing.Drawing2D;
using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>What an <see cref="ActivityRowView"/> needs from the panel that owns it. The row is a pure
/// view: selection, playback and every edit stay in <see cref="ActivitiesPanel"/> (and the hidden
/// TreeView it keeps as its selection model) — the row only reports which node was clicked.</summary>
internal interface IActivityRowHost
{
    bool IsSelected(TreeNode node);
    bool IsMultiSelected(TreeNode node);
    Activity? PlayingActivity { get; }
    MediaFile? PlayingFile { get; }
    Image? ThumbnailFor(MediaFile file);
    void RowNodeClicked(TreeNode node);
    void RowNodeDoubleClicked(TreeNode node);
    void RowContextMenu(TreeNode node, Control source, Point location);
    void ToggleCollapsed(Activity activity);
}

/// <summary>
/// One activity in the 活动节目单, drawn to match the reference mockup's row: number badge ·
/// thumbnail with a type badge · name / play mode · file tags · file count · status · expand chevron.
/// Collapsed (<see cref="Activity.IsCollapsed"/>) it shows as many file tags inline as fit plus a
/// "+N" tag; expanded it lists every file as a selectable card underneath (click selects, Ctrl+click
/// multi-selects, double-click plays, right-click opens the panel's edit menu).
///
/// Owner-drawn as a single control rather than nested Labels/Buttons: the old card built one WinForms
/// Button per file, which could not render the reference's compact tag shape at all. Colours are read
/// from <see cref="ModernUi"/> at paint time, so all three themes apply without rebuilding.
/// </summary>
internal sealed class ActivityRowView : Control
{
    private static readonly Font TitleFont = new("Segoe UI Semibold", 10.5F);
    private static readonly Font BodyFont = new("Segoe UI", 9F);
    private static readonly Font SmallFont = new("Segoe UI", 8.5F);
    private static readonly Font ChipTitleFont = new("Segoe UI Semibold", 9F);
    private static readonly Font BadgeFont = new("Segoe UI", 7.5F, FontStyle.Bold);
    private static readonly Font NumberFont = new("Segoe UI Semibold", 9.5F);

    private readonly IActivityRowHost _host;
    private readonly Activity _activity;
    private readonly TreeNode _activityNode;
    private readonly int _number;

    private readonly List<(Rectangle Bounds, TreeNode Node)> _chips = new();
    private Rectangle _badgeRect, _thumbRect, _titleRect, _subtitleRect, _countRect, _statusRect, _chevronRect;
    private Rectangle _moreRect, _emptyRect;
    private int _moreCount;
    private Point _hover = new(-1, -1);

    public ActivityRowView(IActivityRowHost host, Activity activity, TreeNode activityNode, int number)
    {
        _host = host;
        _activity = activity;
        _activityNode = activityNode;
        _number = number;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 0, 10);
        AccessibleName = $"活动 {number}. {activity.Name}";
    }

    public Activity Activity => _activity;

    private int S(int value) => LogicalToDeviceUnits(value);
    private bool Wide => Width >= S(820);
    private IEnumerable<TreeNode> FileNodes => _activityNode.Nodes.Cast<TreeNode>();

    /// <summary>Sets the width and grows/shrinks to the height that width needs (expanded rows wrap
    /// their file cards, so height depends on width).</summary>
    public void FitToWidth(int width)
    {
        Width = width;
        Height = ComputeLayout(width);
    }

    private int ComputeLayout(int w)
    {
        _chips.Clear();
        _moreRect = _emptyRect = Rectangle.Empty;
        _moreCount = 0;

        var thumb = Wide ? new Size(S(120), S(64)) : new Size(S(96), S(54));
        int headerHeight = thumb.Height + S(20);
        int cy = headerHeight / 2;
        _badgeRect = new Rectangle(S(14), cy - S(13), S(26), S(26));
        _thumbRect = new Rectangle(_badgeRect.Right + S(14), cy - thumb.Height / 2, thumb.Width, thumb.Height);
        int titleWidth = Wide ? S(170) : S(120);
        _titleRect = new Rectangle(_thumbRect.Right + S(16), cy - S(22), titleWidth, S(22));
        _subtitleRect = new Rectangle(_titleRect.Left, cy + S(1), titleWidth, S(20));
        _chevronRect = new Rectangle(w - S(14) - S(24), cy - S(12), S(24), S(24));
        _statusRect = new Rectangle(_chevronRect.Left - S(10) - S(76), cy - S(11), S(76), S(22));
        // Narrow rows drop the file-count column: the "+N" tag and the expanded list already carry it,
        // and without the room it frees not even one file tag fits.
        _countRect = Wide ? new Rectangle(_statusRect.Left - S(6) - S(78), cy - S(11), S(78), S(22)) : Rectangle.Empty;

        var nodes = FileNodes.ToList();
        if (_activity.IsCollapsed)
        {
            // Reference: the row itself carries the activity's file tags — as many as fit, then "+N".
            int x = _titleRect.Right + S(12), right = (_countRect.IsEmpty ? _statusRect.Left : _countRect.Left) - S(10);
            for (int i = 0; i < nodes.Count; i++)
            {
                var file = (MediaFile)nodes[i].Tag!;
                int wanted = Math.Min(S(176), TextRenderer.MeasureText(Path.GetFileName(file.SourcePath), BodyFont).Width + S(42));
                int reserveForMore = i < nodes.Count - 1 ? S(48) : 0; // 8px gap + the 40px "+N" tag
                // Shrink a tag (its name ellipsizes) rather than drop it, down to a still-readable width.
                int chipWidth = Math.Min(wanted, right - x - reserveForMore);
                if (chipWidth < Math.Min(wanted, S(84)))
                {
                    _moreCount = nodes.Count - i;
                    if (x + S(40) <= right) _moreRect = new Rectangle(x, cy - S(16), S(40), S(32));
                    break;
                }
                _chips.Add((new Rectangle(x, cy - S(16), chipWidth, S(32)), nodes[i]));
                x += chipWidth + S(8);
            }
            return headerHeight;
        }

        // Expanded: every file as a card, wrapping under the thumbnail column.
        int left = _thumbRect.Left, maxRight = w - S(14), top = headerHeight;
        if (nodes.Count == 0)
        {
            _emptyRect = new Rectangle(left, top, Math.Max(S(40), maxRight - left), S(36));
            return top + S(36) + S(12);
        }
        int cardWidth = Math.Min(S(236), maxRight - left), cardHeight = S(46), gap = S(8);
        int cx = left, y = top;
        foreach (var node in nodes)
        {
            if (cx + cardWidth > maxRight && cx > left) { cx = left; y += cardHeight + gap; }
            _chips.Add((new Rectangle(cx, y, cardWidth, cardHeight), node));
            cx += cardWidth + gap;
        }
        return y + cardHeight + S(12);
    }

    private TreeNode HitNode(Point p)
    {
        foreach (var (bounds, node) in _chips)
            if (bounds.Contains(p)) return node;
        return _activityNode;
    }

    private bool IsToggleHit(Point p) => _chevronRect.Contains(p) || _moreRect.Contains(p);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (IsToggleHit(e.Location))
        {
            // Rebuilds the list (the host disposes this control) — nothing else may touch it afterwards.
            if (e.Button == MouseButtons.Left) _host.ToggleCollapsed(_activity);
            return;
        }
        var node = HitNode(e.Location);
        if (e.Button == MouseButtons.Left) _host.RowNodeClicked(node);
        else if (e.Button == MouseButtons.Right) _host.RowContextMenu(node, this, e.Location);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && !IsToggleHit(e.Location)) _host.RowNodeDoubleClicked(HitNode(e.Location));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hover = e.Location;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = new Point(-1, -1);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var nodes = FileNodes.ToList();
        bool selected = _host.IsSelected(_activityNode) || nodes.Any(n => _host.IsSelected(n) || _host.IsMultiSelected(n));
        bool playing = ReferenceEquals(_host.PlayingActivity, _activity);

        // Row surface — prototype .act-row / .act-row.sel.
        var surface = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = RoundedBox(surface, S(12)))
        {
            using var fill = new SolidBrush(selected ? Blend(ModernUi.Background, ModernUi.Accent, 0.12f) : ModernUi.Background);
            g.FillPath(fill, path);
            using var border = new Pen(selected ? Color.FromArgb(150, ModernUi.Accent) : ModernUi.Border, 1F);
            g.DrawPath(border, path);
        }

        // Number badge: brand blue while this is the selected/playing activity, outlined otherwise.
        bool strong = selected || playing;
        using (var fill = new SolidBrush(strong ? ModernUi.Accent : Blend(ModernUi.Background, ModernUi.Text, 0.06f)))
            g.FillEllipse(fill, _badgeRect);
        if (!strong) { using var pen = new Pen(ModernUi.Border); g.DrawEllipse(pen, _badgeRect); }
        TextRenderer.DrawText(g, _number.ToString(), NumberFont, _badgeRect, strong ? Color.White : ModernUi.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        DrawThumbnail(g, nodes.Select(n => (MediaFile)n.Tag!).ToList());

        TextRenderer.DrawText(g, _activity.Name, TitleFont, _titleRect, ModernUi.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        string mode = _activity.DefaultPlayMode == PlayMode.SequentialAuto ? "顺序自动播放" : "手动点选播放";
        TextRenderer.DrawText(g, mode, BodyFont, _subtitleRect, ModernUi.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // File count — the data model has no durations to total, unlike the mockup's clock column.
        if (!_countRect.IsEmpty)
        {
            var stack = new Rectangle(_countRect.Left, _countRect.Top + (_countRect.Height - S(14)) / 2, S(14), S(14));
            DrawStackGlyph(g, stack, ModernUi.Muted);
            TextRenderer.DrawText(g, $"{nodes.Count} 个文件", BodyFont,
                Rectangle.FromLTRB(stack.Right + S(6), _countRect.Top, _countRect.Right, _countRect.Bottom), ModernUi.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        // Status: green while this activity is the one playing.
        var dot = new Rectangle(_statusRect.Left, _statusRect.Top + (_statusRect.Height - S(8)) / 2, S(8), S(8));
        using (var dotBrush = new SolidBrush(playing ? ModernUi.Success : ModernUi.Muted))
            g.FillEllipse(dotBrush, dot);
        TextRenderer.DrawText(g, playing ? "播放中" : "未播放", BodyFont,
            Rectangle.FromLTRB(dot.Right + S(7), _statusRect.Top, _statusRect.Right, _statusRect.Bottom),
            playing ? ModernUi.Success : ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        DrawChevron(g, _chevronRect, up: !_activity.IsCollapsed, hot: _chevronRect.Contains(_hover));

        foreach (var (bounds, node) in _chips)
        {
            if (_activity.IsCollapsed) DrawInlineTag(g, bounds, node);
            else DrawFileCard(g, bounds, node);
        }
        if (!_moreRect.IsEmpty)
        {
            using var path = RoundedBox(_moreRect, S(8));
            using var fill = new SolidBrush(Blend(ModernUi.Background, ModernUi.Text, _moreRect.Contains(_hover) ? 0.10f : 0.05f));
            g.FillPath(fill, path);
            TextRenderer.DrawText(g, $"+{_moreCount}", BodyFont, _moreRect, ModernUi.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (!_emptyRect.IsEmpty)
        {
            using var path = RoundedBox(_emptyRect, S(8));
            using var pen = new Pen(ModernUi.Border) { DashStyle = DashStyle.Dash };
            g.DrawPath(pen, path);
            TextRenderer.DrawText(g, "还没有文件 · 右键此活动或「编辑 ▾」→ 添加文件…", SmallFont, _emptyRect, ModernUi.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -3, -3), ModernUi.Text, ModernUi.Background);
    }

    /// <summary>Representative picture for the row: the first visual (non-background-audio) file.</summary>
    private void DrawThumbnail(Graphics g, List<MediaFile> files)
    {
        var file = files.FirstOrDefault(f => f.Kind != MediaKind.Audio && !f.IsBackgroundAudio) ?? files.FirstOrDefault();
        using var clip = RoundedBox(_thumbRect, S(8));
        var image = file == null ? null : _host.ThumbnailFor(file);
        var state = g.Save();
        g.SetClip(clip);
        if (image != null)
        {
            // Cover-fit: fill the frame, cropping whichever axis overflows.
            float scale = Math.Max((float)_thumbRect.Width / image.Width, (float)_thumbRect.Height / image.Height);
            float dw = image.Width * scale, dh = image.Height * scale;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, _thumbRect.Left + (_thumbRect.Width - dw) / 2, _thumbRect.Top + (_thumbRect.Height - dh) / 2, dw, dh);
        }
        else
        {
            using var fill = new LinearGradientBrush(_thumbRect, Blend(ModernUi.Background, ModernUi.Accent, 0.35f),
                Blend(ModernUi.Background, ModernUi.Accent, 0.08f), 45F);
            g.FillRectangle(fill, _thumbRect);
            if (file == null)
                TextRenderer.DrawText(g, "空", BodyFont, _thumbRect, ModernUi.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        g.Restore(state);
        using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255))) g.DrawPath(pen, clip);
        if (file == null) return;

        if (file.Kind == MediaKind.Video)
        {
            int d = S(26);
            var play = new Rectangle(_thumbRect.Left + (_thumbRect.Width - d) / 2, _thumbRect.Top + (_thumbRect.Height - d) / 2, d, d);
            using (var bg = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillEllipse(bg, play);
            using var glyph = new SolidBrush(Color.White);
            float px = play.Left + d * 0.40f, py = play.Top + d * 0.30f;
            g.FillPolygon(glyph, new[] { new PointF(px, py), new PointF(px, play.Bottom - d * 0.30f), new PointF(play.Right - d * 0.28f, play.Top + d / 2f) });
        }

        var (label, color) = KindBadge(file);
        var size = TextRenderer.MeasureText(label, BadgeFont, Size.Empty, TextFormatFlags.NoPadding);
        var badge = new Rectangle(_thumbRect.Left + S(6), _thumbRect.Bottom - S(6) - S(16), size.Width + S(10), S(16));
        using (var path = RoundedBox(badge, S(4)))
        using (var fill = new SolidBrush(color))
            g.FillPath(fill, path);
        TextRenderer.DrawText(g, label, BadgeFont, badge, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawInlineTag(Graphics g, Rectangle bounds, TreeNode node)
    {
        var file = (MediaFile)node.Tag!;
        bool selected = _host.IsSelected(node) || _host.IsMultiSelected(node);
        bool hot = bounds.Contains(_hover);
        using (var path = RoundedBox(bounds, S(8)))
        {
            using var fill = new SolidBrush(Blend(ModernUi.Background, ModernUi.Text, hot ? 0.09f : 0.05f));
            g.FillPath(fill, path);
            using var pen = new Pen(selected ? ModernUi.Accent : Blend(ModernUi.Background, ModernUi.Text, 0.10f), selected ? 1.5F : 1F);
            g.DrawPath(pen, path);
        }
        var icon = new Rectangle(bounds.Left + S(10), bounds.Top + (bounds.Height - S(16)) / 2, S(16), S(16));
        DrawKindIcon(g, icon, file);
        bool playingFile = ReferenceEquals(_host.PlayingFile, file);
        TextRenderer.DrawText(g, (playingFile ? "▶ " : "") + Path.GetFileName(file.SourcePath), BodyFont,
            Rectangle.FromLTRB(icon.Right + S(7), bounds.Top, bounds.Right - S(8), bounds.Bottom),
            playingFile ? ModernUi.Success : ModernUi.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void DrawFileCard(Graphics g, Rectangle bounds, TreeNode node)
    {
        var file = (MediaFile)node.Tag!;
        bool selected = _host.IsSelected(node);
        bool multi = _host.IsMultiSelected(node);
        bool hot = bounds.Contains(_hover);
        using (var path = RoundedBox(bounds, S(9)))
        {
            Color fill = selected || multi ? Blend(ModernUi.Surface, ModernUi.Accent, 0.14f)
                : Blend(ModernUi.Surface, ModernUi.Text, hot ? 0.06f : 0.02f);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            using var pen = new Pen(selected || multi ? ModernUi.Accent : ModernUi.Border, selected || multi ? 1.5F : 1F);
            g.DrawPath(pen, path);
        }
        var icon = new Rectangle(bounds.Left + S(10), bounds.Top + (bounds.Height - S(22)) / 2, S(22), S(22));
        DrawKindIcon(g, icon, file);
        bool playingFile = ReferenceEquals(_host.PlayingFile, file);
        int textLeft = icon.Right + S(9), textRight = bounds.Right - (multi ? S(26) : S(8));
        TextRenderer.DrawText(g, (playingFile ? "▶ " : "") + Path.GetFileName(file.SourcePath), ChipTitleFont,
            Rectangle.FromLTRB(textLeft, bounds.Top + S(5), textRight, bounds.Top + S(24)),
            playingFile ? ModernUi.Success : ModernUi.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, FileMeta(file), SmallFont,
            Rectangle.FromLTRB(textLeft, bounds.Top + S(24), textRight, bounds.Bottom - S(4)), ModernUi.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (multi)
        {
            // Ctrl+click multi-selection (for 统一设置属性…) gets a check mark, distinct from plain selection.
            var check = new Rectangle(bounds.Right - S(22), bounds.Top + (bounds.Height - S(16)) / 2, S(16), S(16));
            using (var fill = new SolidBrush(ModernUi.Accent)) g.FillEllipse(fill, check);
            using var pen = new Pen(Color.White, 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(check.Left + check.Width * 0.27f, check.Top + check.Height * 0.52f),
                new PointF(check.Left + check.Width * 0.44f, check.Top + check.Height * 0.68f),
                new PointF(check.Left + check.Width * 0.74f, check.Top + check.Height * 0.34f),
            });
        }
    }

    private static string FileMeta(MediaFile file)
    {
        if (file.IsBackgroundAudio) return "背景音频 · 叠加循环";
        if (file.Kind == MediaKind.Document && WpsDocumentController.IsOfficeDocument(file.SourcePath)) return "WPS 打开 · 手动切换";
        if (file.Kind is MediaKind.Image or MediaKind.Document)
            return file.StayDuration is { } stay ? $"停留 {stay.TotalSeconds:0} 秒" : "手动翻页";
        return file.OnCompletion switch
        {
            CompletionAction.Loop => "循环播放",
            CompletionAction.HoldOnLastFrame => "播完停在最后一帧",
            _ => "播完自动下一项",
        };
    }

    /// <summary>Type label + colour, as on the mockup's thumbnails (PPT orange, PDF red, DOC blue…).</summary>
    private static (string Label, Color Color) KindBadge(MediaFile file)
    {
        string ext = Path.GetExtension(file.SourcePath).TrimStart('.').ToUpperInvariant();
        return ext switch
        {
            "PPT" or "PPTX" => ("PPT", Color.FromArgb(232, 98, 46)),
            "PDF" => ("PDF", Color.FromArgb(226, 59, 59)),
            "DOC" or "DOCX" => ("DOC", Color.FromArgb(46, 107, 224)),
            "XLS" or "XLSX" => ("XLS", Color.FromArgb(33, 140, 84)),
            _ when file.Kind == MediaKind.Video => (ext.Length is > 0 and <= 4 ? ext : "视频", Color.FromArgb(170, 0, 0, 0)),
            _ when file.Kind == MediaKind.Audio => (ext.Length is > 0 and <= 4 ? ext : "音频", Color.FromArgb(107, 75, 208)),
            _ => (ext.Length is > 0 and <= 4 ? ext : "文件", Color.FromArgb(30, 111, 224)),
        };
    }

    /// <summary>Small coloured file-type glyphs, following the prototype's fileTypeIcon().</summary>
    private void DrawKindIcon(Graphics g, Rectangle r, MediaFile file)
    {
        var (_, color) = KindBadge(file);
        if (file.Kind == MediaKind.Audio)
        {
            using (var fill = new SolidBrush(Color.FromArgb(107, 75, 208))) g.FillEllipse(fill, r);
            using var note = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, Math.Max(1.2F, r.Width / 14f));
            float stemX = r.Left + r.Width * 0.60f;
            g.DrawLine(pen, stemX, r.Top + r.Height * 0.26f, stemX, r.Top + r.Height * 0.66f);
            g.DrawLine(pen, stemX, r.Top + r.Height * 0.26f, r.Left + r.Width * 0.74f, r.Top + r.Height * 0.34f);
            g.FillEllipse(note, r.Left + r.Width * 0.34f, r.Top + r.Height * 0.56f, r.Width * 0.30f, r.Height * 0.24f);
            return;
        }
        Color tile = file.Kind switch
        {
            MediaKind.Video => Color.FromArgb(28, 43, 70),
            MediaKind.Image => Color.FromArgb(28, 53, 96),
            _ => color,
        };
        using (var path = RoundedBox(r, Math.Max(2, r.Width / 6)))
        using (var fill = new SolidBrush(tile))
            g.FillPath(fill, path);
        Color ink = file.Kind is MediaKind.Video or MediaKind.Image ? Color.FromArgb(127, 208, 255) : Color.White;
        using var glyphPen = new Pen(ink, Math.Max(1.2F, r.Width / 14f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (file.Kind == MediaKind.Video)
        {
            using var tri = new SolidBrush(ink);
            g.FillPolygon(tri, new[]
            {
                new PointF(r.Left + r.Width * 0.40f, r.Top + r.Height * 0.30f),
                new PointF(r.Left + r.Width * 0.40f, r.Top + r.Height * 0.70f),
                new PointF(r.Left + r.Width * 0.70f, r.Top + r.Height * 0.50f),
            });
        }
        else if (file.Kind == MediaKind.Image)
        {
            g.DrawLines(glyphPen, new[]
            {
                new PointF(r.Left + r.Width * 0.18f, r.Top + r.Height * 0.76f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.50f),
                new PointF(r.Left + r.Width * 0.58f, r.Top + r.Height * 0.64f),
                new PointF(r.Left + r.Width * 0.82f, r.Top + r.Height * 0.40f),
            });
        }
        else
        {
            for (int i = 0; i < 3; i++)
            {
                float y = r.Top + r.Height * (0.34f + i * 0.17f);
                g.DrawLine(glyphPen, r.Left + r.Width * 0.26f, y, r.Left + r.Width * (i == 2 ? 0.56f : 0.74f), y);
            }
        }
    }

    private void DrawStackGlyph(Graphics g, Rectangle r, Color color)
    {
        using var pen = new Pen(color, 1.4F) { LineJoin = LineJoin.Round };
        var back = new Rectangle(r.Left + S(3), r.Top, r.Width - S(3), r.Height - S(3));
        var front = new Rectangle(r.Left, r.Top + S(3), r.Width - S(3), r.Height - S(3));
        using (var backPath = RoundedBox(back, S(2))) g.DrawPath(pen, backPath);
        using var bg = new SolidBrush(ModernUi.Background);
        using var frontPath = RoundedBox(front, S(2));
        g.FillPath(bg, frontPath);
        g.DrawPath(pen, frontPath);
    }

    private void DrawChevron(Graphics g, Rectangle r, bool up, bool hot)
    {
        if (hot)
        {
            using var fill = new SolidBrush(Blend(ModernUi.Background, ModernUi.Text, 0.08f));
            g.FillEllipse(fill, r);
        }
        using var pen = new Pen(hot ? ModernUi.Text : ModernUi.Muted, 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f, dx = S(5), dy = S(3);
        g.DrawLines(pen, up
            ? new[] { new PointF(cx - dx, cy + dy), new PointF(cx, cy - dy), new PointF(cx + dx, cy + dy) }
            : new[] { new PointF(cx - dx, cy - dy), new PointF(cx, cy + dy), new PointF(cx + dx, cy - dy) });
    }

    internal static Color Blend(Color baseColor, Color overlay, float amount) => Color.FromArgb(
        (int)(baseColor.R + (overlay.R - baseColor.R) * amount),
        (int)(baseColor.G + (overlay.G - baseColor.G) * amount),
        (int)(baseColor.B + (overlay.B - baseColor.B) * amount));

    internal static GraphicsPath RoundedBox(Rectangle r, int radius)
    {
        int d = Math.Min(Math.Max(2, radius * 2), Math.Max(2, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
