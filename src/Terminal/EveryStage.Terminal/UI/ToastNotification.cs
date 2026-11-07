namespace EveryStage.Terminal.UI;

/// <summary>PLANNING.md §11 "异常提示"："按严重程度分色（红=严重/黄=提示）". <see cref="Critical"/> is
/// red, <see cref="Warning"/> is yellow; Info is the non-warning feedback for a successful take.</summary>
public enum ToastSeverity { Warning, Critical, Info }

/// <summary>One button on a Toast — PLANNING.md §11's "涉及播放的异常需带可执行按钮（重试/移除）".
/// Invoking <see cref="Callback"/> is the button click handler's whole job; <see cref="ToastStack"/>
/// dismisses the toast itself right after, so a caller building one of these doesn't also need to
/// arrange for the toast to go away.</summary>
public sealed record ToastAction(string Label, Action Callback);

/// <summary>
/// One row in <see cref="ToastStack"/> (第二十一轮: the right column, 3 s, at most 3): a severity-coloured
/// accent bar, the message (two lines, ellipsis), an "×" and any action buttons in one compact row.
/// The size is given by the stack (column width × one fixed slot), never by the message length.
/// </summary>
public sealed class ToastNotification : Panel
{
    private const int AccentBarWidth = 4;

    // P-10: one shared font for every toast instead of a new Font per notification (which
    // was also never disposed). Process-lifetime by design, like the other static UI fonts.
    private static readonly Font SharedFont = new(ModernUi.UiFont, 8F);

    private readonly Label _messageLabel;

    /// <summary>Raised once, whichever way this toast goes away — "×", an action button, the stack's
    /// timer or <see cref="Dismiss"/>.</summary>
    public event Action? Dismissed;

    public string Message { get; }
    public bool HasActions { get; }

    /// <summary>Closes the toast from code (its time is up, or a 10 s 撤销 offer ran out).</summary>
    public void Dismiss()
    {
        if (!IsDisposed && !Disposing) Dismissed?.Invoke();
    }

    public ToastNotification(string message, ToastSeverity severity, IReadOnlyList<ToastAction>? actions, int width, int height)
    {
        Message = message;
        HasActions = actions is { Count: > 0 };
        ModernUi.SetBackgroundRole(this, UiBackgroundRole.ContentRaised);
        Size = new Size(width, height);
        int S(int v) => LogicalToDeviceUnits(v);
        var font = SharedFont;

        // One compact line: accent bar · message · actions as links · ×.
        var accentBar = ModernUi.SetBackgroundRole(new Panel
        {
            Bounds = new Rectangle(0, 0, S(3), height),
        }, severity switch { ToastSeverity.Critical => UiBackgroundRole.Danger, ToastSeverity.Info => UiBackgroundRole.Accent, _ => UiBackgroundRole.Warning });

        var closeButton = ModernUi.SetForegroundRole(new Label
        {
            Text = "×",
            Font = font,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(width - S(16), 0, S(16), height),
            Cursor = Cursors.Hand,
            AccessibleName = "关闭通知",
        }, UiForegroundRole.Muted);
        closeButton.Click += (_, _) => Dismiss();
        Controls.Add(closeButton);

        int right = closeButton.Left - S(2);
        if (actions != null)
        {
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                var action = actions[i];
                int linkWidth = TextRenderer.MeasureText(action.Label, font).Width + S(6);
                right -= linkWidth;
                var link = new LinkLabel
                {
                    Text = action.Label,
                    Font = font,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Bounds = new Rectangle(right, 0, linkWidth, height),
                    LinkColor = severity == ToastSeverity.Critical ? ModernUi.Danger : ModernUi.AccentText,
                    ActiveLinkColor = ModernUi.Text,
                    BackColor = Color.Transparent,
                };
                link.LinkClicked += (_, _) =>
                {
                    action.Callback();
                    Dismiss();
                };
                Controls.Add(link);
                right -= S(2);
            }
        }
        _messageLabel = ModernUi.SetForegroundRole(new Label
        {
            Text = message.Replace("\r", " ").Replace("\n", " · "),
            AutoEllipsis = true,
            Font = font,
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = Rectangle.FromLTRB(S(3) + S(5), 0, Math.Max(S(40), right), height),
        }, UiForegroundRole.Text);

        Controls.Add(accentBar);
        Controls.Add(_messageLabel);
    }

    /// <summary>Points the given (shared) ToolTip at the message label, showing the full
    /// un-truncated text. Called by <see cref="ToastStack"/> with its single ToolTip instance
    /// (P-10) — a ToolTip serves any number of controls, so toasts no longer each allocate
    /// one and leak it on dispose.</summary>
    internal void AttachToolTip(ToolTip toolTip) => toolTip.SetToolTip(_messageLabel, Message);
}
