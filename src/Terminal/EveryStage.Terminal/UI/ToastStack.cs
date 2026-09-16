namespace EveryStage.Terminal.UI;

/// <summary>
/// PLANNING.md §11 "异常提示"："右下角Toast通知栈" — a vertical stack of <see cref="ToastNotification"/>
/// rows, pinned to its parent's bottom-right corner, growing upward as toasts are added (newest at
/// the bottom, closest to where a real notification stack "arrives from"). Deliberately does NOT use
/// WinForms <c>Anchor</c> for repositioning: this project has no dotnet in this sandbox to verify
/// <c>Anchor</c>'s exact interaction with a control whose own <see cref="Control.Height"/> changes
/// programmatically (as opposed to only the parent resizing), so <see cref="RepositionToBottomRight"/>
/// recomputes this control's <see cref="Control.Location"/> from scratch — against the parent's
/// current <see cref="Control.ClientSize"/>, not a cached value — every time either the parent resizes
/// or a toast is added/removed, which is simple enough to reason about without needing to compile it.
/// </summary>
public sealed class ToastStack : Panel
{
    public const int ToastWidth = 320;
    private const int Gap = 8;
    private const int Margin = 16;

    public ToastStack()
    {
        Width = ToastWidth;
        Height = 0;
        BackColor = Color.Transparent;
    }

    /// <summary>Adds one toast to the bottom of the stack and returns it (a caller rarely needs the
    /// instance back, but nothing stops one from dismissing it early, e.g. <c>toast.Dismissed?.Invoke()</c>
    /// isn't public — there's no supported "dismiss programmatically" today beyond the toast's own "×"
    /// button and its action buttons, both of which raise <see cref="ToastNotification.Dismissed"/>
    /// internally).</summary>
    public ToastNotification Show(string message, ToastSeverity severity, params ToastAction[] actions)
    {
        var toast = new ToastNotification(message, severity, actions)
        {
            Left = 0,
            Top = Height == 0 ? 0 : Height + Gap,
        };
        toast.Dismissed += () => Remove(toast);

        Controls.Add(toast);
        Height = toast.Bottom;
        RepositionToBottomRight();
        BringToFront();
        return toast;
    }

    private void Remove(ToastNotification toast)
    {
        int removedTop = toast.Top;
        int shiftAmount = toast.Height + Gap;

        Controls.Remove(toast);
        toast.Dispose();

        // Close the gap: every remaining toast that was below the removed one (stacked top-to-bottom,
        // so "below" means a larger Top) shifts up to take its place.
        foreach (Control remaining in Controls)
        {
            if (remaining.Top > removedTop) remaining.Top -= shiftAmount;
        }

        Height = Controls.Count == 0 ? 0 : Math.Max(0, Height - shiftAmount);
        RepositionToBottomRight();
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent != null)
        {
            Parent.SizeChanged += (_, _) => RepositionToBottomRight();
            RepositionToBottomRight();
        }
    }

    private void RepositionToBottomRight()
    {
        if (Parent == null) return;
        Left = Parent.ClientSize.Width - Margin - Width;
        Top = Parent.ClientSize.Height - Margin - Height;
    }
}
