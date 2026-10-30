using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// The 文件库 card grid, laid out and drawn here rather than by a WinForms ListView (whose tile view did not
/// re-flow when the card size changed — cards overlapped or left wide gaps — and cannot reorder by drag).
///
/// Columns: as many cards of at least <see cref="MinCardWidth"/> as fit, stretched to fill the width with a
/// fixed gap between them; the card height comes from <see cref="TileHeightFor"/>. Vertical scrolling
/// (wheel, scroll bar, keyboard). Selection: click, Ctrl+click, Shift+click, Ctrl+A, arrow keys. Dragging a
/// card shows an insertion bar and raises <see cref="ReorderRequested"/>; files dragged in from Explorer
/// still reach the owner's DragEnter/DragDrop. Cards are painted by the owner (<see cref="DrawCard"/>).
/// </summary>
internal sealed class MediaGrid : Control
{
    public sealed class Item
    {
        public required MediaFile File { get; init; }
        public string Meta { get; init; } = "";
        public string ToolTip { get; init; } = "";
        public bool Selected { get; set; }
    }

    private readonly List<Item> _items = new();
    private readonly VScrollBar _scrollBar = new() { Visible = false };
    private readonly ToolTip _toolTip = new() { InitialDelay = 600 };
    private readonly System.Windows.Forms.Timer _autoScroll = new() { Interval = 30 };
    private int _columns = 1;
    private int _cardWidth;
    private int _tileHeight;
    private int _scroll;
    private int _anchor = -1;          // Shift+click range start
    private int _focus = -1;           // keyboard position
    private Item? _hoverItem;
    private Point _mouseDown;
    private Item? _pressed;
    private bool _dragging;
    private int _insertAt = -1;        // drag insertion index (0..count)
    private Point _dragPoint;

    /// <summary>Gap between cards and around the grid, in logical pixels.</summary>
    public int Gap { get; set; } = 12;

    /// <summary>Smallest card width (logical pixels) — the 小 / 中 / 大 setting.</summary>
    public int MinCardWidth
    {
        get => _minCardWidth;
        set { if (_minCardWidth != value) { _minCardWidth = value; Relayout(); } }
    }
    private int _minCardWidth = 180;

    /// <summary>Card height for a card width (both device pixels).</summary>
    public Func<int, int> TileHeightFor { get; set; } = width => width * 3 / 4;

    /// <summary>Paints one card into its rectangle.</summary>
    public event Action<Graphics, Item, Rectangle>? DrawCard;
    public event Action? SelectionChanged;
    /// <summary>A plain left click on a card (not a Ctrl/Shift selection click, not the end of a drag).</summary>
    public event Action<Item, MouseEventArgs>? ItemClicked;
    public event Action<Item, MouseEventArgs>? ItemDoubleClicked;
    /// <summary>Right click: the card under the pointer (null on empty space) and the point.</summary>
    public event Action<Item?, Point>? ContextMenuRequested;
    /// <summary>Drag to reorder: the cards being moved, and the card they go before (null: the end).</summary>
    public event Action<IReadOnlyList<Item>, Item?>? ReorderRequested;
    /// <summary>Whether dragging to reorder is allowed right now (e.g. not while a filter hides cards).</summary>
    public Func<bool>? CanReorder { get; set; }

    public MediaGrid()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = ModernUi.Background;
        _scrollBar.Dock = DockStyle.Right;
        _scrollBar.Scroll += (_, _) => { _scroll = _scrollBar.Value; Invalidate(); };
        Controls.Add(_scrollBar);
        _autoScroll.Tick += (_, _) => AutoScrollStep();
        AccessibleName = "文件库";
        AccessibleRole = AccessibleRole.List;
    }

    public IReadOnlyList<Item> Items => _items;
    public IReadOnlyList<Item> SelectedItems => _items.Where(i => i.Selected).ToList();

    public void SetItems(IEnumerable<Item> items)
    {
        // Keep the selection of files that are still listed.
        var selected = _items.Where(i => i.Selected).Select(i => i.File.Id).ToHashSet();
        _items.Clear();
        foreach (var item in items)
        {
            item.Selected = selected.Contains(item.File.Id);
            _items.Add(item);
        }
        _hoverItem = null;
        _anchor = _focus = -1;
        Relayout();
    }

    public void SelectOnly(Item? item)
    {
        foreach (var i in _items) i.Selected = ReferenceEquals(i, item);
        _anchor = _focus = item == null ? -1 : _items.IndexOf(item);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    private int S(int v) => LogicalToDeviceUnits(v);
    private int ViewWidth => Math.Max(1, ClientSize.Width - (_scrollBar.Visible ? _scrollBar.Width : 0));
    private int ContentHeight => _items.Count == 0 ? 0 : S(Gap) + (_items.Count + _columns - 1) / _columns * (_tileHeight + S(Gap));

    private void Relayout()
    {
        if (IsDisposed) return;
        // Decide the scroll bar first (with it, the width shrinks): lay out without it, then with it if needed.
        _scrollBar.Visible = false;
        Measure();
        if (ContentHeight > ClientSize.Height)
        {
            _scrollBar.Visible = true;
            Measure();
        }
        int max = Math.Max(0, ContentHeight - ClientSize.Height);
        _scroll = Math.Clamp(_scroll, 0, max);
        _scrollBar.Minimum = 0;
        _scrollBar.LargeChange = Math.Max(1, ClientSize.Height);
        _scrollBar.SmallChange = Math.Max(1, _tileHeight / 3);
        _scrollBar.Maximum = Math.Max(0, ContentHeight - 1);
        _scrollBar.Value = Math.Min(_scroll, Math.Max(0, _scrollBar.Maximum - _scrollBar.LargeChange + 1));
        Invalidate();

        void Measure()
        {
            int gap = S(Gap), width = ViewWidth - gap;
            _columns = Math.Max(1, width / (S(MinCardWidth) + gap));
            _cardWidth = Math.Max(S(60), width / _columns - gap);
            _tileHeight = TileHeightFor(_cardWidth);
        }
    }

    /// <summary>The card rectangle of item <paramref name="index"/> in client coordinates.</summary>
    public Rectangle CardBounds(int index)
    {
        int gap = S(Gap), row = index / _columns, column = index % _columns;
        int x = gap + column * (_cardWidth + gap);
        return new Rectangle(x, gap + row * (_tileHeight + gap) - _scroll, _cardWidth, _tileHeight);
    }

    public Item? ItemAt(Point p)
    {
        for (int i = FirstVisible(); i < _items.Count; i++)
        {
            var bounds = CardBounds(i);
            if (bounds.Top > ClientSize.Height) break;
            if (bounds.Contains(p)) return _items[i];
        }
        return null;
    }

    private int FirstVisible() => Math.Max(0, (_scroll - S(Gap)) / Math.Max(1, _tileHeight + S(Gap)) * _columns);

    public void EnsureVisible(Item item)
    {
        int index = _items.IndexOf(item);
        if (index < 0) return;
        var bounds = CardBounds(index);
        if (bounds.Top < 0) ScrollTo(_scroll + bounds.Top - S(Gap));
        else if (bounds.Bottom > ClientSize.Height) ScrollTo(_scroll + bounds.Bottom - ClientSize.Height + S(Gap));
    }

    private void ScrollTo(int value)
    {
        int max = Math.Max(0, ContentHeight - ClientSize.Height);
        value = Math.Clamp(value, 0, max);
        if (value == _scroll) return;
        _scroll = value;
        if (_scrollBar.Visible) _scrollBar.Value = Math.Min(_scroll, Math.Max(0, _scrollBar.Maximum - _scrollBar.LargeChange + 1));
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Relayout();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollTo(_scroll - e.Delta * Math.Max(S(40), _tileHeight / 2) / 120);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor == Color.Transparent ? ModernUi.Background : BackColor);
        for (int i = FirstVisible(); i < _items.Count; i++)
        {
            var bounds = CardBounds(i);
            if (bounds.Top > ClientSize.Height) break;
            if (bounds.Bottom < 0 || !bounds.IntersectsWith(e.ClipRectangle)) continue;
            DrawCard?.Invoke(g, _items[i], bounds);
            if (_dragging && _items[i].Selected)
            {
                using var veil = new SolidBrush(Color.FromArgb(120, ModernUi.Background));
                g.FillRectangle(veil, bounds);
            }
        }
        if (Focused && ShowFocusCues && _focus >= 0 && _focus < _items.Count)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(CardBounds(_focus), 3, 3), ModernUi.Text, ModernUi.Background);
        if (_dragging && _insertAt >= 0) DrawInsertionBar(g);
    }

    private void DrawInsertionBar(Graphics g)
    {
        Rectangle anchor;
        int x;
        if (_insertAt < _items.Count)
        {
            anchor = CardBounds(_insertAt);
            x = anchor.Left - S(Gap) / 2;
        }
        else
        {
            anchor = CardBounds(_items.Count - 1);
            x = anchor.Right + S(Gap) / 2;
        }
        using var pen = new Pen(ModernUi.Accent, S(4)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        g.DrawLine(pen, x, anchor.Top + S(4), x, anchor.Bottom - S(4));
    }

    // ── Mouse ────────────────────────────────────────────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var item = ItemAt(e.Location);
        if (e.Button == MouseButtons.Right)
        {
            if (item != null && !item.Selected) SelectOnly(item);
            ContextMenuRequested?.Invoke(item, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (item == null)
        {
            if ((ModifierKeys & (Keys.Control | Keys.Shift)) == 0) SelectOnly(null);
            return;
        }
        int index = _items.IndexOf(item);
        if ((ModifierKeys & Keys.Control) != 0)
        {
            item.Selected = !item.Selected;
            _anchor = _focus = index;
            Invalidate();
            SelectionChanged?.Invoke();
            return;
        }
        if ((ModifierKeys & Keys.Shift) != 0 && _anchor >= 0)
        {
            int from = Math.Min(_anchor, index), to = Math.Max(_anchor, index);
            for (int i = 0; i < _items.Count; i++) _items[i].Selected = i >= from && i <= to;
            _focus = index;
            Invalidate();
            SelectionChanged?.Invoke();
            return;
        }
        // A press on an already selected card keeps the multi-selection (it may be dragged as a group).
        if (!item.Selected) SelectOnly(item);
        else { _anchor = _focus = index; }
        _pressed = item;
        _mouseDown = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = ItemAt(e.Location);
        if (!ReferenceEquals(hover, _hoverItem))
        {
            _hoverItem = hover;
            _toolTip.SetToolTip(this, hover?.ToolTip ?? "");
            Invalidate();
        }
        if (_pressed == null || e.Button != MouseButtons.Left) return;
        if (!_dragging)
        {
            var size = SystemInformation.DragSize;
            if (Math.Abs(e.X - _mouseDown.X) < size.Width && Math.Abs(e.Y - _mouseDown.Y) < size.Height) return;
            if (CanReorder?.Invoke() == false) { _pressed = null; return; }
            _dragging = true;
            Cursor = Cursors.SizeAll;
        }
        _dragPoint = e.Location;
        UpdateInsertion(e.Location);
        _autoScroll.Enabled = e.Y < S(40) || e.Y > ClientSize.Height - S(40);
    }

    private void UpdateInsertion(Point p)
    {
        int insert = _items.Count;
        for (int i = FirstVisible(); i < _items.Count; i++)
        {
            var bounds = CardBounds(i);
            if (bounds.Top > ClientSize.Height) break;
            // The row the pointer is in, then the card whose middle is to the right of it.
            if (p.Y > bounds.Bottom + S(Gap) / 2) continue;
            if (p.X < bounds.Left + bounds.Width / 2) { insert = i; break; }
            if (i % _columns == _columns - 1 || i == _items.Count - 1) { insert = i + 1; break; }
        }
        if (insert != _insertAt) { _insertAt = insert; Invalidate(); }
    }

    private void AutoScrollStep()
    {
        if (!_dragging) { _autoScroll.Stop(); return; }
        int step = S(18);
        if (_dragPoint.Y < S(40)) ScrollTo(_scroll - step);
        else if (_dragPoint.Y > ClientSize.Height - S(40)) ScrollTo(_scroll + step);
        UpdateInsertion(_dragPoint);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var pressed = _pressed;
        bool dragged = _dragging;
        _pressed = null;
        _autoScroll.Stop();
        if (dragged)
        {
            _dragging = false;
            Cursor = Cursors.Default;
            int insert = _insertAt;
            _insertAt = -1;
            Invalidate();
            var moving = SelectedItems;
            // Insert before the first card at or after the insertion point that is not itself moving.
            Item? before = null;
            for (int i = Math.Max(0, insert); i < _items.Count; i++)
                if (!_items[i].Selected) { before = _items[i]; break; }
            if (moving.Count > 0 && insert >= 0) ReorderRequested?.Invoke(moving, before);
            return;
        }
        if (pressed != null && e.Button == MouseButtons.Left && ReferenceEquals(ItemAt(e.Location), pressed)
            && (ModifierKeys & (Keys.Control | Keys.Shift)) == 0)
        {
            if (pressed.Selected && SelectedItems.Count > 1) SelectOnly(pressed);
            ItemClicked?.Invoke(pressed, e);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && ItemAt(e.Location) is { } item) ItemDoubleClicked?.Invoke(item, e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverItem != null) { _hoverItem = null; Invalidate(); }
    }

    public bool IsHovered(Item item) => ReferenceEquals(item, _hoverItem);

    // ── Keyboard ─────────────────────────────────────────────────────────────────────────────────

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        if (e.KeyCode == Keys.A && e.Control)
        {
            foreach (var item in _items) item.Selected = true;
            Invalidate();
            SelectionChanged?.Invoke();
            e.Handled = true;
            return;
        }
        int rowsPerPage = Math.Max(1, ClientSize.Height / Math.Max(1, _tileHeight + S(Gap)));
        int current = Math.Max(0, _focus);
        int next = e.KeyCode switch
        {
            Keys.Left => current - 1,
            Keys.Right => current + 1,
            Keys.Up => current - _columns,
            Keys.Down => current + _columns,
            Keys.Home => 0,
            Keys.End => _items.Count - 1,
            Keys.PageUp => current - _columns * rowsPerPage,
            Keys.PageDown => current + _columns * rowsPerPage,
            _ => int.MinValue,
        };
        if (e.KeyCode == Keys.Enter && _focus >= 0)
        {
            ItemDoubleClicked?.Invoke(_items[_focus], new MouseEventArgs(MouseButtons.Left, 2, 0, 0, 0));
            e.Handled = true;
            return;
        }
        if (next == int.MinValue) return;
        next = Math.Clamp(next, 0, _items.Count - 1);
        if (e.Shift && _anchor >= 0)
        {
            int from = Math.Min(_anchor, next), to = Math.Max(_anchor, next);
            for (int i = 0; i < _items.Count; i++) _items[i].Selected = i >= from && i <= to;
            _focus = next;
            Invalidate();
            SelectionChanged?.Invoke();
        }
        else SelectOnly(_items[next]);
        EnsureVisible(_items[next]);
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _autoScroll.Dispose();
        }
        base.Dispose(disposing);
    }
}
