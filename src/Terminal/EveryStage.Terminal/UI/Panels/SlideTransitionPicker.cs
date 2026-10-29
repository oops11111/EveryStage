using System.Drawing.Drawing2D;
using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// 设置 → PPT 翻页过场 (design ⑦): three effect cards (渐隐渐显 / 翻页效果 / 雨点浸润, each showing the
/// effect halfway through), four direction buttons, the duration, and a large preview that plays the
/// chosen transition between two sample pages. All pictures are drawn by <see cref="SlideTransitions"/>,
/// the same code the big screen uses.
/// </summary>
internal sealed class SlideTransitionPicker : Control
{
    private static readonly string[] EffectNames = { "渐隐渐显", "翻页效果", "雨点浸润" };
    private static readonly string[] EffectDescriptions =
    {
        "上一页沿所选方向渐渐淡去，下一页同时浮现",
        "上一页像纸张一样被掀开，露出下面的下一页",
        "下一页像雨点一样一滴滴晕开，沿方向铺满画面",
    };
    private static readonly string[] DirectionNames = { "从上到下", "从下到上", "从左到右", "从右到左" };

    private readonly Bitmap _pageA;
    private readonly Bitmap _pageB;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly TrackBar _duration;
    private readonly Label _durationLabel;
    private readonly RoundedActionButton _play;
    private Rectangle[] _cards = Array.Empty<Rectangle>();
    private Rectangle[] _directions = Array.Empty<Rectangle>();
    private Rectangle _preview;
    private Rectangle _effectsLabel, _directionsLabel, _durationRow, _previewLabel, _hint;
    private float _previewT = 0.5f;
    private Point _hover = new(-1, -1);

    public SlideTransitionEffect Effect { get; private set; }
    public SlideTransitionDirection Direction { get; private set; }
    public int Milliseconds => _duration.Value * 100;

    public SlideTransitionPicker(SlideTransitionEffect effect, SlideTransitionDirection direction, int milliseconds)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Effect = effect;
        Direction = direction;
        _pageA = SamplePage("三大核心功能", "①  一键投屏　②  节目单流程", Color.FromArgb(244, 241, 234), Color.FromArgb(232, 226, 212), Color.FromArgb(28, 39, 51));
        _pageB = SamplePage("产品展示", "第 8 页", Color.FromArgb(29, 78, 107), Color.FromArgb(11, 34, 51), Color.White);
        _duration = new TrackBar { Minimum = 3, Maximum = 20, TickStyle = TickStyle.None, Value = Math.Clamp(milliseconds / 100, 3, 20), AutoSize = false, Height = 30 };
        _durationLabel = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, ForeColor = ModernUi.Text };
        _duration.ValueChanged += (_, _) => { UpdateDurationLabel(); Invalidate(); };
        _play = new RoundedActionButton { Text = "▶ 播放预览", Height = 32, Width = 110 };
        ModernUi.StyleButton(_play, primary: true);
        _play.Click += (_, _) => PlayPreview();
        Controls.Add(_duration);
        Controls.Add(_durationLabel);
        Controls.Add(_play);
        UpdateDurationLabel();
        _timer.Tick += (_, _) =>
        {
            float t = (float)(_clock.Elapsed.TotalMilliseconds / Milliseconds);
            // Play, hold the new page briefly, then return to the halfway still.
            if (t > 1.6f) { _timer.Stop(); _previewT = 0.5f; }
            else _previewT = Math.Min(1f, t);
            Invalidate(_preview);
        };
        Cursor = Cursors.Default;
        AccessibleName = "PPT 翻页过场";
    }

    private void UpdateDurationLabel() => _durationLabel.Text = $"{_duration.Value / 10.0:0.0} 秒";

    private void PlayPreview()
    {
        _clock.Restart();
        _previewT = 0f;
        _timer.Start();
    }

    private int S(int v) => LogicalToDeviceUnits(v);

    /// <summary>Lays the picker out for <paramref name="width"/> and takes the height it needs.</summary>
    public void FitToWidth(int width)
    {
        Width = width;
        bool twoColumns = width >= S(820);
        int leftWidth = twoColumns ? (int)(width * 0.54) : width;
        int y = 0;
        _effectsLabel = new Rectangle(0, y, leftWidth, S(20));
        y += S(26);
        int gap = S(12), cardWidth = (leftWidth - 2 * gap) / 3, thumbHeight = cardWidth * 9 / 16;
        int cardHeight = thumbHeight + S(66);
        _cards = Enumerable.Range(0, 3).Select(i => new Rectangle(i * (cardWidth + gap), y, cardWidth, cardHeight)).ToArray();
        y += cardHeight + S(18);
        _directionsLabel = new Rectangle(0, y, leftWidth, S(20));
        y += S(26);
        int dirGap = S(8), dirWidth = (leftWidth - 3 * dirGap) / 4;
        _directions = Enumerable.Range(0, 4).Select(i => new Rectangle(i * (dirWidth + dirGap), y, dirWidth, S(56))).ToArray();
        y += S(56) + S(6);
        _hint = new Rectangle(0, y, leftWidth, S(20));
        y += S(30);
        _durationRow = new Rectangle(0, y, leftWidth, S(30));
        _duration.Bounds = new Rectangle(S(48), y, Math.Max(S(80), leftWidth - S(48) - S(70)), S(30));
        _durationLabel.Bounds = new Rectangle(_duration.Right + S(8), y, S(62), S(30));
        y += S(30);

        int previewLeft = twoColumns ? leftWidth + S(28) : 0;
        int previewTop = twoColumns ? 0 : y + S(20);
        int previewWidth = width - previewLeft;
        _previewLabel = new Rectangle(previewLeft, previewTop, previewWidth, S(20));
        _preview = new Rectangle(previewLeft, previewTop + S(26), previewWidth, previewWidth * 9 / 16);
        _play.Location = new Point(previewLeft, _preview.Bottom + S(10));
        int previewBottom = _play.Bottom;
        Height = Math.Max(y, previewBottom) + S(4);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hover = e.Location;
        Cursor = _cards.Concat(_directions).Any(r => r.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = new Point(-1, -1);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        for (int i = 0; i < _cards.Length; i++)
            if (_cards[i].Contains(e.Location)) { Effect = (SlideTransitionEffect)i; PlayPreview(); Invalidate(); return; }
        for (int i = 0; i < _directions.Length; i++)
            if (_directions[i].Contains(e.Location)) { Direction = (SlideTransitionDirection)i; PlayPreview(); Invalidate(); return; }
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // ←/→ change the effect, ↑/↓ the direction.
        if (e.KeyCode is Keys.Left or Keys.Right) Effect = (SlideTransitionEffect)(((int)Effect + (e.KeyCode == Keys.Right ? 1 : 2)) % 3);
        else if (e.KeyCode is Keys.Up or Keys.Down) Direction = (SlideTransitionDirection)(((int)Direction + (e.KeyCode == Keys.Down ? 1 : 3)) % 4);
        else return;
        e.Handled = true;
        PlayPreview();
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var labelFont = new Font(ModernUi.UiFont, 9F);
        using var nameFont = new Font(ModernUi.UiFontSemibold, 10F);
        using var smallFont = new Font(ModernUi.UiFont, 8.5F);
        TextRenderer.DrawText(g, "动画", labelFont, _effectsLabel, ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "方向", labelFont, _directionsLabel, ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "时长", labelFont, new Rectangle(0, _durationRow.Y, S(44), _durationRow.Height), ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        for (int i = 0; i < _cards.Length; i++)
        {
            var card = _cards[i];
            bool selected = (int)Effect == i, hot = card.Contains(_hover);
            using (var path = ActivityRowView.RoundedBox(card, S(10)))
            {
                using var fill = new SolidBrush(hot ? ModernUi.Mix(ModernUi.Background, ModernUi.Text, 0.04f) : ModernUi.Background);
                g.FillPath(fill, path);
                using var pen = new Pen(selected ? ModernUi.Accent : ModernUi.Border, selected ? 2f : 1f);
                g.DrawPath(pen, path);
            }
            var thumb = new Rectangle(card.X + S(10), card.Y + S(10), card.Width - S(20), (card.Width - S(20)) * 9 / 16);
            DrawStill(g, thumb, (SlideTransitionEffect)i, 0.5f);
            var name = new Rectangle(thumb.X, thumb.Bottom + S(6), thumb.Width, S(22));
            TextRenderer.DrawText(g, EffectNames[i] + (selected ? "  ✓" : ""), nameFont, name, selected ? ModernUi.AccentText : ModernUi.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, EffectDescriptions[i], smallFont, new Rectangle(thumb.X, name.Bottom, thumb.Width, card.Bottom - name.Bottom - S(6)),
                ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }

        for (int i = 0; i < _directions.Length; i++)
        {
            var box = _directions[i];
            bool selected = (int)Direction == i, hot = box.Contains(_hover);
            using (var path = ActivityRowView.RoundedBox(box, S(8)))
            {
                using var fill = new SolidBrush(selected ? ModernUi.NavActive : hot ? ModernUi.Mix(ModernUi.Background, ModernUi.Text, 0.04f) : ModernUi.Background);
                g.FillPath(fill, path);
                using var pen = new Pen(selected ? ModernUi.Accent : ModernUi.Border, selected ? 2f : 1f);
                g.DrawPath(pen, path);
            }
            var color = selected ? ModernUi.AccentText : ModernUi.Text;
            DrawArrow(g, new Point(box.X + box.Width / 2, box.Y + S(18)), (SlideTransitionDirection)i, color);
            TextRenderer.DrawText(g, DirectionNames[i], smallFont, new Rectangle(box.X, box.Y + S(30), box.Width, S(20)), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        string d = DirectionNames[(int)Direction];
        string hint = Effect switch
        {
            SlideTransitionEffect.PageTurn => $"翻页效果：从{d[1]}边掀起，向{d[3]}边翻过去",
            SlideTransitionEffect.RainSoak => $"雨点浸润：雨点从{d[1]}边落下，向{d[3]}边铺满",
            _ => $"渐隐渐显：画面从{d[1]}边开始淡去，向{d[3]}边推进",
        };
        TextRenderer.DrawText(g, hint, smallFont, _hint, ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(g, $"效果预览 · {EffectNames[(int)Effect]} · {d} · {Milliseconds / 1000.0:0.0} 秒", labelFont, _previewLabel, ModernUi.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        DrawStill(g, _preview, Effect, SlideTransitions.Ease(_previewT));

        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(_cards[(int)Effect], 3, 3), ModernUi.Text, ModernUi.Background);
    }

    private void DrawStill(Graphics g, Rectangle r, SlideTransitionEffect effect, float t)
    {
        if (r.Width < 4 || r.Height < 4) return;
        using var from = new Bitmap(_pageA, r.Size);
        using var to = new Bitmap(_pageB, r.Size);
        using var frame = new Bitmap(r.Width, r.Height);
        using (var fg = Graphics.FromImage(frame))
            SlideTransitions.Draw(fg, new Rectangle(Point.Empty, r.Size), from, to, t, effect, Direction);
        var state = g.Save();
        using (var clip = ActivityRowView.RoundedBox(r, S(6))) g.SetClip(clip);
        g.DrawImage(frame, r);
        g.Restore(state);
    }

    private void DrawArrow(Graphics g, Point center, SlideTransitionDirection direction, Color color)
    {
        float angle = direction switch
        {
            SlideTransitionDirection.BottomToTop => 180f,
            SlideTransitionDirection.LeftToRight => -90f,
            SlideTransitionDirection.RightToLeft => 90f,
            _ => 0f,
        };
        var state = g.Save();
        g.TranslateTransform(center.X, center.Y);
        g.RotateTransform(angle);
        using var pen = new Pen(color, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLine(pen, 0, -S(8), 0, S(8));
        g.DrawLines(pen, new[] { new PointF(-S(5), S(3)), new PointF(0, S(8)), new PointF(S(5), S(3)) });
        g.Restore(state);
    }

    private static Bitmap SamplePage(string title, string subtitle, Color top, Color bottom, Color ink)
    {
        var page = new Bitmap(640, 360);
        using var g = Graphics.FromImage(page);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var fill = new LinearGradientBrush(new Rectangle(0, 0, 640, 360), top, bottom, 60f)) g.FillRectangle(fill, 0, 0, 640, 360);
        using var titleFont = new Font(ModernUi.UiFontSemibold, 34F, GraphicsUnit.Pixel);
        using var subFont = new Font(ModernUi.UiFont, 20F, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(ink);
        g.DrawString(title, titleFont, brush, 48, 128);
        using var subBrush = new SolidBrush(Color.FromArgb(200, ink));
        g.DrawString(subtitle, subFont, subBrush, 50, 186);
        return page;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _pageA.Dispose();
            _pageB.Dispose();
        }
        base.Dispose(disposing);
    }
}
