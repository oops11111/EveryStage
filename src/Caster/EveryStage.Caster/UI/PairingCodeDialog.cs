namespace EveryStage.Caster.UI;

public sealed class PairingCodeDialog : Form
{
    private readonly Label _codeLabel;

    public PairingCodeDialog(string terminalName)
    {
        Text = "核对配对码";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(360, 176);
        TopMost = true;

        var title = new Label
        {
            Text = $"正在与 \"{terminalName}\" 配对",
            Bounds = new Rectangle(16, 16, 328, 28),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 11F),
        };
        _codeLabel = new Label
        {
            Text = "--- ---",
            Bounds = new Rectangle(16, 48, 328, 58),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 30F),
            ForeColor = ModernUi.Accent,
        };
        var hint = new Label
        {
            Text = "请在终端机上核对同一组数字后点接受",
            Bounds = new Rectangle(16, 108, 328, 24),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.DimGray,
        };
        var cancel = new Button { Text = "取消", Bounds = new Rectangle(256, 138, 88, 28), DialogResult = DialogResult.Cancel };
        CancelButton = cancel;
        Controls.AddRange(new Control[] { title, _codeLabel, hint, cancel });
        BackColor = ModernUi.Surface;
        ForeColor = ModernUi.Text;
    }

    public void SetCode(string verificationCode)
    {
        if (IsDisposed) return;
        _codeLabel.Text = verificationCode.Length == 6 ? verificationCode.Insert(3, " ") : verificationCode;
    }
}
