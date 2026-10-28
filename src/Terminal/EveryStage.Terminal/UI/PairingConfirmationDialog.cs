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
    private readonly CheckBox _allowMonitorCheckbox;
    private readonly CheckBox _trustCheckbox;

    public bool Accepted { get; private set; }
    public bool AllowCast => _allowCastCheckbox.Checked;
    public bool AllowMonitor => _allowMonitorCheckbox.Checked;
    public TrustMode TrustMode => _trustCheckbox.Checked ? TrustMode.Trusted : TrustMode.RequireManualConfirmation;

    public PairingConfirmationDialog(PairingRequest request)
    {
        Text = "投屏设备配对请求";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(380, 292);
        TopMost = true;

        string code = request.VerificationCode.Length == 6
            ? request.VerificationCode.Insert(3, " ")
            : request.VerificationCode;
        var codeLabel = new Label
        {
            Text = code,
            Bounds = new Rectangle(12, 18, 356, 52),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 28F),
            ForeColor = ModernUi.Accent,
        };
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
        _allowMonitorCheckbox = new CheckBox
        {
            Text = "允许被监看（该设备可以查看本终端机状态）",
            Bounds = new Rectangle(12, 192, 356, 24),
        };
        // Same honest disclaimer as EditPairedDevicePermissionsDialog (which lets this same flag be
        // revised later) — 监看 has no actual implementation anywhere in this repo yet, only this
        // permission flag being collected/stored/displayed.
        var monitorDisclaimer = new Label
        {
            Text = "（监看功能本身尚未实现，此开关暂无实际效果）",
            ForeColor = Color.DimGray,
            Bounds = new Rectangle(28, 216, 340, 16),
        };
        _trustCheckbox = new CheckBox
        {
            // Honest label (audit B-14): nothing auto-accepts on this flag yet — and it must not until the
            // requester's identity can be verified (a first-contact DeviceId is whatever the sender claims).
            Text = "标记为信任设备（自动接受尚未实现，每次仍需确认）",
            Bounds = new Rectangle(12, 234, 356, 24),
        };

        var acceptButton = new Button { Text = "接受", Bounds = new Rectangle(180, 258, 88, 28), DialogResult = DialogResult.OK };
        var declineButton = new Button { Text = "拒绝", Bounds = new Rectangle(280, 258, 88, 28), DialogResult = DialogResult.Cancel };

        AcceptButton = acceptButton;
        CancelButton = declineButton;

        Controls.AddRange(new Control[]
        {
            codeLabel, codeHint, infoLabel, _allowCastCheckbox, _allowMonitorCheckbox, monitorDisclaimer, _trustCheckbox, acceptButton, declineButton,
        });
        ModernUi.StyleDialog(this);

        FormClosed += (_, _) => Accepted = DialogResult == DialogResult.OK;
    }
}
