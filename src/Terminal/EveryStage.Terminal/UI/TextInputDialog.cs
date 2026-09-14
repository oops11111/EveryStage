namespace EveryStage.Terminal.UI;

/// <summary>Minimal reusable "prompt for one line of text" dialog (scenario/activity naming) —
/// WinForms has no built-in equivalent of VB's InputBox, and pulling in Microsoft.VisualBasic for
/// one dialog isn't worth the odd dependency for a C#-only project.</summary>
public sealed class TextInputDialog : Form
{
    private readonly TextBox _textBox;

    public string Value => _textBox.Text;

    public TextInputDialog(string title, string prompt, string initialValue = "")
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 120);

        var promptLabel = new Label { Text = prompt, Bounds = new Rectangle(12, 12, 296, 20) };
        _textBox = new TextBox { Text = initialValue, Bounds = new Rectangle(12, 36, 296, 24) };

        var okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, Bounds = new Rectangle(140, 76, 80, 28) };
        var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(228, 76, 80, 28) };
        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[] { promptLabel, _textBox, okButton, cancelButton });
    }

    /// <summary>Shows the dialog and returns the entered text, or null if cancelled or left blank.</summary>
    public static string? Prompt(IWin32Window owner, string title, string promptText, string initialValue = "")
    {
        using var dialog = new TextInputDialog(title, promptText, initialValue);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        string trimmed = dialog.Value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
