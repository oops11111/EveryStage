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

    public bool AllowCast => _allowCastCheckbox.Checked;
    // Only 「允许投放」 is offered (product decision); 监看 and 信任 keep whatever was stored.
    public bool AllowMonitor { get; }
    public TrustMode TrustMode { get; }

    public EditPairedDevicePermissionsDialog(PairedDevice device)
    {
        Text = $"编辑权限 — {device.DeviceName}";
        AllowMonitor = device.AllowMonitor;
        TrustMode = device.TrustMode;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 124);

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
        var saveButton = new Button { Text = "保存", Bounds = new Rectangle(120, 86, 88, 28), DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "取消", Bounds = new Rectangle(216, 86, 88, 28), DialogResult = DialogResult.Cancel };

        AcceptButton = saveButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[]
        {
            infoLabel, _allowCastCheckbox, saveButton, cancelButton,
        });
        ModernUi.StyleDialog(this);
    }
}
