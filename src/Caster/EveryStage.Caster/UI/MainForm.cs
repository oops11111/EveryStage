using EveryStage.Caster.Capture;
using EveryStage.Caster.Casting;
using EveryStage.Caster.Discovery;
using EveryStage.Caster.Encode;
using EveryStage.Discovery;
using EveryStage.Transport;

namespace EveryStage.Caster.UI;

/// <summary>
/// The Caster's whole UI (PLANNING.md §12): "单一任务导向，不做复杂功能堆叠" — a standby panel
/// (target terminal list + start button + privacy notice) and, once paired, a second panel that now
/// really streams: picking a terminal and pairing successfully immediately starts a real
/// <see cref="LiveCastSession"/> (capture -> NV12 -> H.264 -> RTP, sent to the Terminal). Below that,
/// three independent self-tests remain available as standalone diagnostics for isolating which stage
/// (capture, encode, or transport) is at fault if live casting misbehaves: screen capture
/// (<see cref="CaptureSelfTestRunner"/>), H.264 encoding (<see cref="EncodeSelfTestRunner"/>,
/// capture -> NV12 -> hardware encoder), and RTP transport (<see cref="TransportSelfTest"/>, a real
/// loopback UDP round-trip with synthetic NAL-shaped payloads). None of the three self-tests touch
/// the live cast session or each other. The "终端机确认" line in the paired panel is the Terminal's
/// own periodic status report (<see cref="LiveCastSession.IsTerminalAlive"/>) — see this project's
/// README for what that does and doesn't guarantee (it's a lightweight heartbeat, not per-packet
/// acknowledgment, and it can't distinguish "never confirmed" from "confirmed once, then the
/// Terminal went quiet").
///
/// The standby list is now a merge of two sources (PLANNING.md §12 "已配对直显"): terminals
/// <see cref="TerminalDiscoveryClient"/> currently sees beacons from (immediately actionable — a real
/// IP address is known), and terminals in <see cref="PairedTerminalStore"/> that paired successfully
/// before but aren't currently broadcasting (shown, marked offline, but not selectable — see
/// <see cref="TerminalListEntry"/>'s doc comment on why this class deliberately never caches an old
/// IP address to try anyway). A successful pairing upserts into the store from
/// <see cref="ShowPaired"/>, so the next time this Caster starts (or the Terminal temporarily drops
/// off beacon range and comes back), the entry is either already there offline or gets refreshed by
/// the next beacon. A "移除配对" button undoes that — <see cref="OnRemovePairingClick"/> deletes the
/// persisted record for whichever entry is selected (online or offline), so a terminal that will
/// never come back doesn't sit in the offline half of this list forever with no way to clear it.
/// </summary>
public sealed class MainForm : Form
{
    private readonly TerminalDiscoveryClient _discoveryClient;
    private readonly DeviceIdentity _identity;
    private readonly PairedTerminalStore _pairedTerminals;
    private readonly CaptureSelfTestRunner _captureSelfTest = new();
    private readonly EncodeSelfTestRunner _encodeSelfTest = new();
    private readonly System.Windows.Forms.Timer _listRefreshTimer;
    private readonly System.Windows.Forms.Timer _captureStatsTimer;
    private readonly System.Windows.Forms.Timer _encodeStatsTimer;
    private readonly System.Windows.Forms.Timer _liveCastStatsTimer;

    private readonly Panel _standbyPanel;
    private readonly ListBox _terminalListBox;
    private readonly Button _startButton;
    private readonly Button _removePairingButton;

    private readonly Panel _pairedPanel;
    private readonly Label _pairedWithLabel;
    private readonly Label _liveCastStatsLabel;
    private readonly Button _stopCastButton;
    private readonly Button _captureSelfTestButton;
    private readonly Label _captureStatsLabel;
    private readonly Button _encodeSelfTestButton;
    private readonly Label _encodeStatsLabel;
    private readonly Button _transportSelfTestButton;
    private readonly Label _transportStatsLabel;

    private DiscoveredTerminal? _pairedTerminal;
    private LiveCastSession? _liveCastSession;

    /// <summary>One row of the standby list — either a live, currently-reachable terminal
    /// (<see cref="Live"/> set, from a recent beacon) or a previously-paired terminal that isn't
    /// broadcasting right now (<see cref="Live"/> null). Deliberately carries no address for the
    /// offline case: see <see cref="Discovery.PairedTerminal"/>'s doc comment on why a cached address
    /// would be actively misleading rather than merely stale. <see cref="DisplayText"/> (not
    /// <see cref="DiscoveredTerminal"/>'s own <c>DeviceName</c>) is what <see cref="_terminalListBox"/>
    /// binds its <c>DisplayMember</c> to, so the "（离线）" suffix shows up without needing a custom
    /// <c>ListBox</c> item renderer.</summary>
    private sealed record TerminalListEntry(Guid DeviceId, string DeviceName, DiscoveredTerminal? Live)
    {
        public string DisplayText => Live != null ? DeviceName : $"{DeviceName}（离线）";
    }

    public MainForm(TerminalDiscoveryClient discoveryClient, DeviceIdentity identity, PairedTerminalStore pairedTerminals)
    {
        _discoveryClient = discoveryClient;
        _identity = identity;
        _pairedTerminals = pairedTerminals;

        Text = "EveryStage 投屏机";
        ClientSize = new Size(320, 506);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        // --- 待机态 (PLANNING.md §12) ---
        _terminalListBox = new ListBox
        {
            Bounds = new Rectangle(12, 12, 296, 160),
            DisplayMember = nameof(TerminalListEntry.DisplayText), // else ListBox shows the record's generated ToString().
        };
        // Only a live (online) entry has a real IP address to pair/cast to — an offline paired
        // entry is shown for visibility (PLANNING.md §12 "已配对直显") but can't be selected to
        // start anything until a fresh beacon from it turns it back into a live entry.
        _terminalListBox.SelectedIndexChanged += (_, _) =>
        {
            _startButton.Enabled = _terminalListBox.SelectedItem is TerminalListEntry { Live: not null };
            // Enabled for either an online or offline entry, as long as it actually has a
            // persisted record — a live entry from a terminal this Caster has never successfully
            // paired with (just currently broadcasting) has nothing to remove.
            _removePairingButton.Enabled = _terminalListBox.SelectedItem is TerminalListEntry entry
                && _pairedTerminals.Find(entry.DeviceId) != null;
        };

        var privacyLabel = new Label
        {
            Text = "点击\"开始投屏\"后，将投放整个屏幕（全屏捕获），而不是仅本窗口或某个应用。",
            ForeColor = Color.DimGray,
            Bounds = new Rectangle(12, 178, 296, 40),
        };

        _startButton = new Button
        {
            Text = "开始投屏",
            Enabled = false,
            Bounds = new Rectangle(12, 226, 296, 32),
        };
        _startButton.Click += OnStartButtonClick;

        _removePairingButton = new Button
        {
            Text = "移除配对",
            Enabled = false,
            Bounds = new Rectangle(12, 264, 296, 28),
        };
        _removePairingButton.Click += OnRemovePairingClick;

        _standbyPanel = new Panel { Dock = DockStyle.Fill };
        _standbyPanel.Controls.AddRange(new Control[] { _terminalListBox, privacyLabel, _startButton, _removePairingButton });

        // --- 投屏中态：现在是真的在投屏（见类doc comment），不再是占位符 ---
        _pairedWithLabel = new Label { Bounds = new Rectangle(12, 12, 296, 32) };
        _liveCastStatsLabel = new Label { Bounds = new Rectangle(12, 46, 296, 90), ForeColor = Color.DimGray };

        _stopCastButton = new Button { Text = "停止投屏", Bounds = new Rectangle(12, 140, 296, 32) };
        _stopCastButton.Click += (_, _) => ShowStandby();

        var diagnosticsNoteLabel = new Label
        {
            Text = "以下三个按钮各自独立、互不影响，是采集/编码/传输三个环节各自的自检工具，\n" +
                   "用来在投屏出问题时单独定位是哪一步——它们不会影响上面正在进行的投屏。",
            ForeColor = Color.DimGray,
            Bounds = new Rectangle(12, 184, 296, 40),
        };

        _captureSelfTestButton = new Button { Text = "开始屏幕捕获自检", Bounds = new Rectangle(12, 228, 296, 32) };
        _captureSelfTestButton.Click += OnCaptureSelfTestClick;
        _captureStatsLabel = new Label { Bounds = new Rectangle(12, 262, 296, 50), ForeColor = Color.DimGray };

        _encodeSelfTestButton = new Button { Text = "开始编码自检 (捕获→NV12→H.264)", Bounds = new Rectangle(12, 316, 296, 32) };
        _encodeSelfTestButton.Click += OnEncodeSelfTestClick;
        _encodeStatsLabel = new Label { Bounds = new Rectangle(12, 350, 296, 50), ForeColor = Color.DimGray };

        _transportSelfTestButton = new Button { Text = "运行传输自检 (本机回环)", Bounds = new Rectangle(12, 404, 296, 32) };
        _transportSelfTestButton.Click += OnTransportSelfTestClick;
        _transportStatsLabel = new Label { Bounds = new Rectangle(12, 438, 296, 40), ForeColor = Color.DimGray };

        _pairedPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        _pairedPanel.Controls.AddRange(new Control[]
        {
            _pairedWithLabel, _liveCastStatsLabel, _stopCastButton, diagnosticsNoteLabel,
            _captureSelfTestButton, _captureStatsLabel,
            _encodeSelfTestButton, _encodeStatsLabel, _transportSelfTestButton, _transportStatsLabel,
        });

        Controls.Add(_pairedPanel);
        Controls.Add(_standbyPanel);

        _listRefreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _listRefreshTimer.Tick += (_, _) => RefreshTerminalList();
        _listRefreshTimer.Start();

        _captureStatsTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _captureStatsTimer.Tick += (_, _) => RefreshCaptureStats();

        _encodeStatsTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _encodeStatsTimer.Tick += (_, _) => RefreshEncodeStats();

        _liveCastStatsTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _liveCastStatsTimer.Tick += (_, _) => RefreshLiveCastStats();
    }

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

        _encodeStatsLabel.ForeColor = Color.DimGray;
        _encodeStatsLabel.Text =
            $"已编码访问单元数: {_encodeSelfTest.AccessUnitsEncoded}\n" +
            $"编码总字节数: {_encodeSelfTest.TotalEncodedBytes}";
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
                ? $"通过：{result.NalUnitsSent} 个NAL单元全部往返一致（含FU-A分片重组）。"
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
            .Select(t => new TerminalListEntry(t.DeviceId, t.DeviceName, t))
            .Concat(_pairedTerminals.All
                .Where(p => !liveIds.Contains(p.DeviceId))
                .OrderBy(p => p.DeviceName)
                .Select(p => new TerminalListEntry(p.DeviceId, p.DeviceName, null)))
            .ToList();

        _terminalListBox.BeginUpdate();
        _terminalListBox.Items.Clear();
        foreach (var entry in entries) _terminalListBox.Items.Add(entry);
        if (selected != null)
        {
            var stillPresent = entries.FirstOrDefault(e => e.DeviceId == selected.DeviceId);
            if (stillPresent != null) _terminalListBox.SelectedItem = stillPresent;
        }
        _terminalListBox.EndUpdate();
    }

    private void OnRemovePairingClick(object? sender, EventArgs e)
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
        RefreshTerminalList();
    }

    private async void OnStartButtonClick(object? sender, EventArgs e)
    {
        // The offline half of TerminalListEntry.Live == null can't reach here in practice (the
        // SelectedIndexChanged handler above keeps _startButton disabled for it), but the pattern
        // match still guards against it directly rather than trusting that invariant blindly.
        if (_terminalListBox.SelectedItem is not TerminalListEntry { Live: not null } entry) return;
        var terminal = entry.Live!; // non-null per the pattern match above.

        _startButton.Enabled = false;
        _startButton.Text = "配对中...";
        try
        {
            var response = await _discoveryClient.RequestPairingAsync(terminal, _identity);
            if (response is { Accepted: true })
            {
                ShowPaired(terminal);
            }
            else
            {
                MessageBox.Show(this,
                    response == null ? "终端机未响应（超时）。" : $"终端机拒绝了配对请求：{response.Reason ?? "未说明原因"}",
                    "配对失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            // Re-checks Live (not just non-null) rather than reusing the `entry` this method
            // started with: RefreshTerminalList() runs on its own 1s timer independently of this
            // await, and could have rebuilt the list — replacing the selected item with a fresh
            // TerminalListEntry instance — while a pairing request was in flight (its 15s default
            // timeout is longer than a single refresh tick). If the selected terminal's beacon
            // happened to lapse during that wait, re-enabling the button without this check would
            // let the user immediately retry against what the list now shows as offline.
            _startButton.Text = "开始投屏";
            _startButton.Enabled = _terminalListBox.SelectedItem is TerminalListEntry { Live: not null };
        }
    }

    private void ShowPaired(DiscoveredTerminal terminal)
    {
        // Remembered here, not just when the standby list was last built — this is the one point
        // where a pairing is actually confirmed successful, which is the right moment to persist it
        // for next time's "已配对直显" list (PLANNING.md §12), independent of whether/when the
        // standby list next happens to refresh.
        _pairedTerminals.Upsert(new PairedTerminal(terminal.DeviceId, terminal.DeviceName, DateTimeOffset.Now));

        _pairedTerminal = terminal;
        _pairedWithLabel.Text = $"正在向 \"{terminal.DeviceName}\" 投屏...";
        _standbyPanel.Visible = false;
        _pairedPanel.Visible = true;

        _liveCastSession?.Dispose();
        _liveCastSession = new LiveCastSession(_discoveryClient, _identity, terminal);
        _liveCastSession.Start();
        _liveCastStatsTimer.Start();
        RefreshLiveCastStats();
    }

    private void RefreshLiveCastStats()
    {
        if (_liveCastSession == null) return;

        if (_liveCastSession.LastError != null)
        {
            _liveCastStatsLabel.ForeColor = Color.DarkRed;
            _liveCastStatsLabel.Text = $"投屏出错：{_liveCastSession.LastError}";
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
        // shouldn't pretend to.
        string terminalLine = _liveCastSession.IsTerminalAlive
            ? $"终端机确认: 已解码 {_liveCastSession.TerminalFramesDecoded} 帧" +
              (_liveCastSession.TerminalVideoError != null ? $"（终端机视频出错：{_liveCastSession.TerminalVideoError}）" : "") +
              (_liveCastSession.TerminalAudioError != null ? $"（终端机音频出错：{_liveCastSession.TerminalAudioError}）" : "")
            : "终端机确认: 未确认（尚未收到或已停止收到终端机的状态回报）";

        _liveCastStatsLabel.Text =
            $"分辨率: {_liveCastSession.Width}x{_liveCastSession.Height}\n" +
            $"已捕获帧数: {_liveCastSession.FramesCaptured}   已发送访问单元: {_liveCastSession.AccessUnitsSent}\n" +
            $"已发送字节数: {_liveCastSession.BytesSent}\n" +
            audioLine + "\n" +
            terminalLine;
    }

    private void ShowStandby()
    {
        _liveCastStatsTimer.Stop();
        _liveCastSession?.Stop();
        _liveCastSession?.Dispose();
        _liveCastSession = null;
        _liveCastStatsLabel.Text = "";

        if (_captureSelfTest.IsRunning)
        {
            _captureSelfTest.Stop();
            _captureStatsTimer.Stop();
            _captureSelfTestButton.Text = "开始屏幕捕获自检";
            _captureStatsLabel.Text = "";
        }
        if (_encodeSelfTest.IsRunning)
        {
            _encodeSelfTest.Stop();
            _encodeStatsTimer.Stop();
            _encodeSelfTestButton.Text = "开始编码自检 (捕获→NV12→H.264)";
            _encodeStatsLabel.Text = "";
        }
        _transportStatsLabel.Text = "";

        _pairedTerminal = null;
        _pairedPanel.Visible = false;
        _standbyPanel.Visible = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _listRefreshTimer.Dispose();
            _captureStatsTimer.Dispose();
            _captureSelfTest.Dispose();
            _encodeStatsTimer.Dispose();
            _encodeSelfTest.Dispose();
            _liveCastStatsTimer.Dispose();
            _liveCastSession?.Dispose();
        }
        base.Dispose(disposing);
    }
}
