namespace EveryStage.Terminal.UI;

/// <summary>
/// Event notices (第二十一轮): a fixed area at the bottom of the right column — exactly
/// <see cref="MaxVisible"/> slots tall, so it never grows, pushes or covers anything else. Newest at the
/// bottom; a fourth notice drops the oldest. A notice closes by itself after 3 s (one with action
/// buttons — 重试 / 撤销 — after 10 s, so there is time to press it).
///
/// Persistent notices (DEV-10) are the exception: a <see cref="ToastSeverity.Critical"/> notice, or one
/// shown with <see cref="ShowSticky"/>, stays until the operator dismisses it and is never evicted by a
/// newer notice. While every slot holds a persistent notice, ordinary notices wait in a queue and
/// appear (with a fresh lifetime) as slots free up — a decision the operator has not made yet is
/// never silently dropped. More persistent notices than slots stack above the fixed area rather than
/// hiding one where its × cannot be reached.
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
    private readonly HashSet<ToastNotification> _persistent = new();
    private readonly List<PendingNotice> _pending = new();
    private bool _evicting;
    /// <summary>One ToolTip for every toast's full-text hover (P-10) — a ToolTip can serve
    /// any number of controls, replacing the per-toast instance ToastNotification used to
    /// allocate and never dispose.</summary>
    private readonly ToolTip _sharedToolTip = new();
    private bool _floating;

    /// <summary>A notice that could not be shown yet (every slot holds a persistent notice): shown,
    /// with a fresh lifetime, when a slot frees up. Message, severity, actions, sticky.</summary>
    private sealed record PendingNotice(string Message, ToastSeverity Severity, ToastAction[] Actions, bool Sticky);

    /// <summary>Raised after the visible notices or the waiting queue change (a notice shown,
    /// dismissed, evicted or promoted): the collapsed-column count badge (DEV-10) listens.</summary>
    public event Action? NoticesChanged;

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

    /// <summary>How many of the visible notices are persistent (never auto-dismissed, never evicted).</summary>
    public int StickyCount => _persistent.Count;

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

    /// <summary>An ordinary notice: auto-dismissed, and evicted by a newer notice when full — unless
    /// every slot holds a persistent notice, in which case it waits (null is returned: the notice
    /// appears when a slot frees up).</summary>
    public ToastNotification? Show(string message, ToastSeverity severity, params ToastAction[] actions) =>
        ShowCore(message, severity, sticky: false, actions);

    /// <summary>A notice that stays until the operator dismisses it and is never evicted: a decision
    /// the operator has not made yet (DEV-06). Critical notices are persistent without asking.</summary>
    public ToastNotification ShowSticky(string message, ToastSeverity severity, params ToastAction[] actions) =>
        ShowCore(message, severity, sticky: true, actions)!;

    private ToastNotification? ShowCore(string message, ToastSeverity severity, bool sticky, ToastAction[] actions)
    {
        bool persistent = sticky || severity == ToastSeverity.Critical;
        if (!persistent && _toasts.Count >= MaxVisible)
        {
            // No eviction while a Show is making room: a dismissal below would otherwise promote a
            // queued notice into the freed slot and the new notice would then overflow past MaxVisible.
            _evicting = true;
            try
            {
                while (_toasts.Count >= MaxVisible)
                {
                    var victim = _toasts.FirstOrDefault(t => !_persistent.Contains(t));
                    if (victim == null)
                    {
                        _pending.Add(new PendingNotice(message, severity, actions, sticky));
                        RaiseNoticesChanged();
                        return null;
                    }
                    victim.Dismiss();
                }
            }
            finally { _evicting = false; }
        }
        var toast = AddToast(message, severity, actions, persistent);
        PromotePending();
        RaiseNoticesChanged();
        return toast;
    }

    private ToastNotification AddToast(string message, ToastSeverity severity, ToastAction[] actions, bool persistent)
    {
        var toast = new ToastNotification(message, severity, actions, Math.Max(S(120), ClientSize.Width - S(Margin_) * 2), S(SlotHeight));
        toast.AttachToolTip(_sharedToolTip);
        System.Windows.Forms.Timer? expiry = null;
        if (!persistent)
        {
            expiry = new System.Windows.Forms.Timer { Interval = (int)(toast.HasActions ? ActionLifetime : Lifetime).TotalMilliseconds };
            expiry.Tick += (_, _) => toast.Dismiss();
        }
        toast.Dismissed += () =>
        {
            expiry?.Dispose();
            Remove(toast);
        };
        if (persistent) _persistent.Add(toast);
        _toasts.Add(toast);
        Controls.Add(toast);
        expiry?.Start();
        LayoutToasts();
        if (_floating) BringToFront();
        return toast;
    }

    private void PromotePending()
    {
        while (_toasts.Count < MaxVisible && _pending.Count > 0)
        {
            var next = _pending[0];
            _pending.RemoveAt(0);
            AddToast(next.Message, next.Severity, next.Actions, next.Sticky || next.Severity == ToastSeverity.Critical);
        }
    }

    private void RaiseNoticesChanged()
    {
        foreach (var handler in NoticesChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action)handler)(); } catch (Exception) { }
        }
    }

    private void Remove(ToastNotification toast)
    {
        if (!_toasts.Remove(toast)) return;
        _persistent.Remove(toast);
        Controls.Remove(toast);
        toast.Dispose();
        LayoutToasts();
        if (!_evicting) PromotePending();
        RaiseNoticesChanged();
    }

    private void LayoutToasts()
    {
        // More persistent notices than slots stack above the fixed area (their × stays reachable)
        // instead of one being clipped away where it can never be dismissed.
        int wantedHeight = _toasts.Count > MaxVisible
            ? S(SlotHeight) * _toasts.Count + S(Gap) * (_toasts.Count - 1) + S(Margin_) * 2
            : AreaHeight;
        if (Height != wantedHeight) Height = wantedHeight;
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
