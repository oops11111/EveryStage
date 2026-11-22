using System.Drawing.Drawing2D;

namespace EveryStage.Terminal.UI;

/// <summary>
/// A horizontal strip of every slide's thumbnail for the 演讲者视图: slide it with the mouse wheel or by
/// dragging, click one to put that slide on the big screen. The slide on air is framed and kept in view.
/// Thumbnails are loaded lazily, visible ones first, one at a time (each is a call into the presentation
/// program, which must not be flooded mid-show).
/// </summary>
internal sealed class SlideThumbnailStrip : Control
{
    private readonly Color _back, _card, _line, _ink, _muted, _accent;
    private readonly Dictionary<int, Bitmap> _images = new();
    private readonly HashSet<int> _requested = new();
    private Func<int, Task<Bitmap?>>? _loader;
    private int _count;
    private int _current;
    private float _offset;
    private int _version;
    private bool _loading;
    private bool _loadRequested;
    private int _visibleFirst = -1, _visibleLast = -1;
    private readonly PresenterOffsetMotion _motion;

    internal float PresentedOffset => _offset;
    internal double RequestedOffset => _motion.Target;
    internal bool MotionRunning => _motion.IsRunning;
    internal bool MotionTimerEnabled => _motion.TimerEnabled;
    private int _hover = -1;
    private int _keyboardSlide;
    private Point _downAt;
    private float _downOffset;
    private bool _pressed, _dragging;

    /// <summary>A thumbnail was clicked (1-based slide number).</summary>
    public event Action<int>? SlideChosen;

    public SlideThumbnailStrip(Color back, Color card, Color line, Color ink, Color muted, Color accent, IAnimationPreference? preference = null)
    {
        (_back, _card, _line, _ink, _muted, _accent) = (back, card, line, ink, muted, accent);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = new Font(ModernUi.UiFont, 9F);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        AccessibleName = "页面缩略图";
        AccessibleDescription = "方向键选择页面，回车跳到该页";
        _motion = new PresenterOffsetMotion(this, value =>
        {
            if (IsDisposed) return;
            _offset = Math.Clamp((float)value, 0, MaxOffset);
            Invalidate();
            LoadIfVisibleRangeChanged();
        }, RequestVisibleLoad, preference);
    }

    private int S(int value) => LogicalToDeviceUnits(value);
    private int ThumbWidth => S(144);
    private int ThumbHeight => S(81);
    private int Gap => S(10);
    private int Inset => S(4);
    private int Item => ThumbWidth + Gap;
    private float MaxOffset => Math.Max(0f, Inset * 2f + _count * (float)Item - Gap - Width);

    /// <summary>Starts over for a show of <paramref name="count"/> slides (0 = none).</summary>
    public void Reset(int count, Func<int, Task<Bitmap?>>? loader)
    {
        _version++;
        _motion.Freeze(_offset);
        _pressed = _dragging = false;
        Capture = false;
        _visibleFirst = _visibleLast = -1;
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
        _requested.Clear();
        _count = Math.Max(0, count);
        _loader = loader;
        _current = 0;
        _keyboardSlide = 0;
        _offset = 0;
        _hover = -1;
        Invalidate();
        RequestVisibleLoad();
    }

    /// <summary>Supplies a thumbnail directly (CI screenshot).</summary>
    public void SetImage(int slide, Bitmap image)
    {
        if (_images.Remove(slide, out var old)) old.Dispose();
        _images[slide] = image;
        _requested.Add(slide);
        Invalidate();
    }

    /// <summary>The slide on air (1-based); scrolls it into view.</summary>
    public int Current
    {
        get => _current;
        set
        {
            if (_current == value) return;
            _current = value;
            ScrollIntoView(value);
            Invalidate();
            RequestVisibleLoad();
        }
    }

    private int HitTest(Point point)
    {
        if (_count == 0 || point.Y < Inset || point.Y > Inset + ThumbHeight + S(20)) return -1;
        float x = point.X + _offset - Inset;
        if (x < 0) return -1;
        int index = (int)(x / Item);
        if (index >= _count || x - index * Item > ThumbWidth) return -1;
        return index + 1;
    }

    private void ScrollBy(float delta)
    {
        double basis = _motion.IsRunning ? _motion.Target : _offset;
        double next = Math.Clamp(basis + delta, 0, MaxOffset);
        MoveOffset(next);
    }

    private void MoveOffset(double target)
    {
        if (_pressed || IsDisposed) return; // Direct manipulation has priority over automatic follow.
        target = Math.Clamp(target, 0, MaxOffset);
        if (Math.Abs(target - (_motion.IsRunning ? _motion.Target : _offset)) < 0.5) return;
        _motion.MoveTo(target, _offset);
        RequestVisibleLoad();
    }

    private void SetDirectOffset(float value)
    {
        _offset = Math.Clamp(value, 0, MaxOffset);
        Invalidate();
        LoadIfVisibleRangeChanged();
    }

    private void LoadIfVisibleRangeChanged()
    {
        int first = Math.Max(1, (int)((_offset - Inset) / Item) + 1);
        int last = Math.Min(_count, (int)((_offset + Width) / Item) + 2);
        if (first == _visibleFirst && last == _visibleLast) return;
        _visibleFirst = first;
        _visibleLast = last;
        RequestVisibleLoad();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _keyboardSlide = _current >= 1 ? _current : 1;
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Space or Keys.Enter
            || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_count == 0) { base.OnKeyDown(e); return; }
        int? next = e.KeyCode switch
        {
            Keys.Left or Keys.Up => Math.Max(1, _keyboardSlide - 1),
            Keys.Right or Keys.Down => Math.Min(_count, _keyboardSlide + 1),
            Keys.Home => 1,
            Keys.End => _count,
            _ => null,
        };
        if (next is { } slide)
        {
            _keyboardSlide = slide;
            ScrollIntoView(slide);
            Invalidate();
            RequestVisibleLoad();
            e.Handled = true;
            return;
        }
        if (e.KeyCode is Keys.Space or Keys.Enter && _keyboardSlide >= 1 && _keyboardSlide <= _count)
        {
            SlideChosen?.Invoke(_keyboardSlide);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollBy(-e.Delta / 120f * Item * 2);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _motion.Freeze(_offset);
        _pressed = true;
        _dragging = false;
        _downAt = e.Location;
        _downOffset = _offset;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressed)
        {
            int dx = e.X - _downAt.X;
            if (!_dragging && Math.Abs(dx) > S(6)) _dragging = true;
            if (_dragging)
            {
                Cursor = Cursors.SizeWE;
                SetDirectOffset(_downOffset - dx);
            }
            return;
        }
        int hover = HitTest(e.Location);
        if (hover != _hover) { _hover = hover; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_pressed) return;
        bool wasDragging = _dragging;
        _pressed = false;
        Capture = false;
        Cursor = Cursors.Hand;
        if (!wasDragging && HitTest(e.Location) is var slide and > 0) SlideChosen?.Invoke(slide);
        _dragging = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture) return;
        _pressed = _dragging = false;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; Invalidate(); }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_motion == null) return; // Control base constructor can resize before fields initialize.
        _pressed = _dragging = false;
        Capture = false;
        _motion.Freeze(Math.Clamp(_offset, 0, MaxOffset));
        SetDirectOffset(_offset);
        ScrollIntoView(_current); // a narrower window must not leave the page on air half cut off
        RequestVisibleLoad();
    }

    private void ScrollIntoView(int slide)
    {
        if (slide < 1) return;
        double next = _motion.IsRunning ? _motion.Target : _offset;
        float left = Inset + (slide - 1) * (float)Item, right = left + ThumbWidth;
        if (left < next + Inset) next = left - Inset - Item; // keep the previous one visible too
        else if (right > next + Width - Inset) next = right - Width + Inset + Item;
        MoveOffset(next);
    }

    private void RequestVisibleLoad()
    {
        if (IsDisposed || _loader == null || _count == 0) return;
        _loadRequested = true;
        if (!_loading) _ = LoadVisibleAsync();
    }

    private async Task LoadVisibleAsync()
    {
        if (_loading || IsDisposed) return;
        _loading = true;
        try
        {
            while (!IsDisposed && _loader != null && _count > 0)
            {
                _loadRequested = false;
                int first = Math.Max(1, (int)((_offset - Inset) / Item) + 1);
                int last = Math.Min(_count, (int)((_offset + Width) / Item) + 2);
                int wanted = -1;
                // The slide on air and its neighbours first, then whatever is in view.
                foreach (int candidate in new[] { _current, _current + 1 }.Concat(Enumerable.Range(first, Math.Max(0, last - first + 1))))
                    if (candidate >= 1 && candidate <= _count && !_requested.Contains(candidate)) { wanted = candidate; break; }
                if (wanted < 0) return;
                _requested.Add(wanted);
                int version = _version;
                Bitmap? image = null;
                try { image = await _loader(wanted); } catch (Exception) { }
                if (IsDisposed) { image?.Dispose(); return; }
                if (version != _version) { image?.Dispose(); continue; }
                if (image != null)
                {
                    if (_images.Remove(wanted, out var previous)) previous.Dispose();
                    _images[wanted] = image;
                    Invalidate();
                }
            }
        }
        finally
        {
            _loading = false;
            if (_loadRequested && !IsDisposed && _loader != null && _count > 0) RequestVisibleLoad();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        if (_count == 0)
        {
            TextRenderer.DrawText(g, "放映开始后，这里列出所有页面，点一下就能跳到那一页", Font, ClientRectangle, _muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }
        int first = Math.Max(0, (int)((_offset - Inset) / Item));
        int last = Math.Min(_count - 1, (int)((_offset + Width) / Item) + 1);
        for (int i = first; i <= last; i++)
        {
            int slide = i + 1;
            var rect = new Rectangle((int)(Inset + i * Item - _offset), Inset, ThumbWidth, ThumbHeight);
            using (var card = new SolidBrush(_card)) g.FillRectangle(card, rect);
            if (_images.TryGetValue(slide, out var image))
            {
                float scale = Math.Min((float)rect.Width / image.Width, (float)rect.Height / image.Height);
                var size = new SizeF(image.Width * scale, image.Height * scale);
                g.DrawImage(image, new RectangleF(rect.X + (rect.Width - size.Width) / 2, rect.Y + (rect.Height - size.Height) / 2, size.Width, size.Height));
            }
            else
            {
                TextRenderer.DrawText(g, slide.ToString(), Font, rect, _muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            bool current = slide == _current, hover = slide == _hover;
            using (var pen = new Pen(current ? _accent : hover ? _ink : _line, current ? S(3) : 1))
            {
                var border = current ? Rectangle.Inflate(rect, -S(1), -S(1)) : new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                g.DrawRectangle(pen, border);
            }
            var label = new Rectangle(rect.X, rect.Bottom + S(2), rect.Width, S(18));
            TextRenderer.DrawText(g, current ? $"● {slide}  正在放映" : slide.ToString(), Font, label, current ? _accent : _muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
        }
        // The keyboard-selected thumb (distinct from the one on air): a dotted accent frame.
        if (Focused && ShowFocusCues && _keyboardSlide >= 1 && _keyboardSlide <= _count)
        {
            var focusRect = new Rectangle((int)(Inset + (_keyboardSlide - 1) * Item - _offset), Inset, ThumbWidth, ThumbHeight);
            using var focus = new Pen(_accent, S(2)) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, focusRect);
        }
        // Edge fades: there is more to slide to on that side.
        int fade = S(28);
        if (_offset > 0.5f)
            using (var left = new LinearGradientBrush(new Rectangle(0, 0, fade, Height), _back, Color.FromArgb(0, _back), 0F))
                g.FillRectangle(left, 0, 0, fade, Height);
        if (_offset < MaxOffset - 0.5f)
            using (var right = new LinearGradientBrush(new Rectangle(Width - fade, 0, fade, Height), Color.FromArgb(0, _back), _back, 0F))
                g.FillRectangle(right, Width - fade, 0, fade, Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _version++;
            _loadRequested = false;
            _motion?.Dispose();
            foreach (var image in _images.Values) image.Dispose();
            _images.Clear();
        }
        base.Dispose(disposing);
    }
}
