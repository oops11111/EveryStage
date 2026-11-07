using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

/// <summary>
/// 快捷键速查 (L-04): a read-only list of every assignable hotkey with its CURRENT binding, opened
/// from the rail's「快捷键」button. Operators run the show from the keyboard, but until this existed
/// the only way to look a binding up was digging through 设置's hotkey section one row at a time —
/// and bindings are user-changeable, so a printed cheat sheet would go stale. This dialog therefore
/// renders straight from the live <see cref="AppSettings"/> via <see cref="Hotkeys.Label"/> /
/// <see cref="Hotkeys.Format"/> — the same single source the settings page edits — so it can never
/// disagree with what the keys actually do. Editing stays in 设置 · 热键; this is lookup only.
/// </summary>
public sealed class HotkeyListDialog : Form
{
    public HotkeyListDialog(AppSettings settings)
    {
        Text = "快捷键";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(400, 264);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(16, 14, 16, 0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var name = new Label { Text = Hotkeys.Label(action), AutoSize = true, Margin = new Padding(0, 3, 8, 3) };
            var key = new Label
            {
                Text = Hotkeys.Format(settings.HotkeyFor(action)),
                AutoSize = true,
                Margin = new Padding(0, 3, 0, 3),
                Font = new Font(ModernUi.UiFontSemibold, 10F),
            };
            layout.Controls.Add(name);
            layout.Controls.Add(key);
        }

        var note = new Label
        {
            Text = "在「设置 · 热键」里可以改键。另：单击文件进预览、双击上屏（可在设置里改为单击即上屏）。",
            AutoSize = false,
            Bounds = new Rectangle(16, 196, 368, 30),
        };
        ModernUi.SetForegroundRole(note, UiForegroundRole.Muted);

        var closeButton = new Button { Text = "关闭", DialogResult = DialogResult.OK, Bounds = new Rectangle(304, 228, 80, 28) };
        AcceptButton = closeButton;
        CancelButton = closeButton;

        Controls.Add(layout);
        Controls.Add(note);
        Controls.Add(closeButton);
        ModernUi.StyleDialog(this);
    }
}
