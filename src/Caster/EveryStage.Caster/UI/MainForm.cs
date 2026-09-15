using EveryStage.Caster.Capture;
using EveryStage.Caster.Casting;
using EveryStage.Caster.Discovery;
using EveryStage.Caster.Encode;
using EveryStage.Discovery;
using EveryStage.Rendering;
using EveryStage.Transport;

namespace EveryStage.Caster.UI;

/// <summary>
/// The Caster's whole UI (PLANNING.md §12): "单一任务导向，不做复杂功能堆叠" — a standby panel
/// (target terminal list + start button + privacy notice) and, once paired, a second panel that now
/// really streams: picking a terminal and pairing successfully immediately starts a real
/// <see cref="LiveCastSession"/> (capture -> NV12 -> H.264 -> RTP, sent to the Terminal). Below that,
/// five independent self-tests remain available as standalone diagnostics for isolating which stage
/// (capture, encode, transport, audio capture, or AAC encode) is at fault if live casting misbehaves:
/// screen capture (<see cref="CaptureSelfTestRunner"/>), H.264 encoding (<see cref="EncodeSelfTestRunner"/>,
/// capture -> NV12 -> hardware encoder), RTP transport (<see cref="TransportSelfTest"/>, a real
/// loopback UDP round-trip with synthetic NAL-shaped payloads), audio capture
/// (<see cref="AudioCaptureSelfTestRunner"/>, added a round after the other three — see this
/// project's README on why WASAPI loopback capture had no independent self-test until now), and AAC
/// encoding (<see cref="AacEncodeSelfTestRunner"/>, capture -> <see cref="AacAudioEncoder"/> — this
/// repo's first audio-encoding MFT, not yet wired into the live cast session's own audio path, which
/// still sends uncompressed PCM; see this project's README). None of the five self-tests touch the
/// live cast session or each other — including the two audio-capturing ones (WASAPI loopback and AAC
/// encode) running concurrently with a live cast's own <c>AudioCaptureSource</c> and each other,
/// which this repo has never verified on a real machine but expects to work since WASAPI loopback
/// capture (unlike exclusive-mode rendering) is inherently a shared, read-only tap on the render
/// stream, not something one capture client can lock out another from. The "终端机确认" line in the
/// paired panel is the Terminal's
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
///
/// The "投屏中" panel now carries a persistent privacy reminder + elapsed-time line
/// (<see cref="_privacyReminderLabel"/>, PLANNING.md §12) for as long as a cast is running, not just
/// the one-time notice on the standby panel before "开始投屏" is clicked — a cast can run for a while,
/// and the person at this machine (not necessarily the same person who started it, if it's shared)
/// should have a standing, hard-to-miss reminder that the whole screen is being broadcast, not just a
/// notice they saw once before starting. The elapsed time itself is read from
/// <see cref="LiveCastSession.Elapsed"/> and is purely a UI display value — the same underlying
/// <c>Stopwatch</c> also drives A/V sync's RTP timestamps, but neither reads from the other.
///
/// The standby panel also now has a monitor picker (PLANNING.md §12 "选择捕获哪个显示器") —
/// <see cref="_monitorComboBox"/>, populated by <see cref="ScreenCaptureSource.EnumerateOutputs"/>
/// and re-enumerated every time this panel becomes visible again (<see cref="RefreshMonitorList"/>,
/// called from both the constructor and <see cref="ShowStandby"/>) rather than once ever — the exact
/// staleness mistake this project already made and fixed once for the Terminal's own monitor picker
/// (`SettingsPanel`'s "显示" tab). The selected <c>outputIndex</c> flows straight through
/// <see cref="ShowPaired"/> into <see cref="LiveCastSession"/>'s constructor and from there into
/// <see cref="ScreenCaptureSource"/>'s — previously that was hardcoded to 0 (whatever DXGI enumerates
/// first) with no UI to change it at all.
/// </summary>
public sealed class MainForm : Form
{
    private readonly TerminalDiscoveryClient _discoveryClient;
    private readonly DeviceIdentity _identity;
    private readonly PairedTerminalStore _pairedTerminals;
    private readonly CaptureSelfTestRunner _captureSelfTest = new();
    private readonly EncodeSelfTestRunner _encodeSelfTest = new();
    private readonly AudioCaptureSelfTestRunner _audioCaptureSelfTest = new();
    private readonly AacEncodeSelfTestRunner _aacEncodeSelfTest = new();
    private readonly System.Windows.Forms.Timer _listRefreshTimer;
    private readonly System.Windows.Forms.Timer _captureStatsTimer;
    private readonly System.Windows.Forms.Timer _encodeStatsTimer;
    private readonly System.Windows.Forms.Timer _audioCaptureStatsTimer;
    private readonly System.Windows.Forms.Timer _aacEncodeStatsTimer;
    private readonly System.Windows.Forms.Timer _liveCastStatsTimer;

    private readonly Panel _standbyPanel;
    private readonly ListBox _terminalListBox;
    private readonly Button _startButton;
    private readonly Button _removePairingButton;
    private readonly ComboBox _monitorComboBox;

    private readonly Panel _pairedPanel;
    private readonly Label _pairedWithLabel;
    private readonly Label _privacyReminderLabel;
    private readonly Label _liveCastStatsLabel;
    private readonly Button _stopCastButton;
    private readonly Button _captureSelfTestButton;
    private readonly Label _captureStatsLabel;
    private readonly Button _encodeSelfTestButton;
    private readonly Label _encodeStatsLabel;
    private readonly Button _transportSelfTestButton;
    private readonly Label _transportStatsLabel;
    private readonly Button _audioCaptureSelfTestButton;
    private readonly Label _audioCaptureStatsLabel;
    private readonly Button _aacEncodeSelfTestButton;
    private readonly Label _aacEncodeStatsLabel;

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

    /// <summary>One entry of the standby panel's monitor picker — <see cref="OutputIndex"/> is
    /// passed straight through to <see cref="LiveCastSession"/>'s own <c>outputIndex</c> constructor
    /// parameter, which passes it straight through to <see cref="ScreenCaptureSource"/>'s, so this
    /// never needs its own translation step between "what the picker shows" and "what capture
    /// actually uses" — same DXGI adapter-output index all the way through.</summary>
    private sealed record MonitorComboItem(int OutputIndex, string Label)
    {
        public override string ToString() => Label; // what the ComboBox actually renders.
    }

    public MainForm(TerminalDiscoveryClient discoveryClient, DeviceIdentity identity, PairedTerminalStore pairedTerminals)
    {
        _discoveryClient = discoveryClient;
        _identity = identity;
        _pairedTerminals = pairedTerminals;

        Text = "EveryStage 投屏机";
        // Grown from an original 506: first to 600 to fit a fourth self-test section (audio
        // capture) below the existing capture/encode/transport three, then to 630 to give
        // _liveCastStatsLabel enough extra height for its new always-visible "确认≠健康" caveat
        // line, then to 718 to fit a fifth self-test section (AAC encode) below the audio capture
        // one — see this class's doc comment and _liveCastStatsLabel's own Bounds comment below.
        ClientSize = new Size(320, 718);
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

        // PLANNING.md §12"选择捕获哪个显示器"——之前ScreenCaptureSource固定捕获outputIndex=0，
        // 多显示器场景完全没有UI选择。RefreshMonitorList()（下面）在构造函数末尾和每次回到待机态
        // 时都会重新枚举，跟这一轮刚修过的SettingsPanel显示器列表是同一个"别只枚举一次"教训。
        var monitorLabel = new Label { Text = "选择要投放的显示器：", AutoSize = true, Bounds = new Rectangle(12, 222, 296, 18) };
        _monitorComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(12, 242, 296, 24) };

        _startButton = new Button
        {
            Text = "开始投屏",
            Enabled = false,
            Bounds = new Rectangle(12, 274, 296, 32),
        };
        _startButton.Click += OnStartButtonClick;

        _removePairingButton = new Button
        {
            Text = "移除配对",
            Enabled = false,
            Bounds = new Rectangle(12, 312, 296, 28),
        };
        _removePairingButton.Click += OnRemovePairingClick;

        _standbyPanel = new Panel { Dock = DockStyle.Fill };
        _standbyPanel.Controls.AddRange(new Control[]
        {
            _terminalListBox, privacyLabel, monitorLabel, _monitorComboBox, _startButton, _removePairingButton,
        });
        RefreshMonitorList();

        // --- 投屏中态：现在是真的在投屏（见类doc comment），不再是占位符 ---
        _pairedWithLabel = new Label { Bounds = new Rectangle(12, 12, 296, 32) };

        // PLANNING.md §12 "投屏中" 状态里的隐私提醒条 + 时长显示——之前只有开始投屏前那条一次性的
        // privacyLabel，投屏过程中完全没有任何持续提醒或计时，这两个都是这次新加的。
        _privacyReminderLabel = new Label
        {
            Text = "⚠ 正在投放整个屏幕｜已投屏时长: 00:00:00",
            ForeColor = Color.DarkRed,
            Bounds = new Rectangle(12, 44, 296, 20),
        };

        // Height grown from 90 to 108 (+18) to fit RefreshLiveCastStats' new always-visible
        // "（仅代表状态通道送达...）" caveat line without clipping the 5 lines already packed in
        // here — every control below this one shifted down by that same 18px.
        _liveCastStatsLabel = new Label { Bounds = new Rectangle(12, 70, 296, 108), ForeColor = Color.DimGray };

        _stopCastButton = new Button { Text = "停止投屏", Bounds = new Rectangle(12, 182, 296, 32) };
        _stopCastButton.Click += (_, _) => ShowStandby();

        var diagnosticsNoteLabel = new Label
        {
            Text = "以下五个按钮各自独立、互不影响，是采集/编码/传输/音频采集/AAC音频编码各环节各自的\n" +
                   "自检工具，用来在投屏出问题时单独定位是哪一步——它们不会影响上面正在进行的投屏。",
            ForeColor = Color.DimGray,
            Bounds = new Rectangle(12, 226, 296, 40),
        };

        _captureSelfTestButton = new Button { Text = "开始屏幕捕获自检", Bounds = new Rectangle(12, 270, 296, 32) };
        _captureSelfTestButton.Click += OnCaptureSelfTestClick;
        _captureStatsLabel = new Label { Bounds = new Rectangle(12, 304, 296, 50), ForeColor = Color.DimGray };

        _encodeSelfTestButton = new Button { Text = "开始编码自检 (捕获→NV12→H.264)", Bounds = new Rectangle(12, 358, 296, 32) };
        _encodeSelfTestButton.Click += OnEncodeSelfTestClick;
        _encodeStatsLabel = new Label { Bounds = new Rectangle(12, 392, 296, 50), ForeColor = Color.DimGray };

        _transportSelfTestButton = new Button { Text = "运行传输自检 (本机回环)", Bounds = new Rectangle(12, 446, 296, 32) };
        _transportSelfTestButton.Click += OnTransportSelfTestClick;
        _transportStatsLabel = new Label { Bounds = new Rectangle(12, 480, 296, 40), ForeColor = Color.DimGray };

        _audioCaptureSelfTestButton = new Button { Text = "开始音频采集自检 (WASAPI loopback)", Bounds = new Rectangle(12, 524, 296, 32) };
        _audioCaptureSelfTestButton.Click += OnAudioCaptureSelfTestClick;
        _audioCaptureStatsLabel = new Label { Bounds = new Rectangle(12, 558, 296, 50), ForeColor = Color.DimGray };

        // AacAudioEncoder/AacEncodeSelfTestRunner (see their own doc comments) — this repo's first
        // audio-encoding MFT, not yet wired into LiveCastSession's own audio path (which still sends
        // uncompressed PCM), so this self-test is currently the only way to exercise it at all.
        _aacEncodeSelfTestButton = new Button { Text = "开始AAC编码自检 (WASAPI loopback→AAC)", Bounds = new Rectangle(12, 612, 296, 32) };
        _aacEncodeSelfTestButton.Click += OnAacEncodeSelfTestClick;
        _aacEncodeStatsLabel = new Label { Bounds = new Rectangle(12, 646, 296, 50), ForeColor = Color.DimGray };

        _pairedPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        _pairedPanel.Controls.AddRange(new Control[]
        {
            _pairedWithLabel, _privacyReminderLabel, _liveCastStatsLabel, _stopCastButton, diagnosticsNoteLabel,
            _captureSelfTestButton, _captureStatsLabel,
            _encodeSelfTestButton, _encodeStatsLabel, _transportSelfTestButton, _transportStatsLabel,
            _audioCaptureSelfTestButton, _audioCaptureStatsLabel,
            _aacEncodeSelfTestButton, _aacEncodeStatsLabel,
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

        _audioCaptureStatsTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _audioCaptureStatsTimer.Tick += (_, _) => RefreshAudioCaptureStats();

        _aacEncodeStatsTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _aacEncodeStatsTimer.Tick += (_, _) => RefreshAacEncodeStats();

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

        // Same "only shown once non-zero" convention as RefreshLiveCastStats's backpressure lines.
        string droppedLine = _encodeSelfTest.FramesDroppedForBackpressure > 0
            ? $"\n⚠ 因编码器跟不上采集而丢弃: {_encodeSelfTest.FramesDroppedForBackpressure} 帧"
            : "";

        _encodeStatsLabel.ForeColor = Color.DimGray;
        _encodeStatsLabel.Text =
            $"已编码访问单元数: {_encodeSelfTest.AccessUnitsEncoded}\n" +
            $"编码总字节数: {_encodeSelfTest.TotalEncodedBytes}" +
            droppedLine;
    }

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
            _aacEncodeSelfTestButton.Text = "开始AAC编码自检 (WASAPI loopback→AAC)";
            _aacEncodeStatsLabel.Text = "";
        }
        else
        {
            _aacEncodeSelfTest.Start();
            _aacEncodeStatsTimer.Start();
            _aacEncodeSelfTestButton.Text = "停止AAC编码自检";
            RefreshAacEncodeStats();
        }
    }

    private void RefreshAacEncodeStats()
    {
        if (_aacEncodeSelfTest.LastError != null)
        {
            _aacEncodeStatsLabel.ForeColor = Color.DarkRed;
            _aacEncodeStatsLabel.Text = $"AAC编码出错：{_aacEncodeSelfTest.LastError}";
            _aacEncodeStatsTimer.Stop();
            _aacEncodeSelfTestButton.Text = "开始AAC编码自检 (WASAPI loopback→AAC)";
            return;
        }

        _aacEncodeStatsLabel.ForeColor = Color.DimGray;
        _aacEncodeStatsLabel.Text =
            $"采样率: {_aacEncodeSelfTest.SampleRate}Hz   声道数: {_aacEncodeSelfTest.Channels}\n" +
            $"PCM输入字节数: {_aacEncodeSelfTest.TotalPcmBytesIn}\n" +
            $"已编码访问单元数: {_aacEncodeSelfTest.AccessUnitsEncoded}   编码总字节数: {_aacEncodeSelfTest.TotalEncodedBytes}";
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

    /// <summary>Re-enumerates <see cref="ScreenCaptureSource.EnumerateOutputs"/> into
    /// <see cref="_monitorComboBox"/>, preserving the current selection by
    /// <see cref="MonitorComboItem.OutputIndex"/> (not list position) if that output still exists —
    /// same reasoning <c>SettingsPanel.Refresh_()</c> on the Terminal side just applied to its own
    /// monitor list: re-populating around the last-saved/last-picked value would silently reset an
    /// in-progress choice, and a monitor that's genuinely gone should fall back rather than stay
    /// selected. Called once at construction and again from <see cref="ShowStandby"/>, not just
    /// once ever — the exact staleness mistake this project's README already flagged and fixed for
    /// the Terminal's own monitor picker.</summary>
    private void RefreshMonitorList()
    {
        int? previousSelection = (_monitorComboBox.SelectedItem as MonitorComboItem)?.OutputIndex;

        IReadOnlyList<ScreenCaptureSource.MonitorCaptureOption> outputs;
        try
        {
            using var gpu = new D3D11Device();
            outputs = ScreenCaptureSource.EnumerateOutputs(gpu);
        }
        catch (Exception ex)
        {
            // Same "don't let a diagnostic/setup step take the whole form down" reasoning the self-
            // test runners already apply to their own D3D11Device construction — if even a
            // throwaway device can't be created here, "开始投屏" is going to fail the identical way,
            // so there's nothing extra to gain from surfacing a separate error dialog right now.
            _monitorComboBox.Items.Clear();
            _monitorComboBox.Items.Add(new MonitorComboItem(0, $"默认显示器（枚举失败：{ex.Message}）"));
            _monitorComboBox.SelectedIndex = 0;
            return;
        }

        _monitorComboBox.BeginUpdate();
        _monitorComboBox.Items.Clear();
        foreach (var output in outputs)
        {
            string label = $"显示器 {output.OutputIndex}: {output.DeviceName} " +
                           $"({output.Bounds.Width}x{output.Bounds.Height} @ {output.Bounds.X},{output.Bounds.Y})";
            _monitorComboBox.Items.Add(new MonitorComboItem(output.OutputIndex, label));
        }

        if (_monitorComboBox.Items.Count == 0)
        {
            // Shouldn't happen on any real machine (there's always at least one display) but a
            // combo box with nothing selectable would leave "开始投屏" silently falling back to
            // ScreenCaptureSource's own outputIndex default without the UI ever showing that choice.
            _monitorComboBox.Items.Add(new MonitorComboItem(0, "默认显示器"));
        }

        int selectedIndex = 0;
        for (int i = 0; i < _monitorComboBox.Items.Count; i++)
        {
            if (((MonitorComboItem)_monitorComboBox.Items[i]!).OutputIndex == previousSelection)
            {
                selectedIndex = i;
                break;
            }
        }
        _monitorComboBox.SelectedIndex = selectedIndex;
        _monitorComboBox.EndUpdate();
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
        // Captured now rather than read again inside ShowPaired — the combo box only ever refreshes
        // from ShowStandby()/construction (not on a timer, unlike _terminalListBox), so there's no
        // real race to guard against here, but reading it once at the point of the user's actual
        // click is still the clearer place to fix "what they asked for" for this cast.
        int outputIndex = (_monitorComboBox.SelectedItem as MonitorComboItem)?.OutputIndex ?? 0;

        _startButton.Enabled = false;
        _startButton.Text = "配对中...";
        try
        {
            var response = await _discoveryClient.RequestPairingAsync(terminal, _identity);
            if (response is { Accepted: true })
            {
                ShowPaired(terminal, outputIndex);
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

    private void ShowPaired(DiscoveredTerminal terminal, int outputIndex)
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
        _liveCastSession = new LiveCastSession(_discoveryClient, _identity, terminal, outputIndex);
        _liveCastSession.Start();
        _liveCastStatsTimer.Start();
        RefreshLiveCastStats();
    }

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
        _privacyReminderLabel.Text =
            $"⚠ 正在投放整个屏幕｜已投屏时长: {(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

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
        // shouldn't pretend to. This "确认" is specifically the discovery-socket status channel's
        // health, not the RTP media streams' — they travel independently (see this project's README
        // "已知风险" on why a healthy media stream and a healthy status channel aren't the same
        // thing), so this line can say "确认" while video/audio are actually struggling, or vice
        // versa; the "延迟估算" figure below is likewise only meaningful if this machine's and the
        // Terminal's clocks are reasonably in sync, which this protocol never verifies.
        string latencyNote = _liveCastSession.LastStatusLatencyEstimate is { } latency
            ? $"，延迟估算: {latency.TotalMilliseconds:F0}ms"
            : "";
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
        string terminalLine = _liveCastSession.IsTerminalAlive
            ? $"终端机确认: 已解码 {_liveCastSession.TerminalFramesDecoded} 帧{latencyNote}" +
              (_liveCastSession.TerminalVideoError != null ? $"（终端机视频出错：{_liveCastSession.TerminalVideoError}）" : "") +
              (_liveCastSession.TerminalAudioError != null ? $"（终端机音频出错：{_liveCastSession.TerminalAudioError}）" : "") +
              "\n（仅代表状态通道送达，不代表画面/声音本身一定在正常播放）"
            : "终端机确认: 未确认（尚未收到或已停止收到终端机的状态回报）";

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

        _liveCastStatsLabel.Text =
            $"分辨率: {_liveCastSession.Width}x{_liveCastSession.Height}\n" +
            $"已捕获帧数: {_liveCastSession.FramesCaptured}   已发送访问单元: {_liveCastSession.AccessUnitsSent}\n" +
            $"已发送字节数: {_liveCastSession.BytesSent}\n" +
            audioLine + "\n" +
            terminalLine +
            backpressureLine +
            encoderBackpressureLine;
    }

    private void ShowStandby()
    {
        _liveCastStatsTimer.Stop();
        _liveCastSession?.Stop();
        _liveCastSession?.Dispose();
        _liveCastSession = null;
        _liveCastStatsLabel.Text = "";
        _privacyReminderLabel.Text = "⚠ 正在投放整个屏幕｜已投屏时长: 00:00:00";

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
        if (_audioCaptureSelfTest.IsRunning)
        {
            _audioCaptureSelfTest.Stop();
            _audioCaptureStatsTimer.Stop();
            _audioCaptureSelfTestButton.Text = "开始音频采集自检 (WASAPI loopback)";
            _audioCaptureStatsLabel.Text = "";
        }
        if (_aacEncodeSelfTest.IsRunning)
        {
            _aacEncodeSelfTest.Stop();
            _aacEncodeStatsTimer.Stop();
            _aacEncodeSelfTestButton.Text = "开始AAC编码自检 (WASAPI loopback→AAC)";
            _aacEncodeStatsLabel.Text = "";
        }
        _transportStatsLabel.Text = "";

        _pairedTerminal = null;
        _pairedPanel.Visible = false;
        _standbyPanel.Visible = true;

        // Same "must not go stale for the whole app lifetime" reasoning as RefreshMonitorList's own
        // doc comment — re-enumerate every time standby becomes visible again, not just once at
        // startup, so a monitor unplugged/replugged while a cast was running shows up correctly.
        RefreshMonitorList();
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
            _audioCaptureStatsTimer.Dispose();
            _audioCaptureSelfTest.Dispose();
            _aacEncodeStatsTimer.Dispose();
            _aacEncodeSelfTest.Dispose();
            _liveCastStatsTimer.Dispose();
            _liveCastSession?.Dispose();
        }
        base.Dispose(disposing);
    }
}
