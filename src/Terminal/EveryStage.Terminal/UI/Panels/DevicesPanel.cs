using EveryStage.Terminal.Data;
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
    private readonly RequestCard _requestCard;
    private readonly ToggleSwitch? _castSwitch;
    private readonly EveryStage.Terminal.StateMachine.OutputStateMachine? _stateMachine;
    private PairingRequest? _displayedPairingRequest;

    public DevicesPanel(PairedDeviceStore pairedDevices, DeviceConnectionLogger connectionLog,
        string? localName = null, EveryStage.Terminal.StateMachine.OutputStateMachine? stateMachine = null)
    {
        _pairedDevices = pairedDevices;
        _connectionLog = connectionLog;
        _stateMachine = stateMachine;
        Dock = DockStyle.Fill;

        // --- 页头：设备 · 本机：名称 · ● 可被局域网发现 …… 投屏开关 ---
        var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.Transparent };
        var title = new Label { Text = "设备", AutoSize = true, ForeColor = ModernUi.Text, Font = new Font(ModernUi.UiFontSemibold, 15F), BackColor = Color.Transparent };
        var local = new Label { Text = localName == null ? "" : $"本机：{localName}", AutoSize = true, ForeColor = ModernUi.Muted, BackColor = Color.Transparent };
        var discoverable = new Label { Text = "● 可被局域网发现", AutoSize = true, ForeColor = ModernUi.Success, BackColor = Color.Transparent, Font = new Font(ModernUi.UiFont, 9F) };
        header.Controls.AddRange(new Control[] { title, local, discoverable });
        Label? switchLabel = null;
        if (stateMachine != null)
        {
            _castSwitch = new ToggleSwitch { Checked = stateMachine.CastSwitchOn };
            switchLabel = new Label { Text = "投屏开关", AutoSize = true, ForeColor = ModernUi.Text, BackColor = Color.Transparent };
            _castSwitch.CheckedChanged += (_, _) => { if (stateMachine.CastSwitchOn != _castSwitch.Checked) stateMachine.SetCastSwitch(_castSwitch.Checked); };
            stateMachine.CastSwitchChanged += OnCastSwitchChanged;
            header.Controls.Add(_castSwitch);
            header.Controls.Add(switchLabel);
        }
        header.Resize += (_, _) =>
        {
            int cy = (header.ClientSize.Height - 6) / 2;
            title.Location = new Point(0, cy - title.Height / 2);
            local.Location = new Point(title.Right + 16, cy - local.Height / 2 + 2);
            discoverable.Location = new Point(local.Right + 12, cy - discoverable.Height / 2 + 2);
            if (_castSwitch != null && switchLabel != null)
            {
                _castSwitch.Location = new Point(header.ClientSize.Width - _castSwitch.Width, cy - _castSwitch.Height / 2);
                switchLabel.Location = new Point(_castSwitch.Left - 8 - switchLabel.Width, cy - switchLabel.Height / 2);
            }
        };

        // --- 配对请求卡片（有请求时放大显示配对码）---
        _requestCard = new RequestCard { Dock = DockStyle.Top, Height = 48 };
        var requestGap = new Panel { Dock = DockStyle.Top, Height = 16, BackColor = Color.Transparent };

        // --- 已配对设备：标题行 + 设备行 ---
        var listHeader = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.Transparent };
        var listTitle = new Label
        {
            Text = "已配对设备", AutoSize = true, ForeColor = ModernUi.Text, BackColor = Color.Transparent,
            Font = new Font(ModernUi.UiFontSemibold, 11.5F), Location = new Point(0, 6),
        };
        _deviceCount = new Label { AutoSize = true, ForeColor = ModernUi.Muted, BackColor = Color.Transparent };
        listHeader.Controls.Add(listTitle);
        listHeader.Controls.Add(_deviceCount);
        listHeader.Layout += (_, _) => _deviceCount.Location = new Point(listTitle.Right + 8, listTitle.Top + 4);
        _deviceRows = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(0, 2, 0, 0), BackColor = Color.Transparent,
        };
        _deviceRows.Resize += (_, _) => LayoutRows();
        _emptyState = new Label
        {
            Text = "还没有已配对的设备\r\n投屏机发现本机并通过配对确认后，会出现在这里",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = ModernUi.Muted,
            BackColor = Color.Transparent, Visible = false,
        };

        // Dock order: the last Top control added sits highest, so the header goes in last.
        Controls.Add(_deviceRows);
        Controls.Add(_emptyState);
        Controls.Add(listHeader);
        Controls.Add(requestGap);
        Controls.Add(_requestCard);
        Controls.Add(header);

        Refresh_();
    }

    private void OnCastSwitchChanged(bool on)
    {
        if (_castSwitch == null || IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnCastSwitchChanged(on)); return; }
        _castSwitch.Checked = on;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _stateMachine != null) _stateMachine.CastSwitchChanged -= OnCastSwitchChanged;
        base.Dispose(disposing);
    }

    /// <summary>Gives every row the list's width; the scrollbar's width is always reserved so its
    /// appearing can't change the width and re-trigger this.</summary>
    private void LayoutRows()
    {
        int width = Math.Max(LogicalToDeviceUnits(320),
            _deviceRows.Width - SystemInformation.VerticalScrollBarWidth - _deviceRows.Padding.Horizontal - 2);
        _deviceRows.SuspendLayout();
        foreach (Control row in _deviceRows.Controls) row.Size = new Size(width, LogicalToDeviceUnits(60));
        _deviceRows.ResumeLayout();
        // Same stale-AutoScroll-range issue as ActivitiesPanel.LayoutRows: lay out again after resizing.
        _deviceRows.PerformLayout();
    }

    /// <summary>Display a pairing request in the request area. Call on the UI thread.</summary>
    public void ShowPairingRequest(PairingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _displayedPairingRequest = request;
        string code = request.VerificationCode.Length == 6 ? request.VerificationCode.Insert(3, " ") : request.VerificationCode;
        _requestCard.Show(request.DeviceName, code);
        _requestCard.Height = LogicalToDeviceUnits(96);
    }

    /// <summary>Clear the displayed request when it is resolved. A stale request cannot clear a newer one.</summary>
    public void ClearPairingRequest(string requestId)
    {
        if (_displayedPairingRequest?.RequestId != requestId) return;
        _displayedPairingRequest = null;
        _requestCard.Show(null, null);
        _requestCard.Height = LogicalToDeviceUnits(48);
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
            MessageBox.Show(this, $"移除未保存：{DataDirectory.Describe(ex)}", "配对记录保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            MessageBox.Show(this, $"权限未保存：{DataDirectory.Describe(ex)}", "配对记录保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        _deviceCount.Text = $"{devices.Count} 台";
        _emptyState.Visible = devices.Count == 0;
        _deviceRows.Visible = devices.Count > 0;
    }

    /// <summary>The 配对请求 card: an idle one-liner, or (while a request waits) the device name and the
    /// big verification code to compare with the Caster. Accepting / refusing stays in the
    /// confirmation dialog the Terminal opens for every request.</summary>
    private sealed class RequestCard : Control
    {
        private string? _device;
        private string? _code;

        public RequestCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public void Show(string? device, string? code)
        {
            _device = device;
            _code = code;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int S(int v) => LogicalToDeviceUnits(v);
            bool active = _device != null;
            using (var path = ActivityRowView.RoundedBox(new Rectangle(0, 0, Width - 1, Height - 1), S(10)))
            {
                using var fill = new SolidBrush(active ? ModernUi.Mix(ModernUi.Background, ModernUi.Accent, 0.14f) : ModernUi.Surface);
                g.FillPath(fill, path);
                using var pen = new Pen(active ? ModernUi.Accent : ModernUi.Mix(ModernUi.Surface, ModernUi.Border, 0.45f));
                g.DrawPath(pen, path);
            }
            using var body = new Font(ModernUi.UiFont, 9.5F);
            if (!active)
            {
                TextRenderer.DrawText(g, "○  暂无配对请求 · 投屏机请求配对时会显示在这里", body, new Rectangle(S(18), 0, Width - S(36), Height),
                    ModernUi.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }
            using var small = new Font(ModernUi.UiFontSemibold, 8.5F);
            using var name = new Font(ModernUi.UiFontSemibold, 13F);
            using var codeFont = new Font(ModernUi.UiFontSemibold, 24F);
            TextRenderer.DrawText(g, "新的配对请求", small, new Point(S(20), S(18)), ModernUi.AccentText);
            TextRenderer.DrawText(g, _device, name, new Rectangle(S(20), S(38), Width / 2 - S(20), S(28)), ModernUi.Text,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "请在弹出的确认窗口中接受或拒绝", body, new Point(S(20), S(66)), ModernUi.Muted);
            var codeSize = TextRenderer.MeasureText(_code, codeFont);
            int codeX = Math.Max(Width / 2, Width - S(24) - codeSize.Width);
            TextRenderer.DrawText(g, "核对投屏机上显示的数字", body, new Point(codeX, S(14)), ModernUi.Muted);
            TextRenderer.DrawText(g, _code, codeFont, new Point(codeX, S(34)), ModernUi.AccentText);
        }
    }

    /// <summary>
    /// One paired device, owner-drawn like the 活动 page's rows: device icon · name · trust status ·
    /// permission tags · pairing time, with 编辑权限 / 移除配对 on the right. Colours are read from
    /// <see cref="ModernUi"/> at paint time, so theme switches need no rebuild. The two actions are
    /// deferred with BeginInvoke because both end in <see cref="Refresh_"/>, which disposes this row.
    /// </summary>
    private sealed class DeviceRow : Control
    {
        private static readonly Font NameFont = new(ModernUi.UiFontSemibold, 10.5F);
        private static readonly Font BodyFont = new(ModernUi.UiFont, 9F);
        private static readonly Font TagFont = new(ModernUi.UiFont, 8.5F);

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
            Margin = new Padding(0, 0, 0, 6);
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

            using (var path = ActivityRowView.RoundedBox(new Rectangle(0, 0, w - 1, h - 1), S(8)))
            {
                using var fill = new SolidBrush(ModernUi.Surface);
                g.FillPath(fill, path);
                using var border = new Pen(ModernUi.Mix(ModernUi.Surface, ModernUi.Border, 0.45f));
                g.DrawPath(border, path);
            }

            // Device icon tile.
            var tile = new Rectangle(S(12), cy - S(18), S(36), S(36));
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
