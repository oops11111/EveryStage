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
    private async void OnStartButtonClick(object? sender, EventArgs e)
    {
        // The offline half of TerminalListEntry.Live == null can't reach here in practice (the
        // SelectedIndexChanged handler above keeps _startButton disabled for it), but the pattern
        // match still guards against it directly rather than trusting that invariant blindly.
        if (_terminalListBox.SelectedItem is not TerminalListEntry { Live: not null } entry) return;
        var terminal = entry.Live!; // non-null per the pattern match above.
        if (terminal.MediaAuthenticationVersion != 1)
        {
            MessageBox.Show(this, "终端机与投屏机的媒体协议版本不兼容，请将两端更新到同一版本后重试。",
                "需要更新程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (terminal.PairingProtocolVersion != PairingHandshake.ProtocolVersion)
        {
            MessageBox.Show(this, "终端机程序版本过旧，需要更新程序后才能安全配对。",
                "需要更新程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Captured now rather than read again inside ShowPaired — the combo box only ever refreshes
        // from ShowStandby()/construction (not on a timer, unlike _terminalListBox), so there's no
        // real race to guard against here, but reading it once at the point of the user's actual
        // click is still the clearer place to fix "what they asked for" for this cast.
        int outputIndex = (_monitorComboBox.SelectedItem as MonitorComboItem)?.OutputIndex ?? 0;

        _startButton.Enabled = false;
        _startButton.Text = "配对中...";
        try
        {
            var existing = _pairedTerminals.Find(terminal.DeviceId);
            if (existing != null && PairingSecurity.IsSupportedKey(existing.PairingKey, existing.PairingKeyFormatVersion))
            {
                await ShowPairedAsync(terminal, outputIndex, existing.PairingKey!, existing.PairingKeyFormatVersion);
                return;
            }

            await RequestPairingAndShowAsync(terminal, outputIndex);
        }
        finally
        {
            // Re-checks Live (not just non-null) rather than reusing the `entry` this method
            // started with: RefreshTerminalList() runs on its own 1s timer independently of this
            // await, and could have rebuilt the list — replacing the selected item with a fresh
            // TerminalListEntry instance — while a pairing request was in flight (its default
            // timeout, now over 2 minutes to actually cover how long a human might take to click
            // through the Terminal's own confirmation dialog — see TerminalDiscoveryClient's own
            // comment on that fix — is far longer than a single refresh tick). If the selected
            // terminal's beacon happened to lapse during that wait, re-enabling the button without
            // this check would let the user immediately retry against what the list now shows as
            // offline.
            _startButton.Text = "开始投屏";
            _startButton.Enabled = _terminalListBox.SelectedItem is TerminalListEntry { Live: not null };
        }
    }

    private async Task RequestPairingAndShowAsync(DiscoveredTerminal terminal, int outputIndex)
    {
        using var pairingCts = new CancellationTokenSource();
        using var codeDialog = new PairingCodeDialog(terminal.DeviceName);
        bool pairingFinished = false;
        codeDialog.FormClosed += (_, _) =>
        {
            if (!pairingFinished) pairingCts.Cancel();
        };
        bool codeShown = false;
        var response = await _discoveryClient.RequestPairingAsync(terminal, _identity, code =>
        {
            if (IsDisposed || Disposing) return;
            BeginInvoke(() =>
            {
                if (codeDialog.IsDisposed) return;
                codeDialog.SetCode(code);
                if (!codeShown)
                {
                    codeShown = true;
                    codeDialog.Show(this);
                }
            });
        }, cancellationToken: pairingCts.Token);
        bool cancelledByUser = pairingCts.IsCancellationRequested;
        pairingFinished = true;
        if (!codeDialog.IsDisposed) codeDialog.Close();
        if (cancelledByUser) return;
        if (response is { Accepted: true })
        {
            if (!PairingSecurity.IsSupportedKey(response.PairingKey, response.PairingKeyFormatVersion))
            {
                MessageBox.Show(this, "终端机返回的配对密钥无效，请升级两端后重新配对。",
                    "配对失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            await ShowPairedAsync(terminal, outputIndex, response.PairingKey!, response.PairingKeyFormatVersion, allowPairingRecovery: false);
        }
        else
        {
            MessageBox.Show(this,
                response == null ? "终端机未响应（超时）。请到终端机跟前看是否有配对确认弹窗，点「确认」后回到这里重试；若终端机没开机或不在同一局域网，也会这样超时。" : $"终端机拒绝了配对请求：{response.Reason ?? "未说明原因"}",
                "配对失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ShowPairedAsync(DiscoveredTerminal terminal, int outputIndex, string pairingKey, int keyFormatVersion, bool allowPairingRecovery = true)
    {
        // Remembered here, not just when the standby list was last built — this is the one point
        // where a pairing is actually confirmed successful, which is the right moment to persist it
        // for next time's "已配对直显" list (PLANNING.md §12), independent of whether/when the
        // standby list next happens to refresh.
        _pairedTerminals.Upsert(new PairedTerminal(
            terminal.DeviceId, terminal.DeviceName, DateTimeOffset.Now, pairingKey, keyFormatVersion));

        try
        {
            _liveCastSession?.Dispose();
            _liveCastSession = new LiveCastSession(_discoveryClient, _identity, terminal, outputIndex);
            await _liveCastSession.StartAsync();
            if (IsDisposed || Disposing) return;

            _pairedTerminal = terminal;
            _pairedWithLabel.Text = terminal.DeviceName;
            _standbyPanel.Visible = false;
            _pairedPanel.Visible = true;
            _liveCastStatsTimer.Start();
            RefreshLiveCastStats();
        }
        catch (Exception ex)
        {
            _liveCastStatsTimer.Stop();
            _liveCastSession?.Dispose();
            _liveCastSession = null;
            _pairedTerminal = null;
            _pairedPanel.Visible = false;
            _standbyPanel.Visible = true;
            if (IsDisposed || Disposing) return;
            if (allowPairingRecovery && ex.Message.Contains("重新配对", StringComparison.Ordinal))
            {
                _pairedTerminals.Remove(terminal.DeviceId);
                RefreshTerminalList();
                await RequestPairingAndShowAsync(terminal, outputIndex);
                return;
            }
            MessageBox.Show(this, $"无法开始投屏：\n\n{ex.Message}",
                "投屏启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
