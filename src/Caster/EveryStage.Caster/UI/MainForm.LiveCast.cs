using EveryStage.Caster.Discovery;

namespace EveryStage.Caster.UI;

public sealed partial class MainForm
{
    private string _castMonitorLabel = "";
    private long _lastDropCount;
    private DateTime _lastDropAt = DateTime.MinValue;

    /// <summary>The cast is running: the window steps aside and the bar takes over.</summary>
    private void EnterCastingMode(DiscoveredTerminal terminal, string monitorLabel)
    {
        _pairedTerminal = terminal;
        _castMonitorLabel = monitorLabel;
        _lastDropCount = 0;
        _lastDropAt = DateTime.MinValue;
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
        long drops = session.AccessUnitsDroppedForBackpressure + session.EncoderFramesDroppedForBackpressure;
        if (drops > _lastDropCount) _lastDropAt = DateTime.UtcNow;
        _lastDropCount = drops;
        bool congested = DateTime.UtcNow - _lastDropAt < TimeSpan.FromSeconds(5)
            || session.TerminalPacketLossPercent is > 2.0;

        (CastHealth health, string healthText) = !session.IsTerminalAlive
            ? (session.FramesCaptured == 0 && elapsed < TimeSpan.FromSeconds(5) ? CastHealth.Connecting : CastHealth.Waiting,
               elapsed < TimeSpan.FromSeconds(5) ? "连接中" : "等待终端确认")
            : congested ? (CastHealth.Congested, "网络拥堵")
            : (CastHealth.Good, session.TerminalOnProgram ? "大屏显示中" : "流畅");

        string latency = session.RealRoundTripEstimate is { } rtt
            ? (session.IsRttStale ? "测量中" : $"约 {rtt.TotalMilliseconds:F0} ms")
            : "测量中";
        string sound = !_settings.IncludeAudio ? "未投送"
            : session.HasAudio ? (session.AudioError != null ? "出错" : "正常")
            : "不可用";

        _castBar.UpdateState(new CastBarState(
            elapsedText, health, healthText,
            string.IsNullOrEmpty(_castMonitorLabel) ? $"{session.Width}×{session.Height}" : _castMonitorLabel,
            latency, sound,
            session.EncoderTuningSummary.Contains("encoder=software", StringComparison.Ordinal),
            BuildTechnicalDetails()));
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
            $"编码器：{session.EncoderTuningSummary}",
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
