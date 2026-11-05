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
/// One row in <see cref="ToastStack"/> — a message, a severity-colored accent bar, an always-present
/// "×" dismiss button, and zero or more action buttons (PLANNING.md §11: "非关键提示不强加按钮" —
/// passing no actions is the normal case for a <see cref="ToastSeverity.Warning"/> notice; actions
/// exist for the specific "涉及播放的异常" case the spec calls out by name).
///
/// Fixed width and height (matches <see cref="ToastStack"/>'s own width; height is the same whether
/// or not action buttons are present, just with an empty row at the bottom when they aren't) —
/// deliberately not auto-sized to message length: this project has no dotnet in this sandbox to
/// verify WinForms `AutoSize`/`PreferredSize` measurement behaves the way it's documented to, so a
/// fixed layout with <see cref="Label.AutoEllipsis"/> truncating an overlong message is the safer
/// choice here over code whose correctness depends on measurement APIs this repo can't exercise.
/// </summary>
public sealed class ToastNotification : Panel
{
    private const int AccentBarWidth = 4;
    private const int ActionButtonWidth = 64;
    private const int ActionButtonHeight = 26;

    /// <summary>Raised once, whichever way this toast goes away — the "×" button, or after any
    /// action button's callback runs. <see cref="ToastStack"/> is this event's only subscriber in
    /// practice, removing this control from the stack when it fires.</summary>
    public event Action? Dismissed;

    /// <summary>Closes the toast from code (e.g. a 10 s 撤销 offer running out).</summary>
    public void Dismiss()
    {
        if (!IsDisposed) Dismissed?.Invoke();
    }

    public ToastNotification(string message, ToastSeverity severity, IReadOnlyList<ToastAction>? actions = null)
    {
        int width = ToastStack.ToastWidth;
        const int height = 96; // fixed regardless of action count — see class doc comment on why.
        ModernUi.SetBackgroundRole(this, UiBackgroundRole.ContentRaised);
        Width = width;
        Height = height;

        var accentBar = ModernUi.SetBackgroundRole(new Panel
        {
            Bounds = new Rectangle(0, 0, AccentBarWidth, height),
        }, severity switch { ToastSeverity.Critical => UiBackgroundRole.Danger, ToastSeverity.Info => UiBackgroundRole.Accent, _ => UiBackgroundRole.Warning });

        var closeButton = ModernUi.SetBackgroundRole(ModernUi.SetForegroundRole(new Button
        {
            Text = "×",
            FlatStyle = FlatStyle.Flat,
            Bounds = new Rectangle(width - 28, 6, 20, 20),
        }, UiForegroundRole.Muted), UiBackgroundRole.ContentRaised);
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.Click += (_, _) => Dismissed?.Invoke();

        var messageLabel = ModernUi.SetForegroundRole(new Label
        {
            Text = message,
            AutoEllipsis = true,
            Bounds = new Rectangle(AccentBarWidth + 8, 8, width - AccentBarWidth - 8 - 28, height - 8 - ActionButtonHeight - 12),
        }, UiForegroundRole.Text);

        Controls.Add(accentBar);
        Controls.Add(messageLabel);
        Controls.Add(closeButton);

        if (actions is { Count: > 0 })
        {
            // Right-to-left so the first action in the list ends up leftmost — reads naturally as
            // "重试" before "移除" when a caller passes them in that order.
            int cursorX = width - 12;
            int y = height - ActionButtonHeight - 8;
            foreach (var action in actions)
            {
                cursorX -= ActionButtonWidth;
                var button = new Button
                {
                    Text = action.Label,
                    Bounds = new Rectangle(cursorX, y, ActionButtonWidth, ActionButtonHeight),
                };
                ModernUi.StyleButton(button, danger: severity == ToastSeverity.Critical && action.Label == "移除");
                button.Padding = Padding.Empty;
                button.Click += (_, _) =>
                {
                    action.Callback();
                    Dismissed?.Invoke();
                };
                Controls.Add(button);
                cursorX -= 8; // gap between buttons.
            }
        }
    }
}
