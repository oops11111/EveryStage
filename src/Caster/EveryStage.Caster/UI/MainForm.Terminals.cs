using System.Drawing.Drawing2D;
using EveryStage.Caster.Capture;
using EveryStage.Caster.Casting;
using EveryStage.Caster.Discovery;
using EveryStage.Caster.Encode;
using EveryStage.Discovery;
using EveryStage.Rendering;
using EveryStage.Transport;

namespace EveryStage.Caster.UI;

public sealed partial class MainForm
{
    /// <summary>Raised from <see cref="TerminalDiscoveryClient"/>'s background receive loop —
    /// marshal to the UI thread before touching <see cref="_terminalListBox"/>. Guards against a
    /// beacon racing ahead of this form's own window handle: <c>Program.cs</c> calls
    /// <c>discoveryClient.Start()</c> before constructing <see cref="MainForm"/> at all, so in
    /// principle a beacon could arrive before this form has a handle to <see cref="Control.BeginInvoke(Delegate)"/>
    /// onto — vanishingly unlikely on a real LAN and impossible to verify timing for in this sandbox,
    /// but <see cref="_listRefreshTimer"/>'s own poll covers this exact update a moment later
    /// regardless, so silently skipping here rather than risking an exception is the safe choice.</summary>
    private void OnTerminalListChanged()
    {
        if (!IsHandleCreated) return;
        // Explicit new Action(...) rather than a bare method group: Control.BeginInvoke(Delegate)
        // takes the abstract Delegate base type, which a method group can't convert to directly —
        // same wrapping this repo's Terminal side already uses for the same reason
        // (OverlayWindow.BeginInvoke(new Action(...)) in PlaybackEngine.cs).
        BeginInvoke(new Action(RefreshTerminalList));
    }

    private void RefreshTerminalList()
    {
        // Preserve selection across refreshes by DeviceId rather than list index, since the list is
        // re-sorted each time and an entry could shuffle position (or flip online/offline) as
        // beacons come and go.
        var selected = _terminalListBox.SelectedItem as TerminalListEntry;
        var liveTerminals = _discoveryClient.GetTerminals(); // already ordered by name.

        // Online entries first (these are the ones actually actionable), each paired-but-currently-
        // offline entry appended after — see TerminalListEntry's doc comment on why an offline entry
        // can't just reuse a cached live one. A terminal that both paired before and is currently
        // beaconing shows up once, as its online entry (the Where below excludes it from the offline
        // half), not twice.
        var liveIds = liveTerminals.Select(t => t.DeviceId).ToHashSet();
        var entries = liveTerminals
            .Select(t => new TerminalListEntry(t.DeviceId, t.DeviceName, t, _pairedTerminals.Find(t.DeviceId)?.PairedAt))
            .Concat(_pairedTerminals.All
                .Where(p => !liveIds.Contains(p.DeviceId))
                .OrderBy(p => p.DeviceName)
                .Select(p => new TerminalListEntry(p.DeviceId, p.DeviceName, null, p.PairedAt)))
            .ToList();

        _terminalListBox.BeginUpdate();
        _terminalListBox.Items.Clear();
        foreach (var entry in entries) _terminalListBox.Items.Add(entry);
        if (selected != null)
        {
            var stillPresent = entries.FirstOrDefault(e => e.DeviceId == selected.DeviceId);
            if (stillPresent != null) _terminalListBox.SelectedItem = stillPresent;
        }
        _terminalListBox.EndUpdate();
    }

    /// <summary>Re-enumerates <see cref="ScreenCaptureSource.EnumerateOutputs"/> into
    /// <see cref="_monitorComboBox"/>, preserving the current selection by
    /// <see cref="MonitorComboItem.OutputIndex"/> (not list position) if that output still exists —
    /// same reasoning <c>SettingsPanel.Refresh_()</c> on the Terminal side just applied to its own
    /// monitor list: re-populating around the last-saved/last-picked value would silently reset an
    /// in-progress choice, and a monitor that's genuinely gone should fall back rather than stay
    /// selected. Called once at construction and again from <see cref="ShowStandby"/>, not just
    /// once ever — the exact staleness mistake this project's README already flagged and fixed for
    /// the Terminal's own monitor picker.</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => { if (!IsDisposed) RefreshMonitorList(); })); }
        catch (InvalidOperationException) { } // handle destroyed meanwhile (closing)
    }

    private void RefreshMonitorList()
    {
        // Restored by device name: output indices shift when a display is removed (audit A-13).
        string? previousDevice = (_monitorComboBox.SelectedItem as MonitorComboItem)?.DeviceName;
        int? previousSelection = (_monitorComboBox.SelectedItem as MonitorComboItem)?.OutputIndex;

        IReadOnlyList<ScreenCaptureSource.MonitorCaptureOption> outputs;
        try
        {
            using var gpu = new D3D11Device();
            outputs = ScreenCaptureSource.EnumerateOutputs(gpu);
        }
        catch (Exception ex)
        {
            // Same "don't let a diagnostic/setup step take the whole form down" reasoning the self-
            // test runners already apply to their own D3D11Device construction — if even a
            // throwaway device can't be created here, "开始投屏" is going to fail the identical way,
            // so there's nothing extra to gain from surfacing a separate error dialog right now.
            _monitorComboBox.Items.Clear();
            _monitorComboBox.Items.Add(new MonitorComboItem(0, $"默认显示器（枚举失败：{ex.Message}）"));
            _monitorComboBox.SelectedIndex = 0;
            return;
        }

        _monitorComboBox.BeginUpdate();
        _monitorComboBox.Items.Clear();
        foreach (var output in outputs)
        {
            string label = $"显示器 {output.OutputIndex}: {output.DeviceName} " +
                           $"({output.Bounds.Width}x{output.Bounds.Height} @ {output.Bounds.X},{output.Bounds.Y})";
            _monitorComboBox.Items.Add(new MonitorComboItem(output.OutputIndex, label, output.DeviceName));
        }

        if (_monitorComboBox.Items.Count == 0)
        {
            // Shouldn't happen on any real machine (there's always at least one display) but a
            // combo box with nothing selectable would leave "开始投屏" silently falling back to
            // ScreenCaptureSource's own outputIndex default without the UI ever showing that choice.
            _monitorComboBox.Items.Add(new MonitorComboItem(0, "默认显示器"));
        }

        int selectedIndex = 0;
        for (int i = 0; i < _monitorComboBox.Items.Count; i++)
        {
            var item = (MonitorComboItem)_monitorComboBox.Items[i]!;
            if (previousDevice != null ? item.DeviceName == previousDevice : item.OutputIndex == previousSelection)
            {
                selectedIndex = i;
                break;
            }
        }
        _monitorComboBox.SelectedIndex = selectedIndex;
        _monitorComboBox.EndUpdate();
    }

    private void OnRemovePairingClick(object? sender, EventArgs e)
    {
        if (_terminalListBox.SelectedItem is not TerminalListEntry entry) return;

        var confirm = MessageBox.Show(this,
            $"确定要移除与 \"{entry.DeviceName}\" 的配对记录吗？\n" +
            (entry.Live != null
                ? "这不会立即影响它当前的在线状态（还是能看到它，因为它仍在广播）——只是它下次\n离线后不会再出现在这个列表里，除非重新配对一次。"
                : "这个终端机会从待机列表里彻底消失，直到它重新广播并再次配对成功。"),
            "移除配对", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        _pairedTerminals.Remove(entry.DeviceId);
        RefreshTerminalList();
    }
}
