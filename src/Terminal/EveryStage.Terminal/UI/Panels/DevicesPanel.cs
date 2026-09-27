using System.Drawing.Drawing2D;
using EveryStage.Terminal.Devices;
using EveryStage.Terminal.Logging;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// PLANNING.md §8.2's 设备面板: "已配对设备列表（信任状态）+ 发现新设备区域". This implements the
/// paired-device list and a presentation area for pairing requests. Pairing-request display is
/// deliberately fed through this control's public methods so UI event wiring can be added without
/// changing the discovery or authorization flow.
///
/// Laid out in the design prototype's visual system (the reference mockups have no 设备 page): the
/// same page heading as the other pages, a 配对请求 card, and the paired devices as a card list where
/// each row carries its own 编辑权限 / 移除配对 actions — previously a five-column table whose two
/// buttons at the bottom only worked after selecting a row.
/// </summary>
public sealed class DevicesPanel : UserControl
{
    private readonly PairedDeviceStore _pairedDevices;
    private readonly DeviceConnectionLogger _connectionLog;
    private readonly FlowLayoutPanel _deviceRows;
    private readonly Label _deviceCount;
    private readonly Label _emptyState;
    private readonly GlassPanel _requestCard;
    private readonly Label _requestLabel;
    private PairingRequest? _displayedPairingRequest;

    public DevicesPanel(PairedDeviceStore pairedDevices, DeviceConnectionLogger connectionLog)
    {
        _pairedDevices = pairedDevices;
        _connectionLog = connectionLog;
        Dock = DockStyle.Fill;

        // --- 页头（与文件/活动/设置页同款）---
        var heading = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Padding = new Padding(4, 0, 0, 0) };
        heading.Controls.Add(new Label
        {
            Text = "管理已配对设备、信任状态与远程操作权限", Dock = DockStyle.Fill,
            ForeColor = ModernUi.Muted, Font = new Font("Segoe UI", 8.5F), TextAlign = ContentAlignment.MiddleLeft,
        });
        heading.Controls.Add(new Label
        {
            Text = "设备", Dock = DockStyle.Top, Height = 22, ForeColor = ModernUi.Text,
            Font = new Font("Segoe UI Semibold", 13F), TextAlign = ContentAlignment.MiddleLeft,
        });

        // --- 配对请求 ---
        var requestSection = new Panel { Dock = DockStyle.Top, Height = 104, Padding = new Padding(0, 6, 0, 10), BackColor = Color.Transparent };
        var requestTitle = new Label
        {
            Dock = DockStyle.Top, Height = 28, Text = "配对请求", TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 11F), ForeColor = ModernUi.Text,
        };
        _requestCard = new GlassPanel
        {
            Dock = DockStyle.Fill, CornerRadius = 12, Padding = new Padding(16, 0, 16, 0), GlassTint = ModernUi.Palette.GlassTint,
        };
        _requestLabel = new Label
        {
            Dock = DockStyle.Fill, Text = IdleRequestText,
            ForeColor = ModernUi.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        };
        _requestCard.Controls.Add(_requestLabel);
        requestSection.Controls.Add(_requestCard);
        requestSection.Controls.Add(requestTitle);

        // --- 已配对设备：卡片标题行 + 设备行列表 ---
        var listCard = new GlassPanel
        {
            Dock = DockStyle.Fill, CornerRadius = 12, Padding = new Padding(12, 6, 10, 10), GlassTint = ModernUi.Palette.GlassTint,
        };
        var listHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 40, WrapContents = false, Margin = Padding.Empty, BackColor = Color.Transparent,
        };
        listHeader.Controls.Add(new Label
        {
            Text = "已配对设备", AutoSize = true, ForeColor = ModernUi.Text,
            Font = new Font("Segoe UI Semibold", 11F), Margin = new Padding(0, 9, 8, 0),
        });
        _deviceCount = new Label { AutoSize = true, ForeColor = ModernUi.Muted, Margin = new Padding(0, 12, 0, 0) };
        listHeader.Controls.Add(_deviceCount);
        _deviceRows = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(0, 2, 0, 0), BackColor = Color.Transparent,
        };
        _deviceRows.Resize += (_, _) => LayoutRows();
        _emptyState = new Label
        {
            Text = "还没有已配对的设备\r\n其他设备发现本机并通过配对确认后，会出现在这里",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = ModernUi.Muted,
            BackColor = Color.Transparent, Visible = false,
        };
        listCard.Controls.Add(_deviceRows);
        listCard.Controls.Add(_emptyState);
        listCard.Controls.Add(listHeader);

        // Dock order: the last Top control added sits highest, so heading goes in last.
        Controls.Add(listCard);
        Controls.Add(requestSection);
        Controls.Add(heading);

        Refresh_();
    }

    private const string IdleRequestText = "○  暂无请求 · 其他设备请求配对时会显示在这里";

    /// <summary>Gives every row the list's width; the scrollbar's width is always reserved so its
    /// appearing can't change the width and re-trigger this.</summary>
    private void LayoutRows()
    {
        int width = Math.Max(LogicalToDeviceUnits(320),
            _deviceRows.Width - SystemInformation.VerticalScrollBarWidth - _deviceRows.Padding.Horizontal - 2);
        _deviceRows.SuspendLayout();
        foreach (Control row in _deviceRows.Controls) row.Size = new Size(width, LogicalToDeviceUnits(72));
        _deviceRows.ResumeLayout();
        // Same stale-AutoScroll-range issue as ActivitiesPanel.LayoutRows: lay out again after resizing.
        _deviceRows.PerformLayout();
    }

    /// <summary>Display a pairing request in the request area. Call on the UI thread.</summary>
    public void ShowPairingRequest(PairingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _displayedPairingRequest = request;
        _requestLabel.Text = $"●  {request.DeviceName}  ·  {request.RemoteAddress}  ·  等待确认";
        _requestLabel.ForeColor = ModernUi.Warning;
        _requestCard.GlassTint = ActivityRowView.Blend(ModernUi.Palette.GlassTint, ModernUi.Accent, 0.14f);
        _requestCard.Invalidate();
    }

    /// <summary>Clear the displayed request when it is resolved. A stale request cannot clear a newer one.</summary>
    public void ClearPairingRequest(string requestId)
    {
        if (_displayedPairingRequest?.RequestId != requestId) return;
        _displayedPairingRequest = null;
        _requestLabel.Text = IdleRequestText;
        _requestLabel.ForeColor = ModernUi.Muted;
        _requestCard.GlassTint = ModernUi.Palette.GlassTint;
        _requestCard.Invalidate();
    }

    // Until the permissions editor existed, the only way to change a paired device's trust/permission
    // flags after the initial pairing dialog was to remove the pairing entirely and force a brand-new
    // request — see this project's README "已知风险" for why that was a real gap, not a deliberate
    // decision. Editing and removing stay two separate actions (now on every row): removing a pairing
    // and editing its permissions are different operations with different consequences (the former
    // forgets the device, the latter doesn't), and conflating them would make one harder to find.

    private void RemoveDevice(PairedDevice device)
    {
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

    private void EditPermissions(PairedDevice device)
    {
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
        _deviceRows.SuspendLayout();
        var oldRows = _deviceRows.Controls.Cast<Control>().ToList();
        _deviceRows.Controls.Clear();
        foreach (var row in oldRows) row.Dispose();
        var devices = _pairedDevices.All.ToList();
        foreach (var device in devices)
            _deviceRows.Controls.Add(new DeviceRow(device, EditPermissions, RemoveDevice));
        _deviceRows.ResumeLayout(true);
        LayoutRows();

        _deviceCount.Text = $"共 {devices.Count} 台";
        _emptyState.Visible = devices.Count == 0;
        _deviceRows.Visible = devices.Count > 0;
    }

    /// <summary>
    /// One paired device, owner-drawn like the 活动 page's rows: device icon · name · trust status ·
    /// permission tags · pairing time, with 编辑权限 / 移除配对 on the right. Colours are read from
    /// <see cref="ModernUi"/> at paint time, so theme switches need no rebuild. The two actions are
    /// deferred with BeginInvoke because both end in <see cref="Refresh_"/>, which disposes this row.
    /// </summary>
    private sealed class DeviceRow : Control
    {
        private static readonly Font NameFont = new("Segoe UI Semibold", 10.5F);
        private static readonly Font BodyFont = new("Segoe UI", 9F);
        private static readonly Font TagFont = new("Segoe UI", 8.5F);

        private readonly PairedDevice _device;
        private readonly Action<PairedDevice> _edit;
        private readonly Action<PairedDevice> _remove;
        private Rectangle _editButton, _removeButton;
        private Point _hover = new(-1, -1);

        public DeviceRow(PairedDevice device, Action<PairedDevice> edit, Action<PairedDevice> remove)
        {
            _device = device;
            _edit = edit;
            _remove = remove;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            Margin = new Padding(0, 0, 0, 10);
            TabStop = true;
            AccessibleName = device.DeviceName;
            // The table this replaced was keyboard-reachable; keep that: Tab to a row, then Enter/Delete.
            AccessibleDescription = "Enter 编辑权限，Delete 移除配对";
        }

        private int S(int value) => LogicalToDeviceUnits(value);

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Enter or Keys.Delete || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Modifiers != Keys.None || Parent is not { } owner) return;
            if (e.KeyCode == Keys.Enter) owner.BeginInvoke(() => _edit(_device));
            else if (e.KeyCode == Keys.Delete) owner.BeginInvoke(() => _remove(_device));
            else return;
            e.Handled = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _hover = e.Location;
            Cursor = _editButton.Contains(e.Location) || _removeButton.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = new Point(-1, -1);
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            var owner = Parent;
            if (_editButton.Contains(e.Location)) owner?.BeginInvoke(() => _edit(_device));
            else if (_removeButton.Contains(e.Location)) owner?.BeginInvoke(() => _remove(_device));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = Width, h = Height, cy = h / 2;

            using (var path = ActivityRowView.RoundedBox(new Rectangle(0, 0, w - 1, h - 1), S(12)))
            {
                using var fill = new SolidBrush(ModernUi.Background);
                g.FillPath(fill, path);
                using var border = new Pen(ModernUi.Border);
                g.DrawPath(border, path);
            }

            // Device icon tile.
            var tile = new Rectangle(S(14), cy - S(22), S(44), S(44));
            using (var path = ActivityRowView.RoundedBox(tile, S(10)))
            using (var fill = new SolidBrush(ActivityRowView.Blend(ModernUi.Background, ModernUi.Text, 0.06f)))
                g.FillPath(fill, path);
            VectorIcons.Draw(g, NavIcon.Devices, Rectangle.Inflate(tile, -S(11), -S(11)), ModernUi.Muted);

            // Actions on the right: 移除配对 (danger) right-most, 编辑权限 to its left.
            _removeButton = new Rectangle(w - S(14) - S(92), cy - S(16), S(92), S(32));
            _editButton = new Rectangle(_removeButton.Left - S(8) - S(92), cy - S(16), S(92), S(32));
            DrawButton(g, _editButton, "编辑权限", danger: false);
            DrawButton(g, _removeButton, "移除配对", danger: true);

            // Name, then status line: trust dot + permission tags + pairing time.
            int textLeft = tile.Right + S(14), textRight = _editButton.Left - S(12);
            TextRenderer.DrawText(g, _device.DeviceName, NameFont,
                Rectangle.FromLTRB(textLeft, cy - S(24), textRight, cy - S(2)), ModernUi.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            bool trusted = _device.TrustMode == TrustMode.Trusted;
            int x = textLeft, lineTop = cy + S(2), lineHeight = S(22);
            var dot = new Rectangle(x, lineTop + (lineHeight - S(8)) / 2, S(8), S(8));
            using (var dotBrush = new SolidBrush(trusted ? ModernUi.Success : ModernUi.Warning)) g.FillEllipse(dotBrush, dot);
            x = dot.Right + S(6);
            string trust = trusted ? "信任" : "需手动确认";
            int trustWidth = TextRenderer.MeasureText(trust, BodyFont).Width;
            TextRenderer.DrawText(g, trust, BodyFont, new Rectangle(x, lineTop, trustWidth, lineHeight),
                trusted ? ModernUi.Success : ModernUi.Warning, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            x += trustWidth + S(8);

            foreach (var (text, on) in new[] { ("可投放", _device.AllowCast), ("可监看", _device.AllowMonitor) })
            {
                string label = on ? text : "不" + text;
                int tagWidth = TextRenderer.MeasureText(label, TagFont).Width + S(14);
                if (x + tagWidth > textRight) break;
                var tag = new Rectangle(x, lineTop + S(1), tagWidth, lineHeight - S(2));
                using (var path = ActivityRowView.RoundedBox(tag, S(6)))
                {
                    using var fill = new SolidBrush(on ? ActivityRowView.Blend(ModernUi.Background, ModernUi.Accent, 0.16f)
                        : ActivityRowView.Blend(ModernUi.Background, ModernUi.Text, 0.05f));
                    g.FillPath(fill, path);
                }
                TextRenderer.DrawText(g, label, TagFont, tag, on ? ModernUi.Text : ModernUi.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                x += tagWidth + S(6);
            }

            string paired = $"配对于 {_device.PairedAt.LocalDateTime:yyyy-MM-dd HH:mm}";
            if (x + S(8) + TextRenderer.MeasureText(paired, BodyFont).Width <= textRight)
                TextRenderer.DrawText(g, paired, BodyFont, Rectangle.FromLTRB(x + S(4), lineTop, textRight, lineTop + lineHeight),
                    ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -3, -3), ModernUi.Text, ModernUi.Background);
        }

        private void DrawButton(Graphics g, Rectangle r, string text, bool danger)
        {
            bool hot = r.Contains(_hover);
            using var path = ActivityRowView.RoundedBox(r, S(8));
            using (var fill = new SolidBrush(hot
                ? ActivityRowView.Blend(ModernUi.Surface, danger ? ModernUi.Danger : ModernUi.Text, danger ? 0.18f : 0.08f)
                : ModernUi.Surface))
                g.FillPath(fill, path);
            using (var pen = new Pen(danger ? ModernUi.Danger : ModernUi.Border)) g.DrawPath(pen, path);
            TextRenderer.DrawText(g, text, BodyFont, r, danger && hot ? ModernUi.Danger : ModernUi.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
