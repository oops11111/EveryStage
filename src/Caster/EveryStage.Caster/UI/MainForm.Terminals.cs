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
    /// marshal to the UI thread before touching the list. Guards against a
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

    // P-04: see the signature check in RefreshTerminalList.
    private string _terminalListSignature = "";
    private bool _refreshingList;

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

        // P-04: this runs on a 1-second timer. Rebuilding unconditionally (Clear + re-Add)
        // repainted every card and churned the selection once a second even when nothing on
        // the LAN had changed. The signature covers exactly what a card displays — identity,
        // name, online/offline — so an unchanged signature means the existing items already
        // show the right thing; keep them (and the user's selection) untouched.
        string signature = string.Join("\n", entries.Select(e => $"{e.DeviceId}|{e.DeviceName}|{(e.Live != null ? 1 : 0)}|{(e.PairedAt != null ? 1 : 0)}|{e.Live?.Address}"));
        if (signature == _terminalListSignature) return;
        _terminalListSignature = signature;

        _refreshingList = true;
        try
        {
            _terminalListBox.BeginUpdate();
            _terminalListBox.Items.Clear();
            foreach (var entry in entries) _terminalListBox.Items.Add(entry);
            if (selected != null)
            {
                var stillPresent = entries.FirstOrDefault(e => e.DeviceId == selected.DeviceId);
                if (stillPresent != null) _terminalListBox.SelectedItem = stillPresent;
            }
            // Nothing chosen yet and exactly one castable terminal: choose it, saving a click.
            if (_terminalListBox.SelectedItem == null && selected == null && entries.Count(e => e.Live != null) == 1)
                _terminalListBox.SelectedItem = entries.First(e => e.Live != null);
            _terminalListBox.EndUpdate();
        }
        finally
        {
            _refreshingList = false;
        }

        // Keep the page in step with the list (a terminal coming online, going offline, or vanishing).
        if (_currentView == _standbyView || _currentView == _emptyView || _currentView == null)
        {
            if (!_starting) ShowSelectedTerminal();
        }
    }

    /// <summary>Displays plugged in or removed: re-list them (the chosen one is kept by device name,
    /// since output indices shift).</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => { if (!IsDisposed) RefreshMonitorList(); })); }
        catch (InvalidOperationException) { } // handle destroyed meanwhile (closing)
    }

    private void RefreshMonitorList()
    {
        IReadOnlyList<EveryStage.Caster.Capture.ScreenCaptureSource.MonitorCaptureOption> outputs;
        try
        {
            using var gpu = new EveryStage.Rendering.D3D11Device();
            outputs = EveryStage.Caster.Capture.ScreenCaptureSource.EnumerateOutputs(gpu);
        }
        catch (Exception)
        {
            // If even a throwaway device fails, 开始投屏 will fail the same way and say why there.
            _standbyView.SetMonitors(new[] { new MonitorChoice(0, "默认屏幕", "1", null) });
            return;
        }

        var choices = new List<MonitorChoice>();
        int number = 0;
        foreach (var output in outputs)
        {
            number++;
            bool primary = output.Bounds.X == 0 && output.Bounds.Y == 0;
            string label = $"{(primary ? "主屏" : $"屏幕 {number}")} {output.Bounds.Width}×{output.Bounds.Height}";
            choices.Add(new MonitorChoice(output.OutputIndex, label, number.ToString(), output.DeviceName));
        }
        if (choices.Count == 0) choices.Add(new MonitorChoice(0, "默认屏幕", "1", null));
        // The primary screen first: it is what most people mean.
        choices = choices.OrderByDescending(c => c.Label.StartsWith("主屏", StringComparison.Ordinal)).ToList();
        _standbyView.SetMonitors(choices);
    }

    private void OnRemovePairingClick()
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
        _terminalListSignature = "";
        RefreshTerminalList();
        ShowSelectedTerminal();
    }
}
