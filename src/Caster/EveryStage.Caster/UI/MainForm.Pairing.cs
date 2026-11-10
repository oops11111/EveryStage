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
        catch (Exception ex)
        {
            // Anything unexpected still ends on a page that says what happened, never on a silent stop.
            if (!IsDisposed) ShowProblem(ProblemForStartFailure(ex.Message));
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
            : response.Reason?.StartsWith("终端机无法保存配对记录", StringComparison.Ordinal) == true
            ? UnreadablePairingDataProblem("终端机", response.Reason)
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
        // The one point where a pairing is confirmed: remember it for next time's offline list. A
        // failure here used to vanish (this runs from a fire-and-forget task) and the page just stopped.
        try
        {
            _pairedTerminals.Upsert(new PairedTerminal(terminal.DeviceId, terminal.DeviceName, DateTimeOffset.Now, pairingKey, keyFormatVersion));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProblem(UnreadablePairingDataProblem("这台电脑", ex.Message));
            return;
        }

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
        // The Terminal reports its own decoder failure back through the cast start: that is the
        // TERMINAL's machine, not this one.
        if (raw.Contains("视频解码器", StringComparison.Ordinal))
            return new Problem("终端机没法显示投屏", "终端机那台电脑找不到可用的视频解码器。",
                new[]
                {
                    "更新终端机电脑的显卡驱动后重试。",
                    "终端机是 Windows N / KN 版的话：在「设置 › 应用 › 可选功能」里添加「媒体功能包」，重启后重试。",
                }, raw);
        // Bare English text: an older Terminal (no software decoder yet) or a component missing on
        // either side — the message does not say which.
        if (raw.StartsWith("No matching Media Foundation transform", StringComparison.Ordinal))
            return new Problem("没能开始投屏", "这台电脑或终端机缺少视频编码 / 解码组件。",
                new[]
                {
                    "把终端机和投屏器都更新到最新版本：新版在没有显卡编解码时会自动改用 Windows 自带的软件编解码。",
                    "仍不行的话，更新两台电脑的显卡驱动；Windows N / KN 版需要先安装「媒体功能包」。",
                }, raw);
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
        if (raw.Contains("未确认就绪", StringComparison.Ordinal) || raw.Contains("超时", StringComparison.Ordinal)
            || raw.Contains("未响应", StringComparison.Ordinal) || raw.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return new Problem("终端机没有回应", "开始投屏的请求发出去了，但终端机一直没有确认准备好。",
                new[]
                {
                    "确认终端机程序和投屏器是同一版本（都装最新的安装包）。",
                    "确认终端机开着、和这台电脑在同一个局域网，并且两台电脑的系统时间相差不超过 2 分钟。",
                    "仍不行的话，把终端机电脑上 %ProgramData%\\EveryStage\\logs 文件夹发给技术支持。",
                }, raw);
        if (raw.Contains("拒绝", StringComparison.Ordinal))
            return new Problem("终端机拒绝了投屏", "终端机现在不接受这台电脑的投屏。",
                new[] { "在终端机上确认允许投屏后重试。" }, raw);
        return new Problem("没能开始投屏", "开始投屏时出了问题。",
            new[] { "确认终端机开着、在同一个局域网后重试。", "仍不行的话，打开「诊断」检查这台电脑，并把诊断报告发给技术支持。" }, raw);
    }

    /// <summary>The pairing file (or device identity) under %ProgramData%\EveryStage\secure exists but
    /// cannot be read or written by the account running the app.</summary>
    private static Problem UnreadablePairingDataProblem(string where, string? raw) => new(
        "配对没能保存",
        $"{where}读不出或写不进自己的配对数据，所以配对无法完成。通常是 %ProgramData%\\EveryStage\\secure 里的文件属于另一个 Windows 账户。",
        new[]
        {
            $"在{where}上用管理员身份删除 %ProgramData%\\EveryStage\\secure 文件夹。",
            "重启程序（终端机和投屏器都要），然后重新配对。",
        }, raw);

    private void ShowProblem(Problem problem)
    {
        _lastProblem = problem;
        _problemView.Show(problem);
        ShowView(_problemView);
    }

    private Problem? _lastProblem;
}
