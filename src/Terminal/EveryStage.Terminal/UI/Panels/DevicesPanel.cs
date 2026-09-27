using EveryStage.Terminal.Devices;
using EveryStage.Terminal.Logging;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// PLANNING.md §8.2's 设备面板: "已配对设备列表（信任状态）+ 发现新设备区域". This implements the
/// paired-device list and a presentation area for pairing requests. Pairing-request display is
/// deliberately fed through this control's public methods so UI event wiring can be added without
/// changing the discovery or authorization flow.
/// </summary>
public sealed class DevicesPanel : UserControl
{
    private readonly PairedDeviceStore _pairedDevices;
    private readonly DeviceConnectionLogger _connectionLog;
    private readonly ListView _listView;
    private readonly Button _removeButton;
    private readonly Button _editPermissionsButton;
    private readonly Label _requestLabel;
    private PairingRequest? _displayedPairingRequest;

    public DevicesPanel(PairedDeviceStore pairedDevices, DeviceConnectionLogger connectionLog)
    {
        _pairedDevices = pairedDevices;
        _connectionLog = connectionLog;
        Dock = DockStyle.Fill;

        var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.Transparent };
        var title = new Label
        {
            Text = "已配对设备", AutoSize = true, Location = new Point(0, 0),
            Font = new Font("Segoe UI Semibold", 14F), ForeColor = ModernUi.Text,
        };
        var subtitle = new Label
        {
            Text = "管理已配对设备、信任状态与远程操作权限。",
            AutoSize = true, Location = new Point(0, 27),
            Font = new Font("Segoe UI", 8.5F), ForeColor = ModernUi.Muted,
        };
        header.Controls.AddRange(new Control[] { title, subtitle });

        var requestSection = new Panel
        {
            Dock = DockStyle.Top, Height = 104, Padding = new Padding(0, 8, 0, 8),
            BackColor = Color.Transparent,
        };
        var requestTitle = new Label
        {
            Dock = DockStyle.Top, Height = 26, Text = "配对请求",
            Font = new Font("Segoe UI Semibold", 10F), ForeColor = ModernUi.Text,
        };
        var requestCard = new GlassPanel
        {
            Dock = DockStyle.Fill, CornerRadius = 10,
            Padding = new Padding(12, 0, 12, 0), GlassTint = ModernUi.Surface,
        };
        _requestLabel = new Label
        {
            Dock = DockStyle.Fill, Text = "暂无请求",
            ForeColor = ModernUi.Muted, TextAlign = ContentAlignment.MiddleLeft,
        };
        requestCard.Controls.Add(_requestLabel);
        requestSection.Controls.Add(requestCard);
        requestSection.Controls.Add(requestTitle);

        _listView = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            OwnerDraw = true, BorderStyle = BorderStyle.None, GridLines = false,
            BackColor = ModernUi.Background, ForeColor = ModernUi.Text, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = new Font("Segoe UI", 9F),
            SmallImageList = new ImageList { ImageSize = new Size(1, 42), ColorDepth = ColorDepth.Depth32Bit },
        };
        _listView.SmallImageList.Images.Add(new Bitmap(1, 42));
        _listView.Columns.Add("设备名", 210);
        _listView.Columns.Add("信任状态", 140);
        _listView.Columns.Add("允许被投放", 140);
        _listView.Columns.Add("允许被监看", 140);
        _listView.Columns.Add("配对时间", 180);
        _listView.SizeChanged += (_, _) => ResizeColumns();
        _listView.DrawItem += (_, e) =>
        {
            var rowBounds = new Rectangle(e.Bounds.Left, e.Bounds.Top, e.Bounds.Width, 42);
            Color backgroundColor = e.Item?.Selected == true ? ModernUi.Accent
                : e.ItemIndex % 2 == 0 ? ModernUi.Surface : ModernUi.SurfaceRaised;
            using var background = new SolidBrush(backgroundColor);
            e.Graphics.FillRectangle(background, rowBounds);
        };
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
            var rowBounds = new Rectangle(e.Bounds.Left, e.Bounds.Top, e.Bounds.Width, 42);
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, _listView.Font,
                new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, e.Bounds.Width - 14, rowBounds.Height),
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
        Controls.Add(requestSection);
        Controls.Add(header);

        Refresh_();
    }

    private void ResizeColumns()
    {
        if (_listView.Columns.Count != 5 || _listView.ClientSize.Width < 5) return;
        int available = Math.Max(430, _listView.ClientSize.Width - 4);
        int[] weights = [24, 17, 18, 18, 23];
        int used = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            int width = i == weights.Length - 1 ? available - used : available * weights[i] / 100;
            width = Math.Max(i == 0 ? 105 : 90, width);
            _listView.Columns[i].Width = width;
            used += width;
        }
    }

    /// <summary>Display a pairing request in the request area. Call on the UI thread.</summary>
    public void ShowPairingRequest(PairingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _displayedPairingRequest = request;
        _requestLabel.Text = $"{request.DeviceName}  ·  {request.RemoteAddress}  ·  等待确认";
        _requestLabel.ForeColor = ModernUi.Text;
    }

    /// <summary>Clear the displayed request when it is resolved. A stale request cannot clear a newer one.</summary>
    public void ClearPairingRequest(string requestId)
    {
        if (_displayedPairingRequest?.RequestId != requestId) return;
        _displayedPairingRequest = null;
        _requestLabel.Text = "暂无请求";
        _requestLabel.ForeColor = ModernUi.Muted;
    }

    private void OnRemoveClick(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0 || _listView.SelectedItems[0].Tag is not PairedDevice device) return;

        var confirm = MessageBox.Show(this, $"确定要移除与 \"{device.DeviceName}\" 的配对吗？",
            "移除配对", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        try { _pairedDevices.Remove(device.DeviceId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"移除未保存：{ex.Message}", "配对记录保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
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
        var oldTrust = device.TrustMode;
        var oldCast = device.AllowCast;
        var oldMonitor = device.AllowMonitor;
        device.TrustMode = dialog.TrustMode;
        device.AllowCast = dialog.AllowCast;
        device.AllowMonitor = dialog.AllowMonitor;
        // Roll the in-memory edit back and surface the failure if the save doesn't land, rather than
        // showing the edited flags while disk keeps the old ones with no error (audit C-25).
        try { _pairedDevices.Upsert(device); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            device.TrustMode = oldTrust;
            device.AllowCast = oldCast;
            device.AllowMonitor = oldMonitor;
            MessageBox.Show(this, $"权限未保存：{ex.Message}", "配对记录保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
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
