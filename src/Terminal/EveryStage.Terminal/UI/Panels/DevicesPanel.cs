using EveryStage.Terminal.Devices;
using EveryStage.Terminal.Logging;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// PLANNING.md §8.2's 设备面板: "已配对设备列表（信任状态）+ 发现新设备区域". This implements the
/// first half — the paired-device list, with trust/permission columns and a remove button. The
/// "发现新设备" half isn't here: on the Terminal side there's nothing to actively discover (Casters
/// find the Terminal, not the other way around, per PLANNING.md §7's beacon direction) — pairing
/// requests already surface via <see cref="PairingConfirmationDialog"/> regardless of whether this
/// panel is even open, so a duplicate "incoming requests" list here would just be redundant UI for
/// the same underlying event.
/// </summary>
public sealed class DevicesPanel : UserControl
{
    private readonly PairedDeviceStore _pairedDevices;
    private readonly DeviceConnectionLogger _connectionLog;
    private readonly ListView _listView;
    private readonly Button _removeButton;
    private readonly Button _editPermissionsButton;

    public DevicesPanel(PairedDeviceStore pairedDevices, DeviceConnectionLogger connectionLog)
    {
        _pairedDevices = pairedDevices;
        _connectionLog = connectionLog;
        Dock = DockStyle.Fill;

        var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.Transparent };
        var title = new Label
        {
            Text = "已配对设备", AutoSize = true, Location = new Point(0, 0),
            Font = new Font("Segoe UI Semibold", 14F), ForeColor = ModernUi.Text,
        };
        var subtitle = new Label
        {
            Text = "配对请求会以弹窗提醒，确认后会出现在此列表中。",
            AutoSize = true, Location = new Point(0, 25),
            Font = new Font("Segoe UI", 8.5F), ForeColor = ModernUi.Muted,
        };
        header.Controls.AddRange(new Control[] { title, subtitle });

        var requestHint = new GlassPanel
        {
            Dock = DockStyle.Top, Height = 42, CornerRadius = 10,
            Padding = new Padding(12, 0, 12, 0), GlassTint = Color.FromArgb(125, 16, 47, 82),
        };
        var requestLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "⌁  新的配对请求会在这里对应显示，并由确认弹窗完成授权。",
            ForeColor = ModernUi.Muted, TextAlign = ContentAlignment.MiddleLeft,
        };
        requestHint.Controls.Add(requestLabel);

        _listView = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            OwnerDraw = true, BorderStyle = BorderStyle.None, GridLines = false,
            BackColor = ModernUi.Background, ForeColor = ModernUi.Text, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = new Font("Segoe UI", 9F),
        };
        _listView.Columns.Add("设备名", 160);
        _listView.Columns.Add("信任状态", 100);
        _listView.Columns.Add("允许被投放", 90);
        _listView.Columns.Add("允许被监看", 90);
        _listView.Columns.Add("配对时间", 140);
        _listView.DrawColumnHeader += (_, e) =>
        {
            using var background = new SolidBrush(ModernUi.SurfaceRaised);
            using var border = new Pen(ModernUi.Border);
            using var headerFont = new Font("Segoe UI Semibold", 9.5F);
            e.Graphics.FillRectangle(background, e.Bounds);
            e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty,
                headerFont, new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, e.Bounds.Width - 14, e.Bounds.Height),
                ModernUi.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        _listView.DrawItem += (_, e) => e.DrawDefault = false;
        _listView.DrawSubItem += (_, e) =>
        {
            Color backgroundColor = e.Item?.Selected == true ? Color.FromArgb(38, 72, 112)
                : e.ItemIndex % 2 == 0 ? ModernUi.Surface : Color.FromArgb(21, 42, 66);
            using var background = new SolidBrush(backgroundColor);
            e.Graphics.FillRectangle(background, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, _listView.Font,
                new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, e.Bounds.Width - 14, e.Bounds.Height),
                ModernUi.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        _listView.SelectedIndexChanged += (_, _) =>
        {
            _removeButton!.Enabled = _listView.SelectedItems.Count > 0;
            _editPermissionsButton!.Enabled = _listView.SelectedItems.Count > 0;
        };

        // Until now the only way to change a paired device's trust/permission flags after the
        // initial pairing dialog was to remove the pairing entirely and force a brand-new request —
        // see this project's README "已知风险" for why that was a real gap, not a deliberate
        // decision. A separate button from _removeButton rather than folding into it: removing a
        // pairing and editing its permissions are different operations with different consequences
        // (the former forgets the device, the latter doesn't), and conflating them into one button
        // would make one of the two harder to find. Both live in one explicitly-positioned bottom
        // panel (side by side) rather than each being its own DockStyle.Bottom control — this repo
        // otherwise favors absolute Bounds over stacking multiple same-edge-docked controls, whose
        // relative order depends on Controls collection order in a way that's easy to get backwards.
        _editPermissionsButton = new Button { Text = "编辑权限", Bounds = new Rectangle(8, 4, 140, 32), Enabled = false };
        ModernUi.StyleButton(_editPermissionsButton);
        _editPermissionsButton.Click += OnEditPermissionsClick;

        _removeButton = new Button { Text = "移除配对", Bounds = new Rectangle(156, 4, 140, 32), Enabled = false };
        ModernUi.StyleButton(_removeButton, danger: true);
        _removeButton.Click += OnRemoveClick;

        var buttonBar = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        buttonBar.Controls.AddRange(new Control[] { _editPermissionsButton, _removeButton });

        Controls.Add(_listView);
        Controls.Add(buttonBar);
        Controls.Add(requestHint);
        Controls.Add(header);

        Refresh_();
    }

    private void OnRemoveClick(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0 || _listView.SelectedItems[0].Tag is not PairedDevice device) return;

        var confirm = MessageBox.Show(this, $"确定要移除与 \"{device.DeviceName}\" 的配对吗？",
            "移除配对", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        _pairedDevices.Remove(device.DeviceId);
        // DeviceConnectionLogger.LogUnpaired's first real caller (PLANNING.md §14.4's "配对/取消
        // 配对" — LogPaired/LogConnected/LogDisconnected were already wired from DiscoveryService,
        // but nothing ever called this one) — see this project's README "已知风险".
        _connectionLog.LogUnpaired(device.DeviceId.ToString());
        Refresh_();
    }

    private void OnEditPermissionsClick(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0 || _listView.SelectedItems[0].Tag is not PairedDevice device) return;

        using var dialog = new EditPairedDevicePermissionsDialog(device);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // Mutate the same PairedDevice instance PairedDeviceStore.All already holds, then Upsert —
        // matches DiscoveryService.RespondToPairing's own use of Upsert for "this is the current
        // truth for this DeviceId, persist it" rather than a separate in-place-update method.
        device.TrustMode = dialog.TrustMode;
        device.AllowCast = dialog.AllowCast;
        device.AllowMonitor = dialog.AllowMonitor;
        _pairedDevices.Upsert(device);
        Refresh_();
    }

    /// <summary>Call after the paired-device list changes from outside this control (a new pairing
    /// accepted while this panel wasn't the active one, etc.).</summary>
    public void Refresh_()
    {
        _listView.Items.Clear();
        foreach (var device in _pairedDevices.All)
        {
            var item = new ListViewItem(device.DeviceName) { Tag = device };
            item.SubItems.Add(device.TrustMode == TrustMode.Trusted ? "信任" : "需手动确认");
            item.SubItems.Add(device.AllowCast ? "是" : "否");
            item.SubItems.Add(device.AllowMonitor ? "是" : "否");
            item.SubItems.Add(device.PairedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
            _listView.Items.Add(item);
        }
    }
}
