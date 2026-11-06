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
    private void RefreshLiveCastStats()
    {
        if (_liveCastSession == null) return;

        // Kept updating even once LastError is set below (and the stats timer stops) — the elapsed
        // time up to the moment of failure is still meaningful, unlike the rest of the stats block
        // below which gets replaced by the error message entirely.
        var elapsed = _liveCastSession.Elapsed;
        // Formatted from TotalHours rather than TimeSpan's "hh" custom-format specifier (which
        // wraps at 24, like a clock) — a continuous cast running that long is unlikely but this
        // avoids silently showing a wrong, wrapped-around hour count if it ever happens. Purely a
        // display nicety; note this project's README already documents RTP's own 32-bit timestamp
        // wraparound at ~13.25 hours as a separate, more fundamental limit on session length.
        _elapsedLabel.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

        if (_liveCastSession.LastError != null)
        {
            _liveCastStatsLabel.ForeColor = Color.DarkRed;
            _liveCastStatsLabel.Text = $"投屏出错：{_liveCastSession.LastError}";
            _qualityLabel.ForeColor = ModernUi.Danger;
            _qualityLabel.Text = "●  投屏连接异常";
            _liveCastStatsTimer.Stop();
            return;
        }

        _liveCastStatsLabel.ForeColor = Color.DimGray;
        string audioLine = _liveCastSession.HasAudio
            ? $"音频: 已发送 {_liveCastSession.AudioBytesSent} 字节" + (_liveCastSession.AudioError != null ? $"（出错：{_liveCastSession.AudioError}）" : "")
            : $"音频: 未启用" + (_liveCastSession.AudioError != null ? $"（{_liveCastSession.AudioError}）" : "");

        // The one line in this panel that isn't a purely local claim — see LiveCastSession's doc
        // comment on CastStatusMessage. "未确认" covers both "never heard from the terminal at all"
        // and "used to hear from it, not anymore" on purpose: this UI can't tell those apart, and
        // shouldn't pretend to. This "确认" is specifically the discovery-socket status channel's
        // health, not the RTP media streams' — they travel independently (see this project's README
        // "已知风险" on why a healthy media stream and a healthy status channel aren't the same
        // thing), so this line can say "确认" while video/audio are actually struggling, or vice
        // versa; the "延迟估算" figure below is likewise only meaningful if this machine's and the
        // Terminal's clocks are reasonably in sync, which this protocol never verifies.
        string latencyNote = _liveCastSession.LastStatusLatencyEstimate is { } latency
            ? $"，延迟估算: {latency.TotalMilliseconds:F0}ms"
            : "";
        string qualityLatency = _liveCastSession.RealRoundTripEstimate is { } liveRtt
            ? $"延迟 {liveRtt.TotalMilliseconds:F0} ms"
            : "延迟测量中";
        _liveStateLabel.ForeColor = _liveCastSession.IsTerminalAlive
            ? (_liveCastSession.TerminalOnProgram ? ModernUi.Danger : ModernUi.Warning)
            : ModernUi.Warning;
        _liveStateLabel.Text = _liveCastSession.IsTerminalAlive
            ? (_liveCastSession.TerminalOnProgram ? "●  已连接 · 大屏显示中" : "●  已连接 · 等待终端投到大屏")
            : "●  已连接 · 等待终端确认";
        _qualityLabel.ForeColor = _liveCastSession.IsTerminalAlive ? ModernUi.Success : ModernUi.Warning;
        _qualityLabel.Text = _liveCastSession.IsTerminalAlive
            ? $"●  {qualityLatency}  ·  终端在线"
            : $"●  {qualityLatency}  ·  等待终端确认";
        // The trailing "（仅代表状态通道...）" caveat is the one piece of this project's README risk
        // #35 that's actually user-facing rather than just a code comment: it directly answers that
        // risk's own complaint ("这个仓库现在把两者放在UI上却没有特别提醒用户这个区别") without
        // attempting the harder, still-unsolved problem of actually correlating status-channel
        // health with media-stream health (see risk #35 for why that's not attempted here — a
        // Caster whose own screen content is simply static legitimately sends no new access units
        // for seconds at a time under this project's adaptive-frame-rate DDA capture, so "Terminal
        // hasn't reported a new decoded frame recently" is not on its own a reliable "media stream
        // stalled" signal, and a heuristic built on it risked crying wolf during completely normal
        // static-content casting — worse than the plain caveat this settles for instead).
        // TerminalVideoBytesReceived/TerminalHasAudio/TerminalAudioBytesReceived come from the same
        // CastStatusMessage as TerminalFramesDecoded above but, until now, had no caller anywhere in
        // this UI — the protocol/session layer fully plumbed them through, nothing above ever
        // displayed them. Kept as one compact "视频X字节，音频Y字节" clause rather than separate
        // lines, matching the density of the existing "已解码N帧" clause it sits next to.
        string terminalAudioNote = _liveCastSession.TerminalHasAudio
            ? $"，音频已接收 {_liveCastSession.TerminalAudioBytesReceived} 字节"
            : "";
        string terminalLine = _liveCastSession.IsTerminalAlive
            ? $"终端机确认: {(_liveCastSession.TerminalOnProgram ? "大屏显示中" : "设备窗口预览中")}，已解码 {_liveCastSession.TerminalFramesDecoded} 帧，" +
              $"视频已接收 {_liveCastSession.TerminalVideoBytesReceived} 字节{terminalAudioNote}{latencyNote}" +
              (_liveCastSession.TerminalVideoError != null ? $"（终端机视频出错：{_liveCastSession.TerminalVideoError}）" : "") +
              (_liveCastSession.TerminalAudioError != null ? $"（终端机音频出错：{_liveCastSession.TerminalAudioError}）" : "") +
              "\n（仅代表状态通道送达，不代表画面/声音本身一定在正常播放）"
            : "终端机确认: 未确认（尚未收到或已停止收到终端机的状态回报）";

        // Independent of terminalLine/IsTerminalAlive on purpose — RealRoundTripEstimate comes from
        // its own ping/pong exchange (DiscoveryProtocol.PingMessage), not the CastStatusMessage
        // channel terminalLine reports on, so this can show a real number even if the status channel
        // has gone quiet (or vice versa) — that divergence would itself be a useful diagnostic signal
        // this UI shouldn't hide by only showing RTT alongside a "confirmed" status line. Only shown
        // once at least one ping has ever succeeded — same "don't show a permanent placeholder" logic
        // as backpressureLine/encoderBackpressureLine below. The second, conditional line uses the
        // new LiveCastSession.IsRttStale — same "several missed cycles, not one" reasoning
        // IsTerminalAlive already applies to the status channel, mirrored here because
        // RealRoundTripEstimate previously kept showing its last real number forever with no
        // indication it might no longer be current once the Terminal stopped responding to pings.
        // NOTE: this label's Bounds height (108) was already a known tight fit before either RTT
        // line existed (see the label's own construction comment) — two lines here now instead of
        // one makes clipping on top of an already-showing backpressure warning more likely, not
        // less; still not fixed this round, same accepted-but-unverified risk that comment flags.
        // payloadTypeMismatchLine below stacks onto the same risk (a sixth possible line, though one
        // that should in practice never actually show) — not resized for the same reason: growing the
        // label for a line expected to stay permanently invisible would trade a real, common-case
        // problem (clipping when several already-common lines are showing) for wasted vertical space
        // in the overwhelmingly more common case where it never appears at all.
        string rttLine = _liveCastSession.RealRoundTripEstimate is { } rtt
            ? $"\n真实RTT估算: {rtt.TotalMilliseconds:F0}ms（不受两台机器时钟是否同步的影响）" +
              (_liveCastSession.IsRttStale ? "\n⚠ 已超过10秒没有成功测量，这个数字可能已经过时" : "")
            : "";

        // Only shown once something has actually been dropped — on a healthy LAN both counters
        // should stay at 0 forever, and a permanent "已丢弃: 0" line would just be noise. See
        // LiveCastSession.OnAccessUnitEncoded/OnPcmCaptured for the backpressure policy this reports.
        string backpressureLine = _liveCastSession.AccessUnitsDroppedForBackpressure > 0 || _liveCastSession.AudioChunksDroppedForBackpressure > 0
            ? $"\n⚠ 因网络发送跟不上而丢弃: 视频 {_liveCastSession.AccessUnitsDroppedForBackpressure} 个访问单元" +
              $"，音频 {_liveCastSession.AudioChunksDroppedForBackpressure} 个分片"
            : "";
        // A different bottleneck from backpressureLine above (那是网络发送跟不上编码器，这是编码器
        // 本身跟不上屏幕采集) — see H264HardwareEncoder.SubmitFrame's drop policy and README风险#17。
        string encoderBackpressureLine = _liveCastSession.EncoderFramesDroppedForBackpressure > 0
            ? $"\n⚠ 因编码器跟不上采集而丢弃: {_liveCastSession.EncoderFramesDroppedForBackpressure} 帧"
            : "";

        // TerminalPayloadTypeMismatches comes from the same CastStatusMessage as the terminalLine
        // fields above but, until now, was never actually read on this side — EveryStage.Transport's
        // RtpReceiver/RawRtpReceiver have counted it since it was added, and it always reached this
        // class via OnCastStatusReceived, but nothing displayed it (see EveryStage.Transport's README
        // "已知风险" on this counter having no UI/log consumer). Same only-shown-if-nonzero treatment
        // as backpressureLine/encoderBackpressureLine above: on a healthy LAN this should stay 0
        // forever (both ends use the same hardcoded PayloadType constant), so a permanent "0" line
        // would just be noise — a nonzero value here means the Terminal's RtpReceiver/RawRtpReceiver
        // dropped a stray/foreign RTP-shaped datagram, not that this cast's own stream is unhealthy.
        string payloadTypeMismatchLine = _liveCastSession.TerminalPayloadTypeMismatches > 0
            ? $"\n⚠ 终端机丢弃了 {_liveCastSession.TerminalPayloadTypeMismatches} 个PayloadType不匹配的RTP包" +
              "（同一端口上出现了陌生/无关的包，不是这次投屏本身的问题）"
            : "";

        _liveCastStatsLabel.Text =
            $"分辨率: {_liveCastSession.Width}x{_liveCastSession.Height}\n" +
            $"已捕获帧数: {_liveCastSession.FramesCaptured}   已发送访问单元: {_liveCastSession.AccessUnitsSent}\n" +
            $"已发送字节数: {_liveCastSession.BytesSent}\n" +
            audioLine + "\n" +
            terminalLine +
            rttLine +
            backpressureLine +
            encoderBackpressureLine +
            payloadTypeMismatchLine;
    }
}
