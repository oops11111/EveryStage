using System.Drawing.Drawing2D;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// 侧栏的「音频独立播放区」卡片（效果图左侧、标语上方）：紫色音符标题、三行说明、波形 + 时间、音量、
/// ☑ 循环播放 + ↻。它不是另一个播放器，而是文件页 <see cref="AudioPlayerBar"/> 的第二个视图——读它的
/// 状态、调它的命令，所以两处永远是同一首、同一个播放态；侧栏常驻，在活动/设备/设置页也能控制背景音乐。
///
/// 效果图之外补了两处必须的交互：说明下方一行当前曲目「背景音乐.mp3 ▾」（点开与播放条同一个选曲菜单），
/// 以及波形左侧的播放/暂停键。波形同时是进度条：已播部分亮紫，点击跳转。
/// </summary>
internal sealed class AudioSidebarCard : Control
{
    private static readonly Color Purple = Color.FromArgb(155, 108, 255);
    private static readonly int[] WaveHeights = { 7, 12, 18, 10, 20, 14, 8, 16, 22, 11, 6, 15, 19, 9, 13 };
    private static readonly string[] Description = { "独立的音频播放工作区", "支持时间轴、音量、循环", "可作为背景音频使用" };

    private readonly AudioPlayerBar _bar;
    private readonly ToolTip _toolTip = new();
    private bool _compact;

    private Rectangle _iconTile, _title, _nameRow, _playButton, _wave, _time, _volumeIcon, _volumeTrack, _loop, _loopBox, _restart;

    public AudioSidebarCard(AudioPlayerBar bar)
    {
        _bar = bar;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        AccessibleName = "音频独立播放区";
        _bar.StateChanged += OnBarStateChanged;
        LayoutParts();
    }

    /// <summary>Drops the three description lines when the sidebar is too short for the full card.</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            LayoutParts();
            Invalidate();
        }
    }

    public int FullHeight => MeasureHeight(compact: false);
    public int CompactHeight => MeasureHeight(compact: true);

    private int MeasureHeight(bool compact) => 12 + 26 + 8 + (compact ? 0 : 3 * 17 + 6) + 22 + 6 + 22 + 10 + 14 + 10 + 18 + 12;

    private void OnBarStateChanged()
    {
        if (IsHandleCreated && !IsDisposed) Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutParts();
    }

    private void LayoutParts()
    {
        int w = Width, pad = 13, y = 12;
        _iconTile = new Rectangle(pad, y, 26, 26);
        _title = new Rectangle(_iconTile.Right + 8, y, Math.Max(10, w - _iconTile.Right - 8 - pad), 26);
        y += 26 + 8;
        if (!_compact) y += 3 * 17 + 6;
        _nameRow = new Rectangle(pad, y, Math.Max(10, w - pad * 2), 22);
        y += 22 + 6;
        _playButton = new Rectangle(pad - 2, y, 22, 22);
        _wave = new Rectangle(_playButton.Right + 6, y, WaveHeights.Length * 5 - 2, 22);
        _time = Rectangle.FromLTRB(_wave.Right + 8, y, w - pad, y + 22);
        y += 22 + 10;
        _volumeIcon = new Rectangle(pad, y, 16, 14);
        _volumeTrack = Rectangle.FromLTRB(_volumeIcon.Right + 8, y + 6, w - pad - 4, y + 9);
        y += 14 + 10;
        _loopBox = new Rectangle(pad + 1, y + 1, 16, 16);
        using (var font = new Font("Segoe UI", 8.5F))
            _loop = new Rectangle(pad, y, 16 + 8 + TextRenderer.MeasureText("循环播放", font).Width, 18);
        _restart = new Rectangle(w - pad - 18, y, 18, 18);
    }

    protected override void OnPaintBackground(PaintEventArgs e) =>
        ButtonRenderer.DrawParentBackground(e.Graphics, ClientRectangle, this);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = RoundedBox(box, 12))
        using (var fill = new SolidBrush(Color.FromArgb(20, 120, 90, 230)))
        using (var border = new Pen(Color.FromArgb(48, 140, 110, 255)))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        // Header: purple note tile + 音频独立播放区.
        using (var tilePath = RoundedBox(_iconTile, 7))
        using (var tileFill = new SolidBrush(Color.FromArgb(41, Purple)))
            g.FillPath(tileFill, tilePath);
        using (var noteFont = new Font("Segoe UI Symbol", 11F))
            TextRenderer.DrawText(g, "♫", noteFont, _iconTile, Purple,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        using (var titleFont = new Font("Segoe UI Semibold", 10F))
            TextRenderer.DrawText(g, "音频独立播放区", titleFont, _title, ModernUi.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        using var smallFont = new Font("Segoe UI", 8.5F);
        if (!_compact)
        {
            for (int i = 0; i < Description.Length; i++)
                TextRenderer.DrawText(g, Description[i], smallFont, new Rectangle(13, _iconTile.Bottom + 8 + i * 17, Width - 26, 17),
                    ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        var file = _bar.BoundFile;
        // Current track (selector) — or an import hint when the library has no audio.
        if (file != null)
        {
            using var nameFont = new Font("Segoe UI", 9F);
            var nameRect = Rectangle.FromLTRB(_nameRow.Left, _nameRow.Top, _nameRow.Right - 14, _nameRow.Bottom);
            string name = Path.GetFileName(file.SourcePath);
            TextRenderer.DrawText(g, name, nameFont, nameRect, ModernUi.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            int nameWidth = Math.Min(nameRect.Width, TextRenderer.MeasureText(g, name, nameFont, nameRect.Size, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width);
            DrawChevron(g, new PointF(nameRect.Left + nameWidth + 11, _nameRow.Top + _nameRow.Height / 2f), ModernUi.Muted);
        }
        else
        {
            TextRenderer.DrawText(g, "暂无音频 · 点击导入", smallFont, _nameRow, ModernUi.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        bool playing = _bar.IsPlayingNow;
        float progress = _bar.ProgressFraction();

        // Play / pause.
        using (var glyph = new SolidBrush(file != null ? ModernUi.Text : ModernUi.Muted))
        {
            float cx = _playButton.Left + _playButton.Width / 2f, cy = _playButton.Top + _playButton.Height / 2f;
            if (playing)
            {
                g.FillRectangle(glyph, cx - 5, cy - 6, 3.5f, 12);
                g.FillRectangle(glyph, cx + 1.5f, cy - 6, 3.5f, 12);
            }
            else g.FillPolygon(glyph, new[] { new PointF(cx - 4, cy - 6.5f), new PointF(cx - 4, cy + 6.5f), new PointF(cx + 6, cy) });
        }

        // Waveform doubling as the timeline: the played part is brighter.
        for (int i = 0; i < WaveHeights.Length; i++)
        {
            bool played = progress > 0 && (i + 0.5f) / WaveHeights.Length <= progress;
            int alpha = progress > 0 ? (played ? 255 : 110) : 217;
            using var bar = new SolidBrush(Color.FromArgb(alpha, played ? Color.FromArgb(183, 148, 255) : Purple));
            int h = WaveHeights[i];
            using var barPath = RoundedBox(new Rectangle(_wave.Left + i * 5, _wave.Bottom - h, 3, h), 1);
            g.FillPath(bar, barPath);
        }
        TextRenderer.DrawText(g, file != null ? _bar.PositionText() : "--:-- / --:--", smallFont, _time, ModernUi.Muted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // Volume.
        using (var speaker = new SolidBrush(ModernUi.Muted))
        using (var arc = new Pen(ModernUi.Muted, 1.5F))
        {
            var r = _volumeIcon;
            g.FillPolygon(speaker, new[]
            {
                new PointF(r.Left + 1, r.Top + 5), new PointF(r.Left + 4, r.Top + 5), new PointF(r.Left + 8, r.Top + 1.5f),
                new PointF(r.Left + 8, r.Bottom - 1.5f), new PointF(r.Left + 4, r.Bottom - 5), new PointF(r.Left + 1, r.Bottom - 5),
            });
            g.DrawArc(arc, r.Left + 7, r.Top + 3, 7, r.Height - 6, -55, 110);
        }
        float volume = _bar.VolumeFraction();
        using (var track = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
            g.FillRectangle(track, _volumeTrack);
        using (var fillBrush = new SolidBrush(Purple))
            g.FillRectangle(fillBrush, _volumeTrack.Left, _volumeTrack.Top, _volumeTrack.Width * volume, _volumeTrack.Height);
        using (var knob = new SolidBrush(Color.White))
            g.FillEllipse(knob, _volumeTrack.Left + _volumeTrack.Width * volume - 5, _volumeTrack.Top + _volumeTrack.Height / 2f - 5, 10, 10);

        // ☑ 循环播放 + ↻ (play from the start).
        bool looping = _bar.IsLooping;
        using (var boxPath = RoundedBox(_loopBox, 3))
        using (var boxPen = new Pen(looping ? ModernUi.Text : ModernUi.Muted, 1.4F))
            g.DrawPath(boxPen, boxPath);
        if (looping)
        {
            using var check = new Pen(ModernUi.Text, 1.8F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            var b = _loopBox;
            g.DrawLines(check, new[]
            {
                new PointF(b.Left + b.Width * 0.24f, b.Top + b.Height * 0.52f),
                new PointF(b.Left + b.Width * 0.44f, b.Top + b.Height * 0.72f),
                new PointF(b.Left + b.Width * 0.78f, b.Top + b.Height * 0.30f),
            });
        }
        TextRenderer.DrawText(g, "循环播放", smallFont, Rectangle.FromLTRB(_loopBox.Right + 7, _loop.Top, _loop.Right + 4, _loop.Bottom),
            ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        using (var restartPen = new Pen(ModernUi.Muted, 1.6F) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var arrow = new SolidBrush(ModernUi.Muted))
        {
            var r = Rectangle.Inflate(_restart, -2, -2);
            g.DrawArc(restartPen, r, -40, 300);
            float ax = r.Right - 1, ay = r.Top + r.Height * 0.2f;
            g.FillPolygon(arrow, new[] { new PointF(ax + 2.5f, ay - 4), new PointF(ax + 2.5f, ay + 2), new PointF(ax - 3.5f, ay + 1) });
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var file = _bar.BoundFile;
        if (_nameRow.Contains(e.Location))
        {
            if (file != null) _bar.ShowFilePicker(this, new Point(_nameRow.Left, _nameRow.Bottom + 2));
            else _bar.RequestImport();
            return;
        }
        if (file == null) return;
        if (_playButton.Contains(e.Location)) _bar.TogglePlayPause();
        else if (_wave.Contains(e.Location))
        {
            if (_bar.IsPlayingNow) _bar.SeekToFraction((double)(e.X - _wave.Left) / Math.Max(1, _wave.Width));
            else _bar.TogglePlayPause();
        }
        else if (Rectangle.Inflate(_volumeTrack, 4, 8).Contains(e.Location))
            _bar.SetVolume((float)(e.X - _volumeTrack.Left) / Math.Max(1, _volumeTrack.Width));
        else if (_loop.Contains(e.Location)) _bar.ToggleLoop();
        else if (_restart.Contains(e.Location)) _bar.Restart();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hasFile = _bar.BoundFile != null;
        bool interactive = _nameRow.Contains(e.Location) || (hasFile && (_playButton.Contains(e.Location)
            || _wave.Contains(e.Location) || Rectangle.Inflate(_volumeTrack, 4, 8).Contains(e.Location)
            || _loop.Contains(e.Location) || _restart.Contains(e.Location)));
        Cursor = interactive ? Cursors.Hand : Cursors.Default;
        string tip = _nameRow.Contains(e.Location) ? (hasFile ? "切换音频" : "导入音频")
            : hasFile && _restart.Contains(e.Location) ? "从头播放"
            : hasFile && _playButton.Contains(e.Location) ? "播放 / 暂停"
            : "";
        if (_toolTip.GetToolTip(this) != tip) _toolTip.SetToolTip(this, tip);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bar.StateChanged -= OnBarStateChanged;
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private static void DrawChevron(Graphics g, PointF center, Color color)
    {
        using var pen = new Pen(color, 1.5F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(center.X - 4, center.Y - 2), new PointF(center.X, center.Y + 2), new PointF(center.X + 4, center.Y - 2) });
    }

    private static GraphicsPath RoundedBox(Rectangle r, int radius)
    {
        int d = Math.Min(Math.Max(2, radius * 2), Math.Max(1, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
