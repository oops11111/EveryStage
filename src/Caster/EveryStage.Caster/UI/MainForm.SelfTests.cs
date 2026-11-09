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
    private void OnCaptureSelfTestClick(object? sender, EventArgs e)
    {
        if (_captureSelfTest.IsRunning)
        {
            _captureSelfTest.Stop();
            _captureStatsTimer.Stop();
            _captureSelfTestButton.Text = "开始屏幕捕获自检";
            _captureStatsLabel.Text = "";
        }
        else
        {
            _captureSelfTest.Start();
            _captureStatsTimer.Start();
            _captureSelfTestButton.Text = "停止屏幕捕获自检";
            RefreshCaptureStats();
        }
    }

    private void RefreshCaptureStats()
    {
        if (_captureSelfTest.LastError != null)
        {
            _captureStatsLabel.ForeColor = Color.DarkRed;
            _captureStatsLabel.Text = $"捕获出错：{_captureSelfTest.LastError}";
            _captureStatsTimer.Stop();
            _captureSelfTestButton.Text = "开始屏幕捕获自检";
            return;
        }

        _captureStatsLabel.ForeColor = Color.DimGray;
        _captureStatsLabel.Text =
            $"分辨率: {_captureSelfTest.Width}x{_captureSelfTest.Height}\n" +
            $"已捕获帧数: {_captureSelfTest.FrameCount}    近1秒帧率: {_captureSelfTest.Fps:F1}";
    }

    private void OnEncodeSelfTestClick(object? sender, EventArgs e)
    {
        if (_encodeSelfTest.IsRunning)
        {
            _encodeSelfTest.Stop();
            _encodeStatsTimer.Stop();
            _encodeSelfTestButton.Text = "开始编码自检 (捕获→NV12→H.264)";
            _encodeStatsLabel.Text = "";
        }
        else
        {
            _encodeSelfTest.Start();
            _encodeStatsTimer.Start();
            _encodeSelfTestButton.Text = "停止编码自检";
            RefreshEncodeStats();
        }
    }

    private void RefreshEncodeStats()
    {
        if (_encodeSelfTest.LastError != null)
        {
            _encodeStatsLabel.ForeColor = Color.DarkRed;
            _encodeStatsLabel.Text = $"编码出错：{_encodeSelfTest.LastError}";
            _encodeStatsTimer.Stop();
            _encodeSelfTestButton.Text = "开始编码自检 (捕获→NV12→H.264)";
            return;
        }

        // Same "only shown once non-zero" convention as RefreshLiveCastStats's backpressure lines.
        string droppedLine = _encodeSelfTest.FramesDroppedForBackpressure > 0
            ? $"\n⚠ 因编码器跟不上采集而丢弃: {_encodeSelfTest.FramesDroppedForBackpressure} 帧"
            : "";

        _encodeStatsLabel.ForeColor = Color.DimGray;
        _encodeStatsLabel.Text =
            $"已编码访问单元数: {_encodeSelfTest.AccessUnitsEncoded}\n" +
            $"编码总字节数: {_encodeSelfTest.TotalEncodedBytes} · {TuningLine(_encodeSelfTest.TuningSummary)}" +
            droppedLine;
    }

    /// <summary>"低延迟参数 5/5" from <see cref="H264HardwareEncoder.TuningSummary"/> (the label has
    /// room for one short line; the full per-setting result is in the tooltip).</summary>
    private string TuningLine(string summary)
    {
        var parts = summary.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _encodeStatsToolTip.SetToolTip(_encodeStatsLabel, summary);
        string mode = parts.Contains("encoder=software") ? "软件编码（本机没有显卡编码器）· " : "";
        parts = parts.Where(p => !p.StartsWith("encoder=", StringComparison.Ordinal)).ToArray();
        return $"{mode}低延迟参数 {parts.Count(p => p.EndsWith("=ok", StringComparison.Ordinal))}/{parts.Length}";
    }

    private readonly ToolTip _encodeStatsToolTip = new();

    private void OnAudioCaptureSelfTestClick(object? sender, EventArgs e)
    {
        if (_audioCaptureSelfTest.IsRunning)
        {
            _audioCaptureSelfTest.Stop();
            _audioCaptureStatsTimer.Stop();
            _audioCaptureSelfTestButton.Text = "开始音频采集自检 (WASAPI loopback)";
            _audioCaptureStatsLabel.Text = "";
        }
        else
        {
            _audioCaptureSelfTest.Start();
            _audioCaptureStatsTimer.Start();
            _audioCaptureSelfTestButton.Text = "停止音频采集自检";
            RefreshAudioCaptureStats();
        }
    }

    private void RefreshAudioCaptureStats()
    {
        if (_audioCaptureSelfTest.LastError != null)
        {
            _audioCaptureStatsLabel.ForeColor = Color.DarkRed;
            _audioCaptureStatsLabel.Text = $"音频采集出错：{_audioCaptureSelfTest.LastError}";
            _audioCaptureStatsTimer.Stop();
            _audioCaptureSelfTestButton.Text = "开始音频采集自检 (WASAPI loopback)";
            return;
        }

        _audioCaptureStatsLabel.ForeColor = Color.DimGray;
        _audioCaptureStatsLabel.Text =
            $"采样率: {_audioCaptureSelfTest.SampleRate}Hz   声道数: {_audioCaptureSelfTest.Channels}\n" +
            $"已捕获字节数: {_audioCaptureSelfTest.TotalBytesCaptured}    近1秒吞吐量: {_audioCaptureSelfTest.BytesPerSecond / 1024.0:F1} KB/s";
    }

    private void OnAacEncodeSelfTestClick(object? sender, EventArgs e)
    {
        if (_aacEncodeSelfTest.IsRunning)
        {
            _aacEncodeSelfTest.Stop();
            _aacEncodeStatsTimer.Stop();
            _aacEncodeSelfTestButton.Text = "开始AAC编解码自检 (WASAPI loopback→AAC→PCM)";
            _aacEncodeStatsLabel.Text = "";
        }
        else
        {
            _aacEncodeSelfTest.Start();
            _aacEncodeStatsTimer.Start();
            _aacEncodeSelfTestButton.Text = "停止AAC编解码自检";
            RefreshAacEncodeStats();
        }
    }

    private void RefreshAacEncodeStats()
    {
        if (_aacEncodeSelfTest.LastError != null)
        {
            _aacEncodeStatsLabel.ForeColor = Color.DarkRed;
            // LastError is shared between AacAudioEncoder and AacAudioDecoder failures (see
            // AacEncodeSelfTestRunner's OnEncodingFailed/OnDecodingFailed) — "编解码" rather than
            // just "编码" so this label doesn't misattribute a decode-side failure to the encoder.
            _aacEncodeStatsLabel.Text = $"AAC编解码出错：{_aacEncodeSelfTest.LastError}";
            _aacEncodeStatsTimer.Stop();
            _aacEncodeSelfTestButton.Text = "开始AAC编解码自检 (WASAPI loopback→AAC→PCM)";
            return;
        }

        _aacEncodeStatsLabel.ForeColor = Color.DimGray;
        _aacEncodeStatsLabel.Text =
            $"采样率: {_aacEncodeSelfTest.SampleRate}Hz   声道数: {_aacEncodeSelfTest.Channels}\n" +
            $"PCM输入字节数: {_aacEncodeSelfTest.TotalPcmBytesIn}\n" +
            $"已编码访问单元数: {_aacEncodeSelfTest.AccessUnitsEncoded}   编码总字节数: {_aacEncodeSelfTest.TotalEncodedBytes}\n" +
            $"解码回PCM字节数: {_aacEncodeSelfTest.TotalDecodedPcmBytes}";
    }

    private async void OnTransportSelfTestClick(object? sender, EventArgs e)
    {
        _transportSelfTestButton.Enabled = false;
        _transportStatsLabel.ForeColor = Color.DimGray;
        _transportStatsLabel.Text = "运行中...";

        try
        {
            var result = await TransportSelfTest.RunAsync();
            _transportStatsLabel.ForeColor = result.Success ? Color.DimGray : Color.DarkRed;
            _transportStatsLabel.Text = result.Success
                ? $"通过：{result.NalUnitsSent} 个NAL单元全部往返一致（含FU-A分片重组、PayloadType不匹配丢包校验）。"
                : $"失败（发送{result.NalUnitsSent}个/收到{result.NalUnitsReceived}个）：{result.FailureReason}";
        }
        catch (Exception ex)
        {
            // A self-test throwing outright (e.g. couldn't bind the loopback socket at all) is
            // itself a real, reportable finding — surface it the same as a logical failure rather
            // than let an unhandled exception on the UI thread take the whole app down.
            _transportStatsLabel.ForeColor = Color.DarkRed;
            _transportStatsLabel.Text = $"自检本身出错：{ex.Message}";
        }
        finally
        {
            _transportSelfTestButton.Enabled = true;
        }
    }

    private async void OnRawTransportSelfTestClick(object? sender, EventArgs e)
    {
        _rawTransportSelfTestButton.Enabled = false;
        _rawTransportStatsLabel.ForeColor = Color.DimGray;
        _rawTransportStatsLabel.Text = "运行中...";

        try
        {
            var result = await RawTransportSelfTest.RunAsync();
            _rawTransportStatsLabel.ForeColor = result.Success ? Color.DimGray : Color.DarkRed;
            _rawTransportStatsLabel.Text = result.Success
                ? $"通过：{result.PayloadsSent} 个payload全部往返一致，GapEvents=0（本机回环，含PayloadType不匹配丢包校验）。"
                : $"失败（发送{result.PayloadsSent}个/收到{result.PayloadsReceived}个，GapEvents={result.GapEvents}）：{result.FailureReason}";
        }
        catch (Exception ex)
        {
            // Same "a self-test throwing outright is itself a reportable finding" reasoning as
            // OnTransportSelfTestClick above.
            _rawTransportStatsLabel.ForeColor = Color.DarkRed;
            _rawTransportStatsLabel.Text = $"自检本身出错：{ex.Message}";
        }
        finally
        {
            _rawTransportSelfTestButton.Enabled = true;
        }
    }

    private async void OnDiscoverySelfTestClick(object? sender, EventArgs e)
    {
        _discoverySelfTestButton.Enabled = false;
        _discoveryStatsLabel.ForeColor = Color.DimGray;
        _discoveryStatsLabel.Text = "运行中...";

        try
        {
            var result = await DiscoveryProtocolSelfTest.RunAsync();
            _discoveryStatsLabel.ForeColor = result.Success ? Color.DimGray : Color.DarkRed;
            _discoveryStatsLabel.Text = result.Success
                ? $"通过：{result.MessagesVerified} 种消息全部往返一致（本机回环，含全部8种消息类型、畸形数据包不抛异常校验）。"
                : $"失败（已验证{result.MessagesVerified}种）：{result.FailureReason}";
        }
        catch (Exception ex)
        {
            // Same "a self-test throwing outright is itself a reportable finding" reasoning as
            // OnTransportSelfTestClick above.
            _discoveryStatsLabel.ForeColor = Color.DarkRed;
            _discoveryStatsLabel.Text = $"自检本身出错：{ex.Message}";
        }
        finally
        {
            _discoverySelfTestButton.Enabled = true;
        }
    }

    private void OnPairedTerminalStoreSelfTestClick(object? sender, EventArgs e)
    {
        // Synchronous, unlike every other self-test button here — PairedTerminalStoreSelfTest.Run()
        // only does local file I/O against a temp directory (no network/GPU/hardware wait involved),
        // so there's nothing to await and no risk of blocking the UI thread noticeably.
        _pairedTerminalStoreSelfTestButton.Enabled = false;
        _pairedTerminalStoreStatsLabel.ForeColor = Color.DimGray;
        _pairedTerminalStoreStatsLabel.Text = "运行中...";

        try
        {
            var result = PairedTerminalStoreSelfTest.Run();
            _pairedTerminalStoreStatsLabel.ForeColor = result.Success ? Color.DimGray : Color.DarkRed;
            _pairedTerminalStoreStatsLabel.Text = result.Success
                ? "通过：保存/重新加载/删除/损坏文件回退全部符合预期（临时目录，不影响真实配对列表）。"
                : $"失败：{result.FailureReason}";
        }
        catch (Exception ex)
        {
            // PairedTerminalStoreSelfTest.Run() already catches internally and reports failures via
            // its Result, but this mirrors every other self-test button's own catch block for the
            // same "a self-test throwing outright is itself a reportable finding" reasoning, in case
            // something outside that try/catch (e.g. Path.GetTempPath() itself failing) ever throws.
            _pairedTerminalStoreStatsLabel.ForeColor = Color.DarkRed;
            _pairedTerminalStoreStatsLabel.Text = $"自检本身出错：{ex.Message}";
        }
        finally
        {
            _pairedTerminalStoreSelfTestButton.Enabled = true;
        }
    }

    private void OnDeviceIdentitySelfTestClick(object? sender, EventArgs e)
    {
        // Synchronous, same reasoning as OnPairedTerminalStoreSelfTestClick just above —
        // DeviceIdentitySelfTest.Run() only does local file I/O against a temp directory.
        _deviceIdentitySelfTestButton.Enabled = false;
        _deviceIdentityStatsLabel.ForeColor = Color.DimGray;
        _deviceIdentityStatsLabel.Text = "运行中...";

        try
        {
            var result = DeviceIdentitySelfTest.Run();
            _deviceIdentityStatsLabel.ForeColor = result.Success ? Color.DimGray : Color.DarkRed;
            _deviceIdentityStatsLabel.Text = result.Success
                ? "通过：创建/重新加载/改名保存/损坏文件回退/锁定文件回退全部符合预期（临时目录，不影响真实设备身份）。"
                : $"失败：{result.FailureReason}";
        }
        catch (Exception ex)
        {
            // DeviceIdentitySelfTest.Run() already catches internally and reports failures via its
            // Result, but this mirrors every other self-test button's own catch block for the same
            // "a self-test throwing outright is itself a reportable finding" reasoning, in case
            // something outside that try/catch (e.g. Path.GetTempPath() itself failing) ever throws.
            _deviceIdentityStatsLabel.ForeColor = Color.DarkRed;
            _deviceIdentityStatsLabel.Text = $"自检本身出错：{ex.Message}";
        }
        finally
        {
            _deviceIdentitySelfTestButton.Enabled = true;
        }
    }
}
