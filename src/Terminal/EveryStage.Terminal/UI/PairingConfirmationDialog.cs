using EveryStage.Discovery;
using EveryStage.Terminal.Devices;

namespace EveryStage.Terminal.UI;

/// <summary>
/// PLANNING.md §7's "首次需接收端确认（弹窗/PIN码）". This implements the popup-confirmation half
/// of that (accept/decline with the device's claimed name/ID/address shown); a PIN-code exchange
/// isn't implemented here — <see cref="DiscoveryProtocol"/> doesn't carry one, and adding one is a
/// protocol-design decision that belongs with the rest of that draft's caveats, not something to
/// bolt on one-sidedly in the confirmation UI alone.
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
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 236);
        TopMost = true;

        var infoLabel = new Label
        {
            Text = $"设备 \"{request.DeviceName}\" ({request.RemoteAddress}) 请求配对。\n\n" +
                   $"设备ID: {request.DeviceId}",
            Bounds = new Rectangle(12, 12, 296, 70),
        };

        _allowCastCheckbox = new CheckBox
        {
            Text = "允许被投放（该设备可以向本终端机投屏）",
            Bounds = new Rectangle(12, 90, 296, 24),
        };
        _allowMonitorCheckbox = new CheckBox
        {
            Text = "允许被监看（该设备可以查看本终端机状态）",
            Bounds = new Rectangle(12, 116, 296, 24),
        };
        // Same honest disclaimer as EditPairedDevicePermissionsDialog (which lets this same flag be
        // revised later) — 监看 has no actual implementation anywhere in this repo yet, only this
        // permission flag being collected/stored/displayed.
        var monitorDisclaimer = new Label
        {
            Text = "（监看功能本身尚未实现，此开关暂无实际效果）",
            ForeColor = Color.DimGray,
            Bounds = new Rectangle(28, 140, 280, 16),
        };
        _trustCheckbox = new CheckBox
        {
            Text = "信任此设备（以后自动接受，无需再次确认）",
            Bounds = new Rectangle(12, 158, 296, 24),
        };

        var acceptButton = new Button { Text = "接受", Bounds = new Rectangle(120, 192, 88, 28), DialogResult = DialogResult.OK };
        var declineButton = new Button { Text = "拒绝", Bounds = new Rectangle(216, 192, 88, 28), DialogResult = DialogResult.Cancel };

        AcceptButton = acceptButton;
        CancelButton = declineButton;

        Controls.AddRange(new Control[]
        {
            infoLabel, _allowCastCheckbox, _allowMonitorCheckbox, monitorDisclaimer, _trustCheckbox, acceptButton, declineButton,
        });
        ModernUi.StyleDialog(this);

        FormClosed += (_, _) => Accepted = DialogResult == DialogResult.OK;
    }
}
