using EveryStage.Caster.Discovery;

namespace EveryStage.Caster.UI;

public sealed partial class MainForm
{
    private string _castMonitorLabel = "";
    private long _lastDropCount;
    private DateTime _lastDropAt = DateTime.MinValue;
    private long _lastEncoderDropCount;
    private DateTime _lastEncoderDropAt = DateTime.MinValue;

    /// <summary>The cast is running: the window steps aside and the bar takes over.</summary>
    private void EnterCastingMode(DiscoveredTerminal terminal, string monitorLabel)
    {
        _pairedTerminal = terminal;
        _castMonitorLabel = monitorLabel;
        _lastDropCount = 0;
        _lastDropAt = DateTime.MinValue;
        _lastEncoderDropCount = 0;
        _lastEncoderDropAt = DateTime.MinValue;
        var screen = Screen.FromControl(this);
        _castBar = new CastBar(terminal.DeviceName);
        // Deferred: the bar is closed from inside its own button's click otherwise.
        _castBar.StopRequested += () => { if (!IsDisposed) BeginInvoke(new Action(() => StopCast())); };
        _castBar.PlaceOn(screen);
        _castBar.Show();
        Hide();
        _liveCastStatsTimer.Start();
        RefreshLiveCastStats();
    }

    /// <summary>Ends the cast (from the bar's 停止, or because it broke) and brings the window back.</summary>
    private void StopCast(Problem? problem = null)
    {
        _liveCastStatsTimer.Stop();
        _liveCastSession?.Stop();
        _liveCastSession?.Dispose();
        _liveCastSession = null;
        _pairedTerminal = null;
        if (_castBar != null)
        {
            var bar = _castBar;
            _castBar = null;
            bar.Close(); // a shown, modeless form disposes itself on Close
        }
        if (IsDisposed) return;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        RefreshMonitorList();
        if (problem != null) ShowProblem(problem);
        else ShowSelectedTerminal();
    }

    private void RefreshLiveCastStats()
    {
        var session = _liveCastSession;
        if (session == null || _castBar == null) return;

        if (session.LastError is { } error)
        {
            _retry = async () =>
            {
                ShowSelectedTerminal();
                await StartCastAsync();
            };
            StopCast(new Problem("投屏中断了", "投屏过程中出了问题，已经停止。",
                new[] { "检查这台电脑和终端机的网络连接后重试。", "仍不行的话，打开「诊断」检查这台电脑。" }, error));
            return;
        }

        var elapsed = session.Elapsed;
        string elapsedText = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

        // Anything dropped for backpressure in the last few seconds means the network or the encoder
        // cannot keep up right now.
        // Network drops (the send queue) and encoder drops (this PC cannot encode fast enough) are told
        // apart: calling an encoder stall 「网络拥堵」 sent people looking at the wrong thing.
        long networkDrops = session.AccessUnitsDroppedForBackpressure;
        long encoderDrops = session.EncoderFramesDroppedForBackpressure;
        if (networkDrops > _lastDropCount) _lastDropAt = DateTime.UtcNow;
        _lastDropCount = networkDrops;
        if (encoderDrops > _lastEncoderDropCount) _lastEncoderDropAt = DateTime.UtcNow;
        _lastEncoderDropCount = encoderDrops;
        bool congested = DateTime.UtcNow - _lastDropAt < TimeSpan.FromSeconds(5)
            || session.TerminalPacketLossPercent is > 2.0;
        bool encoderBusy = DateTime.UtcNow - _lastEncoderDropAt < TimeSpan.FromSeconds(5);
        bool noPicture = session.AccessUnitsSent == 0 && session.FramesCaptured >= 15;

        // OP-12/OP-13/OP-14: health must follow the terminal's own report, not just our send
        // side. A decoder error in the latest report, or the terminal holding the cast in its
        // preview window instead of Program, is a downgrade — never green. And silence past the
        // 5 s liveness window is a red failure state, never an endless "waiting": the session
        // is NOT ended automatically; the warning line below names the 停止 action.
        bool terminalSilent = !session.IsTerminalAlive && elapsed >= TimeSpan.FromSeconds(5);
        (CastHealth health, string healthText) = terminalSilent
            ? (CastHealth.Failed, "终端无回应")
            : !session.IsTerminalAlive
                ? (session.FramesCaptured == 0 && elapsed < TimeSpan.FromSeconds(5) ? CastHealth.Connecting : CastHealth.Waiting,
                   elapsed < TimeSpan.FromSeconds(5) ? "连接中" : "等待终端确认")
                : session.TerminalVideoError != null ? (CastHealth.Congested, "终端解码出错")
                : noPicture ? (CastHealth.Congested, "画面还没出来")
                : congested ? (CastHealth.Congested, "网络拥堵")
                : encoderBusy ? (CastHealth.Congested, "电脑忙，已降帧")
                : !session.TerminalOnProgram ? (CastHealth.Congested, "未在大屏显示")
                : (CastHealth.Good, "大屏显示中");

        string latency = session.RealRoundTripEstimate is { } rtt
            ? (session.IsRttStale ? "测量中" : $"约 {rtt.TotalMilliseconds:F0} ms")
            : "测量中";
        // OP-30: the sound cell is liveness-gated — with no terminal report there is nothing to
        // say about sound. "不可用" is gone: audio simply not being sent says so.
        string sound = !session.IsTerminalAlive ? "未知（终端无回应）"
            : !_settings.IncludeAudio ? "未投送"
            : session.HasAudio ? (session.AudioError != null ? "出错" : "正常")
            : "未投送音频";

        // The warning line mirrors the details text's drop lines: shown only while a drop
        // counter is non-zero, one line, same wording — plus the OP-14 silence prompt.
        var warningParts = new List<string>();
        if (terminalSilent)
            warningParts.Add("终端超过 5 秒没有回应，画面可能没有送达 — 点右上角「停止」结束本次投屏。");
        if (networkDrops > 0 || session.AudioChunksDroppedForBackpressure > 0)
            warningParts.Add($"网络发送跟不上丢弃：视频 {networkDrops} 个，声音 {session.AudioChunksDroppedForBackpressure} 个");
        if (encoderDrops > 0)
            warningParts.Add($"编码跟不上采集丢弃 {encoderDrops} 帧");

        _castBar.UpdateState(new CastBarState(
            elapsedText, health, healthText,
            string.IsNullOrEmpty(_castMonitorLabel) ? $"{session.Width}×{session.Height}" : _castMonitorLabel,
            latency, sound,
            session.EncoderTuningSummary.Contains("encoder=software", StringComparison.Ordinal),
            BuildTechnicalDetails(),
            $"{session.SelectedBitrateBps / 1_000_000} Mbps",
            session.IsTerminalAlive ? $"已解码 {session.TerminalFramesDecoded} 帧" : "等待终端回报",
            string.Join("；", warningParts)));
    }

    /// <summary>The full technical picture of the running cast, for the bar's details and the
    /// diagnostics report. Raw on purpose: softened wording would hide what support needs.</summary>
    private string BuildTechnicalDetails()
    {
        var session = _liveCastSession;
        if (session == null) return "";
        var lines = new List<string>
        {
            $"分辨率 {session.Width}×{session.Height} · 码率档位 {session.SelectedBitrateBps / 1_000_000} Mbps",
            $"已采集 {session.FramesCaptured} 帧 · 已发送 {session.AccessUnitsSent} 个访问单元 / {session.BytesSent / 1024 / 1024} MB",
            $"编码器：{session.EncoderTuningSummary}" + (session.EncoderFallbackNote is { } note ? $"（{note}）" : ""),
            session.HasAudio
                ? $"声音：已发送 {session.AudioBytesSent / 1024} KB" + (session.AudioError != null ? $"（出错：{session.AudioError}）" : "")
                : "声音：未投送" + (session.AudioError != null ? $"（{session.AudioError}）" : ""),
        };
        if (session.IsTerminalAlive)
        {
            lines.Add($"终端机回报：{(session.TerminalOnProgram ? "大屏显示中" : "设备窗口预览中")}，已解码 {session.TerminalFramesDecoded} 帧，" +
                $"收到视频 {session.TerminalVideoBytesReceived / 1024 / 1024} MB" +
                (session.TerminalHasAudio ? $"、声音 {session.TerminalAudioBytesReceived / 1024} KB" : "") +
                (session.TerminalPacketLossPercent is { } loss ? $"，丢包约 {loss:F1}%" : ""));
            if (session.TerminalVideoError != null) lines.Add($"终端机视频出错：{session.TerminalVideoError}");
            if (session.TerminalAudioError != null) lines.Add($"终端机声音出错：{session.TerminalAudioError}");
        }
        else lines.Add("终端机回报：还没收到（或已经停了）");
        if (session.RealRoundTripEstimate is { } rtt)
            lines.Add($"往返延迟 {rtt.TotalMilliseconds:F0} ms" + (session.IsRttStale ? "（超过 10 秒没测到，可能已过时）" : ""));
        if (session.AccessUnitsDroppedForBackpressure > 0 || session.AudioChunksDroppedForBackpressure > 0)
            lines.Add($"网络发送跟不上而丢弃：视频 {session.AccessUnitsDroppedForBackpressure} 个，声音 {session.AudioChunksDroppedForBackpressure} 个");
        if (session.EncoderFramesDroppedForBackpressure > 0)
            lines.Add($"编码跟不上采集而丢弃：{session.EncoderFramesDroppedForBackpressure} 帧");
        if (session.TerminalPayloadTypeMismatches > 0)
            lines.Add($"终端机丢弃了 {session.TerminalPayloadTypeMismatches} 个无关的 RTP 包（同端口上有别的数据，不是这次投屏的问题）");
        if (!string.IsNullOrEmpty(session.EncoderForceKeyframeSummary))
            lines.Add($"关键帧请求：{session.EncoderForceKeyframeSummary}");
        return string.Join(Environment.NewLine, lines);
    }
}
