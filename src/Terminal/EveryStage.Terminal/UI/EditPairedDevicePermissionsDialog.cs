using EveryStage.Terminal.Devices;

namespace EveryStage.Terminal.UI;

/// <summary>
/// Lets the operator revise an already-paired device's trust/permission flags without having to
/// remove and re-pair it. <see cref="PairingConfirmationDialog"/> is the only other place these
/// three fields were ever settable (at first-pairing time) — until now, "移除配对" (delete the
/// record and force a brand-new pairing request) was the only way to change a mistake or a policy
/// change after the fact, e.g. a device that no longer needs "允许被投放" doesn't need to be
/// forgotten entirely just to revoke that one permission. Deliberately a separate, small dialog
/// class rather than reusing <see cref="PairingConfirmationDialog"/> itself: that class's whole
/// framing (设备 X 请求配对，接受/拒绝) doesn't fit "edit an existing record" at all — there is no
/// pending request here, and "接受/拒绝" would be a confusing pair of button labels for "保存/取消".
/// </summary>
public sealed class EditPairedDevicePermissionsDialog : Form
{
    private readonly CheckBox _allowCastCheckbox;
    private readonly CheckBox _allowMonitorCheckbox;
    private readonly CheckBox _trustCheckbox;

    public bool AllowCast => _allowCastCheckbox.Checked;
    public bool AllowMonitor => _allowMonitorCheckbox.Checked;
    public TrustMode TrustMode => _trustCheckbox.Checked ? TrustMode.Trusted : TrustMode.RequireManualConfirmation;

    public EditPairedDevicePermissionsDialog(PairedDevice device)
    {
        Text = $"编辑权限 — {device.DeviceName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 196);

        var infoLabel = new Label
        {
            Text = $"设备ID: {device.DeviceId}\n配对时间: {device.PairedAt.LocalDateTime:yyyy-MM-dd HH:mm}",
            Bounds = new Rectangle(12, 12, 296, 32),
        };

        _allowCastCheckbox = new CheckBox
        {
            Text = "允许被投放（该设备可以向本终端机投屏）",
            Checked = device.AllowCast,
            Bounds = new Rectangle(12, 52, 296, 24),
        };
        _allowMonitorCheckbox = new CheckBox
        {
            Text = "允许被监看（该设备可以查看本终端机状态）",
            Checked = device.AllowMonitor,
            Bounds = new Rectangle(12, 78, 296, 24),
        };
        // Honest, same spirit as AudioPropertiesDialog's "（当前是占位：...）" labels — 监看
        // (remote-viewing this Terminal's state) has no actual implementation anywhere in this
        // repo yet, only this permission flag being collected/stored/displayed (PLANNING.md §7's
        // "权限分离"). Toggling this checkbox changes nothing observable today; it exists so the
        // stored flag is ready for whenever that feature gets built, without silently misleading
        // whoever's editing this dialog into thinking it already does something.
        var monitorDisclaimer = new Label
        {
            Text = "（监看功能本身尚未实现，此开关暂无实际效果）",
            ForeColor = Color.DimGray,
            Bounds = new Rectangle(28, 102, 280, 16),
        };
        _trustCheckbox = new CheckBox
        {
            Text = "信任此设备（以后自动接受，无需再次确认）",
            Checked = device.TrustMode == TrustMode.Trusted,
            Bounds = new Rectangle(12, 120, 296, 24),
        };

        var saveButton = new Button { Text = "保存", Bounds = new Rectangle(120, 154, 88, 28), DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "取消", Bounds = new Rectangle(216, 154, 88, 28), DialogResult = DialogResult.Cancel };

        AcceptButton = saveButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[]
        {
            infoLabel, _allowCastCheckbox, _allowMonitorCheckbox, monitorDisclaimer, _trustCheckbox, saveButton, cancelButton,
        });
        ModernUi.StyleDialog(this);
    }
}
