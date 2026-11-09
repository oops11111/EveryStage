using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using EveryStage.Caster.Discovery;
using EveryStage.Discovery;
using EveryStage.Transport;

namespace EveryStage.Caster.UI;

public sealed partial class MainForm
{
    private IReadOnlyList<CheckResult> _lastCheckResults = Array.Empty<CheckResult>();
    private DateTime? _lastCheckAt;
    private bool _checking;

    private static readonly string[] CheckNames = { "屏幕采集", "视频编码", "声音采集", "声音编码", "网络（本机回环）", "本地数据" };

    /// <summary>诊断 › 开始检查: runs the existing self-tests one after another, briefly each, and turns
    /// them into one pass / note / fail line per stage.</summary>
    private async Task RunChecksAsync()
    {
        if (_checking || _liveCastSession != null) return;
        _checking = true;
        _diagnosticsView.SetRunning(true);
        StopIndividualSelfTests();
        var results = CheckNames.Select(name => new CheckResult(name, CheckStatus.Pending, "")).ToList();
        void Set(int index, CheckStatus status, string detail)
        {
            results[index] = new CheckResult(CheckNames[index], status, detail);
            _diagnosticsView.SetResults(results.ToList());
        }
        _diagnosticsView.SetResults(results.ToList());
        try
        {
            // 1. Screen capture.
            Set(0, CheckStatus.Running, "检查中…");
            using (var capture = new EveryStage.Caster.Capture.CaptureSelfTestRunner())
            {
                capture.Start();
                await Task.Delay(1500);
                if (capture.LastError is { } error) Set(0, CheckStatus.Fail, error);
                else if (capture.Width > 0) Set(0, CheckStatus.Pass, $"{capture.Width}×{capture.Height}");
                else Set(0, CheckStatus.Warn, "没有拿到画面（屏幕可能处于锁定或被独占）");
                capture.Stop();
            }

            // 2. Video encoding (hardware, or the software fallback).
            Set(1, CheckStatus.Running, "检查中…");
            using (var encode = new EveryStage.Caster.Encode.EncodeSelfTestRunner())
            {
                encode.Start();
                await Task.Delay(3000);
                string tuning = encode.TuningSummary;
                if (encode.LastError is { } error) Set(1, CheckStatus.Fail, error);
                else if (tuning.Contains("encoder=software", StringComparison.Ordinal))
                    Set(1, CheckStatus.Warn, "软件编码（没有显卡编码器，CPU 占用较高）");
                else if (encode.AccessUnitsEncoded > 0) Set(1, CheckStatus.Pass, "显卡编码");
                else if (tuning.Length > 0) Set(1, CheckStatus.Pass, "显卡编码（屏幕没变化，未出帧）");
                else Set(1, CheckStatus.Warn, "编码器没有启动");
                encode.Stop();
            }

            // 3. Sound capture.
            Set(2, CheckStatus.Running, "检查中…");
            using (var audio = new EveryStage.Caster.Capture.AudioCaptureSelfTestRunner())
            {
                audio.Start();
                await Task.Delay(1500);
                if (audio.LastError is { } error) Set(2, CheckStatus.Fail, error);
                else Set(2, CheckStatus.Pass, $"{audio.SampleRate / 1000.0:0.#} kHz · {audio.Channels} 声道");
                audio.Stop();
            }

            // 4. Sound encoding (AAC).
            Set(3, CheckStatus.Running, "检查中…");
            using (var aac = new EveryStage.Caster.Encode.AacEncodeSelfTestRunner())
            {
                aac.Start();
                await Task.Delay(2500);
                if (aac.LastError is { } error) Set(3, CheckStatus.Fail, error);
                else if (aac.AccessUnitsEncoded > 0) Set(3, CheckStatus.Pass, "AAC");
                else Set(3, CheckStatus.Warn, "电脑没在放声音，编码未能验证");
                aac.Stop();
            }

            // 5. Network stack, over loopback.
            Set(4, CheckStatus.Running, "检查中…");
            var failures = new List<string>();
            try
            {
                var video = await TransportSelfTest.RunAsync();
                if (!video.Success) failures.Add($"视频：{video.FailureReason}");
                var raw = await RawTransportSelfTest.RunAsync();
                if (!raw.Success) failures.Add($"声音：{raw.FailureReason}");
                var discovery = await DiscoveryProtocolSelfTest.RunAsync();
                if (!discovery.Success) failures.Add($"发现协议：{discovery.FailureReason}");
            }
            catch (Exception ex) { failures.Add(ex.Message); }
            Set(4, failures.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, failures.Count == 0 ? "正常" : string.Join("；", failures));

            // 6. Local data files.
            Set(5, CheckStatus.Running, "检查中…");
            failures.Clear();
            try
            {
                var store = PairedTerminalStoreSelfTest.Run();
                if (!store.Success) failures.Add($"配对列表：{store.FailureReason}");
                var identity = DeviceIdentitySelfTest.Run();
                if (!identity.Success) failures.Add($"设备身份：{identity.FailureReason}");
            }
            catch (Exception ex) { failures.Add(ex.Message); }
            Set(5, failures.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, failures.Count == 0 ? "正常" : string.Join("；", failures));
        }
        catch (Exception ex)
        {
            int running = results.FindIndex(r => r.Status == CheckStatus.Running);
            if (running >= 0) Set(running, CheckStatus.Fail, ex.Message);
        }
        finally
        {
            for (int i = 0; i < results.Count; i++)
                if (results[i].Status is CheckStatus.Pending or CheckStatus.Running) Set(i, CheckStatus.Warn, "未完成");
            _lastCheckResults = results.ToList();
            _lastCheckAt = DateTime.Now;
            _checking = false;
            if (!IsDisposed) _diagnosticsView.SetRunning(false);
        }
    }

    /// <summary>The individual self-tests and the one-click check must not run side by side.</summary>
    private void StopIndividualSelfTests()
    {
        if (_captureSelfTest.IsRunning) OnCaptureSelfTestClick(null, EventArgs.Empty);
        if (_encodeSelfTest.IsRunning) OnEncodeSelfTestClick(null, EventArgs.Empty);
        if (_audioCaptureSelfTest.IsRunning) OnAudioCaptureSelfTestClick(null, EventArgs.Empty);
        if (_aacEncodeSelfTest.IsRunning) OnAacEncodeSelfTestClick(null, EventArgs.Empty);
    }

    /// <summary>A plain-text report for support: versions, displays, terminals, settings, the last
    /// check and the last problem with its raw message.</summary>
    private void ExportReport()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出诊断报告",
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = $"EveryStage-投屏器诊断-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var text = new StringBuilder();
        string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
        text.AppendLine("EveryStage 投屏器诊断报告");
        text.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"程序版本：{version}");
        text.AppendLine($"系统：{RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）");
        text.AppendLine($"主题：{ModernUi.ThemeName(_settings.Theme)} · 投送声音：{(_settings.IncludeAudio ? "是" : "否")}");
        text.AppendLine();

        text.AppendLine("显示器：");
        try
        {
            using var gpu = new EveryStage.Rendering.D3D11Device();
            foreach (var output in EveryStage.Caster.Capture.ScreenCaptureSource.EnumerateOutputs(gpu))
                text.AppendLine($"  {output.OutputIndex}: {output.DeviceName} {output.Bounds.Width}×{output.Bounds.Height} @ {output.Bounds.X},{output.Bounds.Y}");
        }
        catch (Exception ex) { text.AppendLine($"  无法列出：{ex.Message}"); }
        text.AppendLine();

        text.AppendLine("终端：");
        foreach (var entry in _terminalListBox.Items.OfType<TerminalListEntry>())
            text.AppendLine($"  {entry.DeviceName} · {(entry.Live != null ? $"在线 {entry.Live.Address}" : "离线")} · {(_pairedTerminals.Find(entry.DeviceId) != null ? "已配对" : "未配对")}");
        if (_terminalListBox.Items.Count == 0) text.AppendLine("  （没有发现终端）");
        text.AppendLine();

        text.AppendLine(_lastCheckAt is { } at ? $"一键检查（{at:HH:mm:ss}）：" : "一键检查：还没有运行");
        foreach (var result in _lastCheckResults)
            text.AppendLine($"  {result.Name}：{result.Status switch { CheckStatus.Pass => "通过", CheckStatus.Warn => "注意", CheckStatus.Fail => "失败", _ => "未完成" }} {result.Detail}");
        text.AppendLine();

        if (_lastProblem is { } problem)
        {
            text.AppendLine($"最近一次问题：{problem.Title} — {problem.Reason}");
            if (!string.IsNullOrWhiteSpace(problem.Raw)) text.AppendLine($"  原始信息：{problem.Raw}");
        }
        else text.AppendLine("最近一次问题：无");

        try
        {
            File.WriteAllText(dialog.FileName, text.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"报告没有保存成功：{ex.Message}", "导出诊断报告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
