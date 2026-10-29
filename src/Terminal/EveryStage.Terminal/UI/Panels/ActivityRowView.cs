using System.Drawing.Drawing2D;
using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>Where a run-sheet row stands in the show.</summary>
internal enum RunRowState { Pending, Done, Next, Live }

/// <summary>What an <see cref="ActivityRowView"/> needs from the panel that owns it. The row is a pure
/// view: selection, playback and every edit stay in <see cref="ActivitiesPanel"/> (and the hidden
/// TreeView it keeps as its selection model) — the row only reports which node was clicked.</summary>
internal interface IActivityRowHost
{
    bool IsSelected(TreeNode node);
    bool IsMultiSelected(TreeNode node);
    RunRowState StateOf(MediaFile file);
    /// <summary>The file shown in the 预览 monitor (a 「预览」 tag on its row), if any.</summary>
    MediaFile? PreviewFile { get; }
    Image? ThumbnailFor(MediaFile file);
    void RowNodeClicked(TreeNode node);
    void RowNodeDoubleClicked(TreeNode node);
    void RowContextMenu(TreeNode node, Control source, Point location);
    void ToggleCollapsed(Activity activity);
}

/// <summary>
/// One section (环节) of the 流程 run sheet, drawn after the design: a header line「1  开场  3 项 ·
/// 顺序自动 ───── ⌃」 and, unless collapsed, one row per item: ⋮⋮ · 序号 (1.1) · thumbnail with a type
/// badge · name / kind · 时长 · 结束后 · 状态 (已播 / ● 大屏播放中 / 下一项 / 待播). The live row is tinted
/// in the danger colour, the next one in the accent colour.
///
/// Click selects, Ctrl+click multi-selects, double-click runs the item, right-click opens the panel's
/// edit menu. Owner-drawn as a single control; colours are read from <see cref="ModernUi"/> at paint
/// time, so every theme applies without rebuilding.
/// </summary>
internal sealed class ActivityRowView : Control
{
    private static readonly Font SectionFont = new(ModernUi.UiFontSemibold, 11.5F);
    private static readonly Font SectionNumberFont = new(ModernUi.UiFontSemibold, 10F);
    private static readonly Font NameFont = new(ModernUi.UiFont, 10F);
    private static readonly Font BodyFont = new(ModernUi.UiFont, 9F);
    private static readonly Font SmallFont = new(ModernUi.UiFont, 8.5F);
    private static readonly Font StatusFont = new(ModernUi.UiFontSemibold, 8.5F);
    private static readonly Font BadgeFont = new(ModernUi.UiFont, 7F, FontStyle.Bold);

    private readonly IActivityRowHost _host;
    private readonly Activity _activity;
    private readonly TreeNode _activityNode;
    private readonly int _number;

    private readonly List<(Rectangle Bounds, TreeNode Node, int Index)> _rows = new();
    private Rectangle _headerRect, _chevronRect, _emptyRect;
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
        Margin = new Padding(0, 0, 0, 4);
        AccessibleName = $"环节 {number}. {activity.Name}";
    }

    public Activity Activity => _activity;

    private int S(int value) => LogicalToDeviceUnits(value);
    private IEnumerable<TreeNode> FileNodes => _activityNode.Nodes.Cast<TreeNode>();

    /// <summary>Column layout shared with the panel's column-heading row, so both line up.</summary>
    internal readonly record struct Columns(int Handle, int Number, int Thumb, int Name, int NameRight, int Duration, int After, int Status, bool Wide);

    internal static Columns ColumnsFor(int width, Func<int, int> s)
    {
        bool wide = width >= s(700);
        int x = s(12);
        int handle = x; x += s(22) + s(8);
        int number = x; x += s(40) + s(8);
        int thumb = x; x += s(76) + s(12);
        int name = x;
        int right = width - s(12);
        int status = right - s(104);
        int after = wide ? status - s(12) - s(84) : status;
        int duration = wide ? after - s(12) - s(96) : status;
        return new Columns(handle, number, thumb, name, duration - s(12), duration, after, status, wide);
    }

    /// <summary>Sets the width and grows/shrinks to the height the section needs.</summary>
    public void FitToWidth(int width)
    {
        Width = width;
        Height = ComputeLayout(width);
    }

    private int ComputeLayout(int w)
    {
        _rows.Clear();
        _emptyRect = Rectangle.Empty;
        _headerRect = new Rectangle(0, 0, w, S(44));
        _chevronRect = new Rectangle(w - S(12) - S(24), S(14), S(24), S(24));
        int y = _headerRect.Bottom;
        if (_activity.IsCollapsed) return y + S(2);

        var nodes = FileNodes.ToList();
        if (nodes.Count == 0)
        {
            _emptyRect = new Rectangle(S(12), y, Math.Max(S(40), w - S(24)), S(40));
            return _emptyRect.Bottom + S(6);
        }
        int rowHeight = S(56), gap = S(4);
        for (int i = 0; i < nodes.Count; i++)
        {
            _rows.Add((new Rectangle(0, y, w - 1, rowHeight), nodes[i], i));
            y += rowHeight + gap;
        }
        return y + S(2);
    }

    private TreeNode HitNode(Point p)
    {
        foreach (var (bounds, node, _) in _rows)
            if (bounds.Contains(p)) return node;
        return _activityNode;
    }


    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_chevronRect.Contains(e.Location))
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
        if (e.Button != MouseButtons.Left || _chevronRect.Contains(e.Location)) return;
        if (_headerRect.Contains(e.Location))
        {
            _host.ToggleCollapsed(_activity);
            return;
        }
        _host.RowNodeDoubleClicked(HitNode(e.Location));
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
        bool sectionSelected = _host.IsSelected(_activityNode);

        // Section header: number · name · meta ───── chevron.
        int cy = _headerRect.Top + S(26);
        var numberRect = new Rectangle(S(12), cy - S(11), S(28), S(22));
        TextRenderer.DrawText(g, _number.ToString(), SectionNumberFont, numberRect, ModernUi.AccentText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        int nameWidth = Math.Min(TextRenderer.MeasureText(_activity.Name, SectionFont).Width, Math.Max(S(40), Width / 2));
        var nameRect = new Rectangle(numberRect.Left + S(22), cy - S(12), nameWidth, S(24));
        TextRenderer.DrawText(g, _activity.Name, SectionFont, nameRect, sectionSelected ? ModernUi.AccentText : ModernUi.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        string meta = $"{nodes.Count} 项 · {(_activity.DefaultPlayMode == PlayMode.SequentialAuto ? "顺序自动" : "手动点选")}"
            + (_activity.IsCollapsed ? " · 已收起" : "");
        int metaWidth = TextRenderer.MeasureText(meta, BodyFont).Width;
        var metaRect = new Rectangle(nameRect.Right + S(10), cy - S(10), metaWidth, S(20));
        TextRenderer.DrawText(g, meta, BodyFont, metaRect, ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        using (var line = new Pen(ModernUi.Mix(ModernUi.Background, ModernUi.Border, 0.5f)))
            if (_chevronRect.Left - S(10) > metaRect.Right + S(10))
                g.DrawLine(line, metaRect.Right + S(10), cy, _chevronRect.Left - S(10), cy);
        DrawChevron(g, _chevronRect, up: !_activity.IsCollapsed, hot: _chevronRect.Contains(_hover));
        if (sectionSelected && Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(_headerRect, -3, -3), ModernUi.Text, ModernUi.Background);

        var columns = ColumnsFor(Width, S);
        var preview = _host.PreviewFile;
        foreach (var (bounds, node, index) in _rows)
            DrawItemRow(g, bounds, node, index, columns, preview);

        if (!_emptyRect.IsEmpty)
        {
            using var path = RoundedBox(_emptyRect, S(8));
            using var pen = new Pen(ModernUi.Border) { DashStyle = DashStyle.Dash };
            g.DrawPath(pen, path);
            TextRenderer.DrawText(g, "这个环节还没有文件 · 右键或「＋ 添加文件」", SmallFont, _emptyRect, ModernUi.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private void DrawItemRow(Graphics g, Rectangle bounds, TreeNode node, int index, Columns c, MediaFile? preview)
    {
        var file = (MediaFile)node.Tag!;
        var state = _host.StateOf(file);
        bool selected = _host.IsSelected(node), multi = _host.IsMultiSelected(node);
        bool hot = bounds.Contains(_hover);

        Color fill = state switch
        {
            RunRowState.Live => ModernUi.Mix(ModernUi.Background, ModernUi.Danger, 0.16f),
            RunRowState.Next => ModernUi.Mix(ModernUi.Background, ModernUi.Accent, 0.14f),
            _ => hot ? ModernUi.Mix(ModernUi.Surface, ModernUi.Text, 0.04f) : ModernUi.Surface,
        };
        Color border = state switch
        {
            RunRowState.Live => ModernUi.Danger,
            RunRowState.Next => ModernUi.Accent,
            _ when selected || multi => ModernUi.Accent,
            _ => ModernUi.Mix(ModernUi.Surface, ModernUi.Border, 0.45f),
        };
        using (var path = RoundedBox(bounds, S(8)))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            using var pen = new Pen(border, selected || multi ? 1.8F : 1F);
            g.DrawPath(pen, path);
        }

        bool done = state == RunRowState.Done;
        Color text = done ? ModernUi.Muted : ModernUi.Text;
        int cy = bounds.Top + bounds.Height / 2;

        // ⋮⋮ grip (reorder through the menu's 上移/下移 or Alt+↑/↓).
        using (var dots = new SolidBrush(ModernUi.Mix(ModernUi.Surface, ModernUi.Muted, 0.7f)))
            for (int col = 0; col < 2; col++)
                for (int r = -1; r <= 1; r++)
                    g.FillEllipse(dots, c.Handle + S(7) + col * S(6), cy + r * S(5) - S(1), S(3), S(3));

        TextRenderer.DrawText(g, $"{_number}.{index + 1}", BodyFont, new Rectangle(c.Number, bounds.Top, S(40), bounds.Height),
            ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        var thumb = new Rectangle(c.Thumb, cy - S(22), S(76), S(43));
        DrawThumbnail(g, thumb, file);
        if (ReferenceEquals(preview, file) && state != RunRowState.Live)
        {
            var tag = new Rectangle(thumb.Left + S(3), thumb.Top + S(3), S(30), S(15));
            using (var path = RoundedBox(tag, S(3)))
            using (var brush = new SolidBrush(ModernUi.Accent))
                g.FillPath(brush, path);
            TextRenderer.DrawText(g, "预览", BadgeFont, tag, ModernUi.AccentForeground,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        int nameRight = c.Wide ? c.NameRight : c.Status - S(12);
        TextRenderer.DrawText(g, Path.GetFileName(file.SourcePath), NameFont,
            Rectangle.FromLTRB(c.Name, bounds.Top + S(8), nameRight, cy + S(2)), text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, KindLine(file), SmallFont,
            Rectangle.FromLTRB(c.Name, cy + S(2), nameRight, bounds.Bottom - S(8)), ModernUi.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (c.Wide)
        {
            TextRenderer.DrawText(g, DurationText(file), BodyFont, Rectangle.FromLTRB(c.Duration, bounds.Top, c.After - S(12), bounds.Bottom),
                ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, AfterText(file), BodyFont, Rectangle.FromLTRB(c.After, bounds.Top, c.Status - S(12), bounds.Bottom),
                ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        (string status, Color color) = state switch
        {
            RunRowState.Live => ("● 大屏播放中", ModernUi.Danger),
            RunRowState.Next => ("下一项", ModernUi.AccentText),
            RunRowState.Done => ("已播", ModernUi.Muted),
            _ => ("待播", ModernUi.Muted),
        };
        TextRenderer.DrawText(g, status, StatusFont, Rectangle.FromLTRB(c.Status, bounds.Top, bounds.Right - S(8), bounds.Bottom),
            color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (multi)
        {
            // Ctrl+click multi-selection (for 统一设置属性…) gets a check mark, distinct from plain selection.
            var check = new Rectangle(bounds.Right - S(24), cy - S(8), S(16), S(16));
            using (var brush = new SolidBrush(ModernUi.Accent)) g.FillEllipse(brush, check);
            using var pen = new Pen(Color.White, 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(check.Left + check.Width * 0.27f, check.Top + check.Height * 0.52f),
                new PointF(check.Left + check.Width * 0.44f, check.Top + check.Height * 0.68f),
                new PointF(check.Left + check.Width * 0.74f, check.Top + check.Height * 0.34f),
            });
        }
    }

    private void DrawThumbnail(Graphics g, Rectangle rect, MediaFile file)
    {
        using var clip = RoundedBox(rect, S(5));
        var image = _host.ThumbnailFor(file);
        var state = g.Save();
        g.SetClip(clip);
        if (image != null)
        {
            // Cover-fit: fill the frame, cropping whichever axis overflows.
            float scale = Math.Max((float)rect.Width / image.Width, (float)rect.Height / image.Height);
            float dw = image.Width * scale, dh = image.Height * scale;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, rect.Left + (rect.Width - dw) / 2, rect.Top + (rect.Height - dh) / 2, dw, dh);
        }
        else
        {
            using var fill = new LinearGradientBrush(rect, Blend(ModernUi.Background, ModernUi.Accent, 0.35f),
                Blend(ModernUi.Background, ModernUi.Accent, 0.08f), 45F);
            g.FillRectangle(fill, rect);
            if (file.Kind == MediaKind.Audio) DrawKindIcon(g, new Rectangle(rect.Left + (rect.Width - S(18)) / 2, rect.Top + (rect.Height - S(18)) / 2, S(18), S(18)), file);
        }
        g.Restore(state);

        var (label, color) = FileTypeBadge.For(file);
        var size = TextRenderer.MeasureText(label, BadgeFont, Size.Empty, TextFormatFlags.NoPadding);
        var badge = new Rectangle(rect.Left + S(3), rect.Bottom - S(3) - S(14), size.Width + S(8), S(14));
        using (var path = RoundedBox(badge, S(3)))
        using (var fill = new SolidBrush(color))
            g.FillPath(fill, path);
        TextRenderer.DrawText(g, label, BadgeFont, badge, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>The muted second line under the name: what kind of item this is.</summary>
    private static string KindLine(MediaFile file)
    {
        if (file.IsBackgroundAudio) return "背景音乐 · 叠加播放，不占顺序";
        if (file.Kind == MediaKind.Document && (file.Document != null || EveryStage.Terminal.Documents.DocumentFormats.IsOffice(file.SourcePath)))
            return DocumentSettingsDialog.Summarize(file);
        return file.Kind switch
        {
            MediaKind.Image => "图片",
            MediaKind.Video => "视频",
            MediaKind.Audio => "音频",
            MediaKind.Document => "文档",
            _ => "文件",
        };
    }

    /// <summary>「时长」: how long the item holds the screen. Media length is not stored, so video and
    /// audio read 「播完」.</summary>
    private static string DurationText(MediaFile file)
    {
        if (file.IsBackgroundAudio) return "贯穿本环节";
        if (file.Kind is MediaKind.Image or MediaKind.Document)
            return file.StayDuration is { } stay ? $"停留 {stay.TotalSeconds:0} 秒" : "手动翻页";
        return "播完";
    }

    /// <summary>「结束后」: what happens when the item finishes.</summary>
    private static string AfterText(MediaFile file)
    {
        if (file.IsBackgroundAudio) return "继续播放";
        if (file.Kind is MediaKind.Image or MediaKind.Document && file.StayDuration == null) return "停留";
        return file.OnCompletion switch
        {
            CompletionAction.Loop => "循环",
            CompletionAction.HoldOnLastFrame => "停留",
            _ => "下一项",
        };
    }

    /// <summary>Small coloured file-type glyphs, following the prototype's fileTypeIcon().</summary>
    private void DrawKindIcon(Graphics g, Rectangle r, MediaFile file)
    {
        var (_, color) = FileTypeBadge.For(file);
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
