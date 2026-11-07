namespace EveryStage.Terminal.UI;

/// <summary>
/// Event notices (第二十一轮): a fixed area at the bottom of the right column — exactly
/// <see cref="MaxVisible"/> slots tall, so it never grows, pushes or covers anything else. Newest at the
/// bottom; a fourth notice drops the oldest. A notice closes by itself after 3 s (one with action
/// buttons — 重试 / 撤销 — after 10 s, so there is time to press it).
///
/// While the 监看 column is collapsed to its thin strip the same stack floats at the bottom-right of the
/// window instead (<see cref="Floating"/>), the same size.
/// </summary>
public sealed class ToastStack : Panel
{
    public const int MaxVisible = 3;
    private const int SlotHeight = 16; // one line (a third of the earlier notice height)
    private const int Gap = 2;
    private const int Margin_ = 4;
    private const int FloatingWidth = 320;
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ActionLifetime = TimeSpan.FromSeconds(10);

    private readonly List<ToastNotification> _toasts = new();
    /// <summary>One ToolTip for every toast's full-text hover (P-10) — a ToolTip can serve
    /// any number of controls, replacing the per-toast instance ToastNotification used to
    /// allocate and never dispose.</summary>
    private readonly ToolTip _sharedToolTip = new();
    private bool _floating;

    public ToastStack()
    {
        BackColor = Color.Transparent;
        Height = AreaHeight;
    }

    private int S(int v) => LogicalToDeviceUnits(v);

    /// <summary>The fixed height of the area (three slots).</summary>
    public int AreaHeight => S(SlotHeight) * MaxVisible + S(Gap) * (MaxVisible - 1) + S(Margin_) * 2;

    /// <summary>The notices on screen, oldest first.</summary>
    public IReadOnlyList<ToastNotification> Notices => _toasts;

    /// <summary>Floating over the window's bottom-right corner (column collapsed) instead of docked.</summary>
    public bool Floating
    {
        get => _floating;
        set
        {
            _floating = value;
            Dock = value ? DockStyle.None : DockStyle.Bottom;
            Height = AreaHeight;
            if (value) { Width = S(FloatingWidth); Reposition(); BringToFront(); }
            LayoutToasts();
        }
    }

    /// <summary>Space kept free at the parent's bottom edge while floating (the music bar).</summary>
    public int BottomInset
    {
        get => _bottomInset;
        set { _bottomInset = value; Reposition(); }
    }
    private int _bottomInset;

    /// <summary>Space kept free at the parent's right edge while floating (the collapsed strip).</summary>
    public int RightInset
    {
        get => _rightInset;
        set { _rightInset = value; Reposition(); }
    }
    private int _rightInset;

    public ToastNotification Show(string message, ToastSeverity severity, params ToastAction[] actions)
    {
        while (_toasts.Count >= MaxVisible) _toasts[0].Dismiss();
        var toast = new ToastNotification(message, severity, actions, Math.Max(S(120), ClientSize.Width - S(Margin_) * 2), S(SlotHeight));
        toast.AttachToolTip(_sharedToolTip);
        var expiry = new System.Windows.Forms.Timer { Interval = (int)(toast.HasActions ? ActionLifetime : Lifetime).TotalMilliseconds };
        expiry.Tick += (_, _) => toast.Dismiss();
        toast.Dismissed += () =>
        {
            expiry.Dispose();
            Remove(toast);
        };
        _toasts.Add(toast);
        Controls.Add(toast);
        expiry.Start();
        LayoutToasts();
        if (_floating) BringToFront();
        return toast;
    }

    private void Remove(ToastNotification toast)
    {
        if (!_toasts.Remove(toast)) return;
        Controls.Remove(toast);
        toast.Dispose();
        LayoutToasts();
    }

    private void LayoutToasts()
    {
        int width = Math.Max(S(120), ClientSize.Width - S(Margin_) * 2);
        int y = ClientSize.Height - S(Margin_);
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            var toast = _toasts[i];
            y -= toast.Height;
            toast.Bounds = new Rectangle(S(Margin_), y, width, toast.Height);
            y -= S(Gap);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _sharedToolTip.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        // Width changes rebuild nothing: toasts are short-lived; the next ones take the new width.
        foreach (var toast in _toasts) toast.Width = Math.Max(S(120), ClientSize.Width - S(Margin_) * 2);
        LayoutToasts();
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent != null) Parent.SizeChanged += (_, _) => Reposition();
        Reposition();
    }

    private void Reposition()
    {
        if (!_floating || Parent == null) return;
        Location = new Point(Parent.ClientSize.Width - _rightInset - S(12) - Width, Parent.ClientSize.Height - _bottomInset - S(8) - Height);
    }
}
