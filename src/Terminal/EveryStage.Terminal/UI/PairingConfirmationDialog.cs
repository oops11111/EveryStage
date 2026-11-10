using EveryStage.Discovery;
using EveryStage.Terminal.Devices;

namespace EveryStage.Terminal.UI;

/// <summary>
/// PLANNING.md §7's "首次需接收端确认（弹窗/PIN码）". The current pairing v2 flow
/// uses a six-digit comparison code derived by the discovery handshake; this dialog is the
/// operator-side accept/decline step after the code has been verified visually.
/// </summary>
public sealed class PairingConfirmationDialog : Form
{
    private readonly CheckBox _allowCastCheckbox;

    public bool Accepted { get; private set; }
    public bool AllowCast => _allowCastCheckbox.Checked;
    // 监看 and 信任设备 are not offered (product decision: 只留「允许投放」; nothing implements them, and
    // trust never puts a device straight on the big screen). New pairings store the safe values.
    public bool AllowMonitor => false;
    public TrustMode TrustMode => TrustMode.RequireManualConfirmation;

    public PairingConfirmationDialog(PairingRequest request)
    {
        Text = "投屏设备配对请求";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(380, 232);
        TopMost = true;
        // Its own task-bar button: with the Terminal in the tray (or the Caster on the same PC in front),
        // an owned dialog without one was easy to miss, and an unanswered request expires after 2 minutes.
        ShowInTaskbar = true;
        Shown += (_, _) => { Activate(); BringToFront(); };

        string code = request.VerificationCode.Length == 6
            ? request.VerificationCode.Insert(3, " ")
            : request.VerificationCode;
        var codeLabel = new Label
        {
            Text = code,
            Bounds = new Rectangle(12, 18, 356, 52),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(ModernUi.UiFontSemibold, 28F),
            ForeColor = ModernUi.Accent,
        };
        ModernUi.SetForegroundRole(codeLabel, UiForegroundRole.Accent);
        var codeHint = new Label
        {
            Text = "请核对投屏机上的数字一致后再接受",
            Bounds = new Rectangle(12, 68, 356, 24),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = ModernUi.Text,
        };
        var infoLabel = new Label
        {
            Text = $"设备 \"{request.DeviceName}\" ({request.RemoteAddress}) 请求配对。\n\n" +
                   $"设备ID: {request.DeviceId}",
            Bounds = new Rectangle(12, 100, 356, 58),
        };

        _allowCastCheckbox = new CheckBox
        {
            Text = "允许被投放（该设备可以向本终端机投屏）",
            Bounds = new Rectangle(12, 166, 356, 24),
        };
        var acceptButton = new Button { Text = "接受", Bounds = new Rectangle(180, 198, 88, 28), DialogResult = DialogResult.OK };
        var declineButton = new Button { Text = "拒绝", Bounds = new Rectangle(280, 198, 88, 28), DialogResult = DialogResult.Cancel };

        AcceptButton = acceptButton;
        CancelButton = declineButton;

        Controls.AddRange(new Control[]
        {
            codeLabel, codeHint, infoLabel, _allowCastCheckbox, acceptButton, declineButton,
        });
        ModernUi.StyleDialog(this);

        FormClosed += (_, _) => Accepted = DialogResult == DialogResult.OK;
    }
}
