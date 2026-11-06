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

        var accentBar = ModernUi.SetBackgroundRole(new Panel
        {
            Bounds = new Rectangle(0, 0, S(AccentBarWidth), height),
        }, severity switch { ToastSeverity.Critical => UiBackgroundRole.Danger, ToastSeverity.Info => UiBackgroundRole.Accent, _ => UiBackgroundRole.Warning });

        var closeButton = ModernUi.SetBackgroundRole(ModernUi.SetForegroundRole(new Button
        {
            Text = "×",
            FlatStyle = FlatStyle.Flat,
            Bounds = new Rectangle(width - S(24), S(4), S(20), S(20)),
            AccessibleName = "关闭通知",
        }, UiForegroundRole.Muted), UiBackgroundRole.ContentRaised);
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.Click += (_, _) => Dismiss();

        // Action buttons sit on the right, under the "×"; the message takes the rest.
        int right = width - S(6);
        if (actions != null)
        {
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                var action = actions[i];
                int buttonWidth = Math.Max(S(44), TextRenderer.MeasureText(action.Label, Font).Width + S(14));
                right -= buttonWidth;
                var button = new Button
                {
                    Text = action.Label,
                    Bounds = new Rectangle(right, height - S(26), buttonWidth, S(22)),
                };
                ModernUi.StyleButton(button, danger: severity == ToastSeverity.Critical && action.Label == "移除");
                button.Font = new Font(ModernUi.UiFont, 8.5F);
                button.Padding = Padding.Empty;
                button.Click += (_, _) =>
                {
                    action.Callback();
                    Dismiss();
                };
                Controls.Add(button);
                right -= S(4);
            }
        }
        int messageRight = HasActions ? right : width - S(26);
        var messageLabel = ModernUi.SetForegroundRole(new Label
        {
            Text = message,
            AutoEllipsis = true,
            Font = new Font(ModernUi.UiFont, 8.5F),
            Bounds = Rectangle.FromLTRB(S(AccentBarWidth) + S(8), S(5), Math.Max(S(AccentBarWidth) + S(40), messageRight), height - S(5)),
        }, UiForegroundRole.Text);

        Controls.Add(accentBar);
        Controls.Add(messageLabel);
        Controls.Add(closeButton);
    }
}
