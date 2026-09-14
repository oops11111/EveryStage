using EveryStage.Terminal.Devices;

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
    private readonly ListView _listView;
    private readonly Button _removeButton;

    public DevicesPanel(PairedDeviceStore pairedDevices)
    {
        _pairedDevices = pairedDevices;
        Dock = DockStyle.Fill;

        _listView = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
        _listView.Columns.Add("设备名", 160);
        _listView.Columns.Add("信任状态", 100);
        _listView.Columns.Add("允许被投放", 90);
        _listView.Columns.Add("允许被监看", 90);
        _listView.Columns.Add("配对时间", 140);
        _listView.SelectedIndexChanged += (_, _) => _removeButton.Enabled = _listView.SelectedItems.Count > 0;

        _removeButton = new Button { Text = "移除配对", Dock = DockStyle.Bottom, Height = 32, Enabled = false };
        _removeButton.Click += OnRemoveClick;

        Controls.Add(_listView);
        Controls.Add(_removeButton);

        Refresh_();
    }

    private void OnRemoveClick(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0 || _listView.SelectedItems[0].Tag is not PairedDevice device) return;

        var confirm = MessageBox.Show(this, $"确定要移除与 \"{device.DeviceName}\" 的配对吗？",
            "移除配对", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        _pairedDevices.Remove(device.DeviceId);
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
