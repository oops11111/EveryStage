using System.Drawing.Imaging;
using System.Net;
using EveryStage.Caster.Discovery;
using EveryStage.Discovery;

namespace EveryStage.Caster.UI;

public sealed partial class MainForm
{
    /// <summary>CI only (<c>--ui-screenshots &lt;dir&gt;</c>): renders every page and the cast bar in both
    /// themes, with made-up sample terminals, so the layout can be reviewed without a LAN or a
    /// terminal. Never reached in normal use.</summary>
    internal void CaptureScreenshots(string directory)
    {
        Directory.CreateDirectory(directory);
        _listRefreshTimer.Stop();
        _discoveryClient.TerminalListChanged -= OnTerminalListChanged;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        ShowInTaskbar = false;
        Show();
        Pump();

        var samples = new[]
        {
            new TerminalListEntry(Guid.NewGuid(), "会议厅主控", new DiscoveredTerminal(Guid.NewGuid(), "会议厅主控", IPAddress.Parse("192.168.1.20"), DateTimeOffset.Now, 1, PairingHandshake.ProtocolVersion), null),
            new TerminalListEntry(Guid.NewGuid(), "二楼培训室", new DiscoveredTerminal(Guid.NewGuid(), "二楼培训室", IPAddress.Parse("192.168.1.31"), DateTimeOffset.Now, 1, PairingHandshake.ProtocolVersion), null),
            new TerminalListEntry(Guid.NewGuid(), "展厅 LED", null, DateTimeOffset.Now.AddDays(-12)),
        };

        foreach (var theme in new[] { CasterTheme.Light, CasterTheme.Dark })
        {
            _settings.Theme = theme;
            ModernUi.SetTheme(theme);
            UpdateThemeButton();
            ApplyThemeEverywhere();
            string prefix = $"caster-{theme.ToString().ToLowerInvariant()}";

            _refreshingList = true;
            _terminalListBox.Items.Clear();
            _refreshingList = false;
            ShowSelectedTerminal();
            Save(this, Path.Combine(directory, $"{prefix}-0-empty.png"));

            _refreshingList = true;
            foreach (var sample in samples) _terminalListBox.Items.Add(sample);
            _terminalListBox.SelectedIndex = 0;
            _refreshingList = false;
            ShowSelectedTerminal();
            Save(this, Path.Combine(directory, $"{prefix}-1-standby.png"));

            _pairingView.Begin("二楼培训室");
            _pairingView.SetCode("481207");
            ShowView(_pairingView);
            Save(this, Path.Combine(directory, $"{prefix}-2-pairing.png"));

            ShowProblem(ProblemForStartFailure("本机没有可用的 H.264 视频编码器，无法投屏。显卡硬件编码：没有找到；Windows 自带软件编码：没有找到。"));
            Save(this, Path.Combine(directory, $"{prefix}-4-problem.png"));

            _diagnosticsView.SetResults(new[]
            {
                new CheckResult("屏幕采集", CheckStatus.Pass, "2560×1440"),
                new CheckResult("视频编码", CheckStatus.Warn, "软件编码（没有显卡编码器，CPU 占用较高）"),
                new CheckResult("声音采集", CheckStatus.Pass, "48 kHz · 2 声道"),
                new CheckResult("声音编码", CheckStatus.Pass, "AAC"),
                new CheckResult("网络（本机回环）", CheckStatus.Pass, "正常"),
                new CheckResult("本地数据", CheckStatus.Pass, "正常"),
            });
            ShowView(_diagnosticsView);
            Save(this, Path.Combine(directory, $"{prefix}-5-diagnostics.png"));

            using (var bar = new CastBar("会议厅主控") { StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ShowInTaskbar = false })
            {
                bar.Show();
                bar.UpdateState(new CastBarState("12:34", CastHealth.Good, "流畅", "主屏 2560×1440", "约 80 ms", "正常", SoftwareEncoding: true,
                    "分辨率 2560×1440 · 码率档位 12 Mbps\n已采集 22140 帧 · 已发送 22140 个访问单元 / 1180 MB\n编码器：encoder=software cbr=ok bitrate=ok lowlatency=ok gop=ok bframes=ok"));
                Pump();
                Save(bar, Path.Combine(directory, $"{prefix}-3-castbar.png"));
                bar.ExpandForScreenshot();
                Pump();
                Save(bar, Path.Combine(directory, $"{prefix}-3-castbar-expanded.png"));
                bar.Hide();
            }
        }
    }

    private static void Pump()
    {
        for (int i = 0; i < 5; i++)
        {
            Application.DoEvents();
            Thread.Sleep(30);
        }
    }

    private static void Save(Control control, string path)
    {
        control.PerformLayout();
        Pump();
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        bitmap.Save(path, ImageFormat.Png);
    }
}
