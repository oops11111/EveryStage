using EveryStage.Caster.Casting;
using EveryStage.Caster.Discovery;
using EveryStage.Discovery;

namespace EveryStage.Caster.UI;

public sealed partial class MainForm
{
    private Func<Task>? _retry;
    private bool _starting;
    private CancellationTokenSource? _pairingCts;

    /// <summary>开始投屏 on the selected terminal: straight to casting when it is already paired,
    /// otherwise through the pairing page first. Every failure lands on the problem page with a
    /// 重试 that repeats exactly this.</summary>
    private async Task StartCastAsync()
    {
        if (_starting || _checking || SelectedEntry is not { Live: not null } entry) return;
        var terminal = entry.Live!;
        int outputIndex = _standbyView.SelectedOutputIndex;
        string monitorLabel = _standbyView.SelectedMonitorLabel;
        _retry = async () =>
        {
            ShowSelectedTerminal();
            await StartCastAsync();
        };

        if (terminal.MediaAuthenticationVersion != 1 || terminal.PairingProtocolVersion != PairingHandshake.ProtocolVersion)
        {
            ShowProblem(new Problem("需要更新程序",
                terminal.MediaAuthenticationVersion != 1
                    ? "终端机和这台电脑上的程序版本不兼容。"
                    : "终端机上的程序版本过旧，不能安全配对。",
                new[] { "把终端机和投屏器都更新到同一版本后重试。" }, null, CanRetry: false));
            return;
        }

        _starting = true;
        _terminalListBox.Enabled = false;
        _standbyView.SetBusy(true);
        try
        {
            var existing = _pairedTerminals.Find(terminal.DeviceId);
            if (existing != null && PairingSecurity.IsSupportedKey(existing.PairingKey, existing.PairingKeyFormatVersion))
            {
                await BeginCastAsync(terminal, outputIndex, monitorLabel, existing.PairingKey!, existing.PairingKeyFormatVersion);
                return;
            }
            await PairThenCastAsync(terminal, outputIndex, monitorLabel);
        }
        finally
        {
            _starting = false;
            _terminalListBox.Enabled = true;
            _standbyView.SetBusy(false);
        }
    }

    private async Task PairThenCastAsync(DiscoveredTerminal terminal, int outputIndex, string monitorLabel)
    {
        _pairingCts?.Dispose();
        var cts = _pairingCts = new CancellationTokenSource();
        _pairingView.Begin(terminal.DeviceName);
        ShowView(_pairingView);
        DiscoveryProtocol.PairResponseMessage? response;
        try
        {
            response = await _discoveryClient.RequestPairingAsync(terminal, _identity, code =>
            {
                if (IsDisposed || Disposing) return;
                BeginInvoke(() => { if (!cts.IsCancellationRequested) _pairingView.SetCode(code); });
            }, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            response = null;
        }
        if (IsDisposed || Disposing) return;
        if (cts.IsCancellationRequested)
        {
            ShowSelectedTerminal();
            return;
        }
        if (response is { Accepted: true })
        {
            if (!PairingSecurity.IsSupportedKey(response.PairingKey, response.PairingKeyFormatVersion))
            {
                ShowProblem(new Problem("配对失败", "终端机返回的配对密钥无效。",
                    new[] { "把终端机和投屏器都更新到最新版本后，重新配对。" }, null));
                return;
            }
            await BeginCastAsync(terminal, outputIndex, monitorLabel, response.PairingKey!, response.PairingKeyFormatVersion, allowPairingRecovery: false);
            return;
        }
        ShowProblem(response == null
            ? new Problem("配对没有完成", "终端机没有回应配对请求。",
                new[]
                {
                    "到终端机跟前，看屏幕上有没有配对确认弹窗，点「接受」后重试。",
                    "确认终端机开着，并且和这台电脑在同一个局域网。",
                }, null)
            : new Problem("终端机拒绝了配对", string.IsNullOrWhiteSpace(response.Reason) ? "终端机上点了「拒绝」，或者终端机不允许这台电脑投屏。" : response.Reason!,
                new[] { "重试，并在终端机上点「接受」。", "如果终端机设置了不接受投屏，请先在终端机的「设备」页里打开。" }, response.Reason));
    }

    private void CancelPairing()
    {
        _pairingCts?.Cancel();
        ShowSelectedTerminal();
    }

    private async Task BeginCastAsync(DiscoveredTerminal terminal, int outputIndex, string monitorLabel, string pairingKey, int keyFormatVersion, bool allowPairingRecovery = true)
    {
        // The one point where a pairing is confirmed: remember it for next time's offline list.
        _pairedTerminals.Upsert(new PairedTerminal(terminal.DeviceId, terminal.DeviceName, DateTimeOffset.Now, pairingKey, keyFormatVersion));

        ShowSelectedTerminal();
        _standbyView.SetBusy(true, "正在连接…");
        try
        {
            _liveCastSession?.Dispose();
            _liveCastSession = new LiveCastSession(_discoveryClient, _identity, terminal, outputIndex) { IncludeAudio = _settings.IncludeAudio };
            await _liveCastSession.StartAsync();
            if (IsDisposed || Disposing) return;
            if (_liveCastSession.LastError is { } startError) throw new InvalidOperationException(startError);
            EnterCastingMode(terminal, monitorLabel);
        }
        catch (Exception ex)
        {
            _liveCastSession?.Dispose();
            _liveCastSession = null;
            _pairedTerminal = null;
            if (IsDisposed || Disposing) return;
            if (allowPairingRecovery && ex.Message.Contains("重新配对", StringComparison.Ordinal))
            {
                // The terminal no longer knows this pairing (it was removed there): pair afresh once.
                _pairedTerminals.Remove(terminal.DeviceId);
                RefreshTerminalList();
                await PairThenCastAsync(terminal, outputIndex, monitorLabel);
                return;
            }
            ShowProblem(ProblemForStartFailure(ex.Message));
        }
    }

    /// <summary>Maps a start failure to plain words and next steps; the raw text stays available.</summary>
    private static Problem ProblemForStartFailure(string raw)
    {
        if (raw.Contains("视频编码器", StringComparison.Ordinal) || raw.Contains("Media Foundation", StringComparison.OrdinalIgnoreCase))
            return new Problem("没能开始投屏", "这台电脑找不到可用的视频编码器。通常是显卡驱动缺失，或 Windows N / KN 版缺少媒体组件。",
                new[]
                {
                    "更新显卡驱动后重试。",
                    "Windows N / KN 版：在「设置 › 应用 › 可选功能」里添加「媒体功能包」，重启后重试。",
                }, raw);
        if (raw.Contains("DuplicateOutput", StringComparison.OrdinalIgnoreCase) || raw.Contains("屏幕", StringComparison.Ordinal) && raw.Contains("捕获", StringComparison.Ordinal))
            return new Problem("没能开始投屏", "没法采集这块屏幕的画面。",
                new[] { "换一块屏幕试试，或者关掉正在全屏独占的程序（例如游戏）后重试。", "仍不行的话，打开「诊断」检查这台电脑。" }, raw);
        if (raw.Contains("超时", StringComparison.Ordinal) || raw.Contains("未响应", StringComparison.Ordinal) || raw.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return new Problem("终端机没有响应", "投屏请求发出去了，但终端机没有回应。",
                new[] { "确认终端机开着，并且和这台电脑在同一个局域网。", "终端机正在显示别人的投屏时，可能需要先在终端机上同意切换。" }, raw);
        if (raw.Contains("拒绝", StringComparison.Ordinal))
            return new Problem("终端机拒绝了投屏", "终端机现在不接受这台电脑的投屏。",
                new[] { "在终端机上确认允许投屏后重试。" }, raw);
        return new Problem("没能开始投屏", "开始投屏时出了问题。",
            new[] { "确认终端机开着、在同一个局域网后重试。", "仍不行的话，打开「诊断」检查这台电脑，并把诊断报告发给技术支持。" }, raw);
    }

    private void ShowProblem(Problem problem)
    {
        _lastProblem = problem;
        _problemView.Show(problem);
        ShowView(_problemView);
    }

    private Problem? _lastProblem;
}
