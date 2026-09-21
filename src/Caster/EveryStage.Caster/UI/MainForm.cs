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
/// nine independent self-tests remain available as standalone diagnostics for isolating which stage
/// (capture, encode, video transport, audio capture, AAC encode/decode, audio transport, the
/// discovery/pairing wire format, the paired-terminal list's own on-disk persistence, or the device
/// identity's own on-disk persistence) is at fault if live casting misbehaves: screen capture
/// (<see cref="CaptureSelfTestRunner"/>), H.264 encoding (<see cref="EncodeSelfTestRunner"/>, capture
/// -> NV12 -> hardware encoder), RTP transport (<see cref="TransportSelfTest"/>, a real loopback UDP
/// round-trip with synthetic NAL-shaped payloads), audio capture
/// (<see cref="AudioCaptureSelfTestRunner"/>, added a round after the other three — see this
/// project's README on why WASAPI loopback capture had no independent self-test until now), AAC
/// encode/decode (<see cref="AacEncodeSelfTestRunner"/>, capture -> <see cref="AacAudioEncoder"/> ->
/// <see cref="EveryStage.Rendering.Decode.AacAudioDecoder"/> — this repo's first audio-encoding *and*
/// decoding MFTs, chained into a full in-process round trip; the live cast session's own audio path
/// has since been switched over to the same codec, see this project's README), raw/audio RTP
/// transport (<see cref="RawTransportSelfTest"/>, <see cref="TransportSelfTest"/>'s counterpart for
/// <see cref="RtpSession.SendRawPayloadAsync"/>/<see cref="RawRtpReceiver"/> — the path audio
/// actually uses, with no NAL/FU-A framing), and the discovery protocol's own wire format
/// (<see cref="DiscoveryProtocolSelfTest"/>, a loopback UDP round-trip through
/// <see cref="DiscoveryProtocol.Encode"/>/<see cref="DiscoveryProtocol.Decode"/> for every message
/// type this protocol defines — unlike the six before it, this one has nothing to do with the live
/// capture/encode/transport pipeline at all; it exists because <c>EveryStage.Discovery</c>'s JSON
/// wire format had never been executed even once before this round, only reasoned about), and the
/// paired-terminal list's own on-disk persistence (<see cref="PairedTerminalStoreSelfTest"/>, a
/// save/reload/remove/corrupt-file round trip against a temp directory), and the device identity's
/// own on-disk persistence (<see cref="DeviceIdentitySelfTest"/>, the same save/reload/corrupt-file/
/// locked-file shape applied to <see cref="DeviceIdentity"/> instead — the shared class both Terminal
/// and Caster call <c>LoadOrCreate</c> on). These last two are the only ones of these nine that need
/// no GPU/network/audio hardware at all, only standard .NET file I/O; still never actually executed
/// here either, since this sandbox has no `dotnet` runtime regardless — see this project's README.
/// None of the nine self-tests touch the live cast session or each other — including the two
/// audio-capturing ones (WASAPI loopback and AAC encode/decode) running
/// concurrently with a live cast's own <c>AudioCaptureSource</c> and each other,
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
public sealed class MainForm : GradientForm
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
    private readonly Label _elapsedLabel;
    private readonly Label _qualityLabel;
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
    private readonly Button _rawTransportSelfTestButton;
    private readonly Label _rawTransportStatsLabel;
    private readonly Button _discoverySelfTestButton;
    private readonly Label _discoveryStatsLabel;
    private readonly Button _pairedTerminalStoreSelfTestButton;
    private readonly Label _pairedTerminalStoreStatsLabel;
    private readonly Button _deviceIdentitySelfTestButton;
    private readonly Label _deviceIdentityStatsLabel;

    private DiscoveredTerminal? _pairedTerminal;
    private LiveCastSession? _liveCastSession;

    /// <summary>One row of the standby list — either a live, currently-reachable terminal
    /// (<see cref="Live"/> set, from a recent beacon) or a previously-paired terminal that isn't
    /// broadcasting right now (<see cref="Live"/> null). Deliberately carries no address for the
    /// offline case: see <see cref="Discovery.PairedTerminal"/>'s doc comment on why a cached address
    /// would be actively misleading rather than merely stale. <see cref="DisplayText"/> (not
    /// <see cref="DiscoveredTerminal"/>'s own <c>DeviceName</c>) is what <see cref="_terminalListBox"/>
    /// binds its <c>DisplayMember</c> to, so the "（离线）" suffix shows up without needing a custom
    /// <c>ListBox</c> item renderer.
    ///
    /// <see cref="PairedAt"/> (<see cref="Discovery.PairedTerminal.PairedAt"/> — null for a live
    /// terminal this Caster has never actually paired with) previously had no reader anywhere: it
    /// was captured at pairing time and then just sat in <c>PairedTerminalStore</c>'s persisted JSON.
    /// Surfacing it here answers the one question the offline half of this list can't otherwise
    /// answer at a glance — "is this a terminal I paired with five minutes ago, or one I haven't
    /// seen in months and might as well remove?" — without needing a full multi-column
    /// <c>ListView</c> the way Terminal's own <c>DevicesPanel</c> uses for the same data.</summary>
    private sealed record TerminalListEntry(Guid DeviceId, string DeviceName, DiscoveredTerminal? Live, DateTimeOffset? PairedAt)
    {
        public string DisplayText =>
            (Live != null ? DeviceName : $"{DeviceName}（离线）")
            + (PairedAt is { } pairedAt ? $" · 配对于{pairedAt.LocalDateTime:yyyy-MM-dd}" : "");
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

        Text = "EveryStage Caster";
        // Grown from an original 506: first to 600 to fit a fourth self-test section (audio
        // capture) below the existing capture/encode/transport three, then to 630 to give
        // _liveCastStatsLabel enough extra height for its new always-visible "确认≠健康" caveat
        // line, then to 718 to fit a fifth self-test section (AAC encode) below the audio capture
        // one, then to 733 when that fifth section's stats label grew a 4th line once
        // AacAudioDecoder joined the round trip, then to 813 to fit a sixth section (raw/audio RTP
        // transport self-test, see this class's doc comment and _rawTransportStatsLabel's own
        // Bounds comment below) below the AAC one, then to 883 to fit a seventh section
        // (EveryStage.Discovery's own wire-format self-test) below the raw transport one, then to 963
        // to fit an eighth section (PairedTerminalStore's own on-disk round-trip self-test) below the
        // discovery one, then to 1043 to fit a ninth section (DeviceIdentity's own on-disk round-trip
        // self-test — the same shared EveryStage.Discovery class both Terminal and Caster call
        // LoadOrCreate() on) below the paired-terminal-store one — see this class's doc comment.
        ClientSize = new Size(520, 620);
        MinimumSize = new Size(500, 600);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        WindowsAppearance.UseDarkTitleBar(this);

        // --- 待机态 (PLANNING.md §12) ---
        _terminalListBox = new ListBox
        {
            Bounds = new Rectangle(24, 104, 472, 190),
            DisplayMember = nameof(TerminalListEntry.DisplayText), // else ListBox shows the record's generated ToString().
            Font = new Font("Segoe UI", 11F),
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 84,
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
        };
        _terminalListBox.DrawItem += DrawTerminalCard;
        // Only a live (online) entry has a real IP address to pair/cast to — an offline paired
        // entry is shown for visibility (PLANNING.md §12 "已配对直显") but can't be selected to
        // start anything until a fresh beacon from it turns it back into a live entry.
        _terminalListBox.SelectedIndexChanged += (_, _) =>
        {
            _startButton!.Enabled = _terminalListBox.SelectedItem is TerminalListEntry { Live: not null };
            // Enabled for either an online or offline entry, as long as it actually has a
            // persisted record — a live entry from a terminal this Caster has never successfully
            // paired with (just currently broadcasting) has nothing to remove.
            _removePairingButton!.Enabled = _terminalListBox.SelectedItem is TerminalListEntry entry
                && _pairedTerminals.Find(entry.DeviceId) != null;
        };

        var privacyLabel = new Label
        {
            Text = "点击\"开始投屏\"后，将投放整个屏幕（全屏捕获），而不是仅本窗口或某个应用。",
            ForeColor = Color.DimGray,
            Bounds = new Rectangle(24, 536, 472, 34),
        };

        // PLANNING.md §12"选择捕获哪个显示器"——之前ScreenCaptureSource固定捕获outputIndex=0，
        // 多显示器场景完全没有UI选择。RefreshMonitorList()（下面）在构造函数末尾和每次回到待机态
        // 时都会重新枚举，跟这一轮刚修过的SettingsPanel显示器列表是同一个"别只枚举一次"教训。
        var standbyTitle = new Label { Text = "投屏器 · 待机", Font = new Font("Segoe UI Semibold", 22F), AutoSize = true, Location = new Point(24, 28) };
        var connected = new Label { Text = "●  已连接", ForeColor = ModernUi.Success, AutoSize = true, Location = new Point(402, 42) };
        var targetLabel = new Label { Text = "选择终端", ForeColor = ModernUi.Muted, AutoSize = true, Location = new Point(24, 80) };
        var monitorLabel = new Label { Text = "显示器", ForeColor = ModernUi.Muted, AutoSize = true, Bounds = new Rectangle(24, 316, 472, 22) };
        _monitorComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(24, 342, 472, 34) };

        _startButton = new Button
        {
            Text = "开始投屏",
            Enabled = false,
            Bounds = new Rectangle(24, 400, 472, 50),
        };
        _startButton.Click += OnStartButtonClick;
        ModernUi.Primary(_startButton);

        _removePairingButton = new Button
        {
            Text = "移除配对",
            Enabled = false,
            Bounds = new Rectangle(24, 466, 472, 34),
        };
        _removePairingButton.Click += OnRemovePairingClick;

        _standbyPanel = new GlassPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(8), CornerRadius = 20,
            GlassTint = Color.FromArgb(190, 12, 31, 52),
        };
        _standbyPanel.Controls.AddRange(new Control[]
        {
            standbyTitle, connected, targetLabel, _terminalListBox, privacyLabel, monitorLabel,
            _monitorComboBox, _startButton, _removePairingButton,
        });
        RefreshMonitorList();

        // --- 投屏中态：现在是真的在投屏（见类doc comment），不再是占位符 ---
        _pairedWithLabel = new Label { Bounds = new Rectangle(82, 18, 350, 34), Font = new Font("Segoe UI Semibold", 16F) };
        _elapsedLabel = new Label
        {
            Text = "00:00", Bounds = new Rectangle(82, 48, 330, 60),
            Font = new Font("Segoe UI Semibold", 31F), ForeColor = Color.FromArgb(102, 163, 255),
        };
        _qualityLabel = new Label
        {
            Text = "●  正在建立连接…", Bounds = new Rectangle(24, 118, 420, 30),
            Font = new Font("Segoe UI", 10F), ForeColor = ModernUi.Success,
        };
        var liveCard = new GlassPanel
        {
            Bounds = new Rectangle(24, 82, 472, 166), CornerRadius = 16,
            GlassTint = Color.FromArgb(215, 20, 39, 62),
        };
        liveCard.Controls.AddRange(new Control[]
        {
            new Label
            {
                Text = "▣", TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Symbol", 34F), ForeColor = Color.FromArgb(184, 205, 239),
                Bounds = new Rectangle(20, 24, 54, 72),
            },
            _pairedWithLabel, _elapsedLabel, _qualityLabel,
        });

        // PLANNING.md §12 "投屏中" 状态里的隐私提醒条 + 时长显示——之前只有开始投屏前那条一次性的
        // privacyLabel，投屏过程中完全没有任何持续提醒或计时，这两个都是这次新加的。
        _privacyReminderLabel = new Label
        {
            Text = "♢  当前正在共享所选显示器",
            ForeColor = ModernUi.Warning,
            Bounds = new Rectangle(20, 14, 430, 34),
        };
        var privacyCard = new GlassPanel
        {
            Bounds = new Rectangle(24, 264, 472, 62), CornerRadius = 14,
            GlassTint = Color.FromArgb(215, 66, 49, 24),
        };
        privacyCard.Controls.Add(_privacyReminderLabel);

        // Height grown from 90 to 108 (+18) to fit RefreshLiveCastStats' new always-visible
        // "（仅代表状态通道送达...）" caveat line without clipping the 5 lines already packed in
        // here — every control below this one shifted down by that same 18px.
        _liveCastStatsLabel = new Label { Bounds = new Rectangle(12, 38, 430, 120), ForeColor = ModernUi.Muted };

        _stopCastButton = new Button { Text = "停止投屏", Bounds = new Rectangle(24, 344, 472, 50) };
        _stopCastButton.Click += (_, _) => ShowStandby();
        ModernUi.Primary(_stopCastButton, danger: true);

        var castingTitle = new Label { Text = "投屏器 · 投屏中", Font = new Font("Segoe UI Semibold", 22F), AutoSize = true, Location = new Point(24, 28) };
        var liveState = new Label { Text = "●  正在投屏", ForeColor = ModernUi.Danger, AutoSize = true, Location = new Point(392, 42) };

        var diagnosticsNoteLabel = new Label
        {
            Text = "以下九个按钮各自独立、互不影响，是采集/编码/传输/音频/AAC编码/音频传输/发现协议/两项持久化\n" +
                   "各环节各自的自检工具，用来在投屏出问题时单独定位是哪一步——不会影响正在进行的投屏。",
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
        // audio-encoding MFT. LiveCastSession's own audio path has since been switched over to real
        // AAC encode/decode too (see this project's README), so this self-test is no longer the only
        // way to exercise the codec, but it remains useful as an isolated check that doesn't require
        // an active cast/paired Terminal to run.
        _aacEncodeSelfTestButton = new Button { Text = "开始AAC编解码自检 (WASAPI loopback→AAC→PCM)", Bounds = new Rectangle(12, 612, 296, 32) };
        _aacEncodeSelfTestButton.Click += OnAacEncodeSelfTestClick;
        // Height 65 (not the usual 50 the other stats labels use) — this one now packs 4 lines
        // (RefreshAacEncodeStats grew a "解码回PCM字节数" line once AacAudioDecoder joined the
        // round trip), one more than the 3-line labels elsewhere in this panel.
        _aacEncodeStatsLabel = new Label { Bounds = new Rectangle(12, 646, 296, 65), ForeColor = Color.DimGray };

        // RawTransportSelfTest (see its own doc comment) — TransportSelfTest's counterpart for the
        // raw-payload RTP path (audio) instead of the H.264/NAL path; this project's
        // EveryStage.Transport README used to flag that path as having no automated verification of
        // its own, only reasoned about by analogy to the already-verified video path.
        _rawTransportSelfTestButton = new Button { Text = "运行音频传输自检 (Raw RTP, 本机回环)", Bounds = new Rectangle(12, 715, 296, 32) };
        _rawTransportSelfTestButton.Click += OnRawTransportSelfTestClick;
        _rawTransportStatsLabel = new Label { Bounds = new Rectangle(12, 751, 296, 40), ForeColor = Color.DimGray };

        // EveryStage.Discovery.DiscoveryProtocolSelfTest (see its own doc comment) — this library's
        // README风险第1条一直说"全部内容都没有在真实网络环境验证过"，这次给协议本身的编解码/UDP
        // 往返补上第一个真正跑得起来的检查，跟上面几个自检同一个"独立按钮、互不影响"的精神。
        _discoverySelfTestButton = new Button { Text = "运行发现协议自检 (本机回环)", Bounds = new Rectangle(12, 795, 296, 32) };
        _discoverySelfTestButton.Click += OnDiscoverySelfTestClick;
        _discoveryStatsLabel = new Label { Bounds = new Rectangle(12, 831, 296, 40), ForeColor = Color.DimGray };

        // PairedTerminalStoreSelfTest (see its own doc comment) — unlike the first six, this one
        // isn't reasoned-about-but-never-executed the same way: it only needs standard .NET file I/O,
        // no GPU/network/audio hardware, though this sandbox still has no dotnet runtime to actually
        // run it with either (see this project's README).
        _pairedTerminalStoreSelfTestButton = new Button { Text = "运行配对列表持久化自检 (临时目录)", Bounds = new Rectangle(12, 875, 296, 32) };
        _pairedTerminalStoreSelfTestButton.Click += OnPairedTerminalStoreSelfTestClick;
        _pairedTerminalStoreStatsLabel = new Label { Bounds = new Rectangle(12, 911, 296, 40), ForeColor = Color.DimGray };

        // DeviceIdentitySelfTest (see its own doc comment) — same "only needs standard .NET file
        // I/O" category as PairedTerminalStoreSelfTest just above, applied to the OTHER shared
        // JSON-persistence class in this codebase (EveryStage.Discovery.DeviceIdentity, used by both
        // Terminal and Caster) rather than duplicating that reasoning here.
        _deviceIdentitySelfTestButton = new Button { Text = "运行设备身份持久化自检 (临时目录)", Bounds = new Rectangle(12, 955, 296, 32) };
        _deviceIdentitySelfTestButton.Click += OnDeviceIdentitySelfTestClick;
        _deviceIdentityStatsLabel = new Label { Bounds = new Rectangle(12, 991, 296, 40), ForeColor = Color.DimGray };

        var diagnosticsToggle = new Button { Text = "诊断工具  ▾", Bounds = new Rectangle(24, 412, 472, 36) };
        var diagnosticsPanel = new GlassPanel
        {
            Bounds = new Rectangle(24, 456, 472, 130),
            AutoScroll = true,
            AutoScrollMinSize = new Size(0, 1040),
            Visible = false,
        };
        diagnosticsToggle.Click += (_, _) =>
        {
            diagnosticsPanel.Visible = !diagnosticsPanel.Visible;
            diagnosticsToggle.Text = diagnosticsPanel.Visible ? "诊断工具  ▴" : "诊断工具  ▾";
        };
        diagnosticsPanel.Controls.AddRange(new Control[]
        {
            _liveCastStatsLabel, diagnosticsNoteLabel,
            _captureSelfTestButton, _captureStatsLabel,
            _encodeSelfTestButton, _encodeStatsLabel, _transportSelfTestButton, _transportStatsLabel,
            _audioCaptureSelfTestButton, _audioCaptureStatsLabel,
            _aacEncodeSelfTestButton, _aacEncodeStatsLabel,
            _rawTransportSelfTestButton, _rawTransportStatsLabel,
            _discoverySelfTestButton, _discoveryStatsLabel,
            _pairedTerminalStoreSelfTestButton, _pairedTerminalStoreStatsLabel,
            _deviceIdentitySelfTestButton, _deviceIdentityStatsLabel,
        });

        _pairedPanel = new GlassPanel
        {
            Dock = DockStyle.Fill, Visible = false, Padding = new Padding(8), CornerRadius = 20,
            GlassTint = Color.FromArgb(190, 12, 31, 52),
        };
        _pairedPanel.Controls.AddRange(new Control[]
        {
            castingTitle, liveState, liveCard, privacyCard,
            _stopCastButton, diagnosticsToggle, diagnosticsPanel,
        });

        Controls.Add(_pairedPanel);
        Controls.Add(_standbyPanel);
        ModernUi.StyleTree(this);
        ModernUi.Primary(_startButton);
        ModernUi.Primary(_stopCastButton, danger: true);

        _listRefreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _listRefreshTimer.Tick += (_, _) => RefreshTerminalList();
        _listRefreshTimer.Start();

        // TerminalDiscoveryClient.TerminalListChanged (see its own doc comment) previously had no
        // subscriber anywhere — this 1s timer alone already covered every case it needs to
        // (including the removal/expiry case TerminalListChanged deliberately doesn't cover, see
        // that class's own comment on why), just with up to ~1s of avoidable staleness for the
        // "a brand-new terminal just started beaconing" case specifically. Subscribing doesn't
        // replace the timer (still needed for expiry) — it only makes that one case feel instant
        // instead of waiting for the next tick, which matters most exactly when someone is watching
        // this standby list for a Terminal they just powered on.
        _discoveryClient.TerminalListChanged += OnTerminalListChanged;

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

    /// <summary>Raised from <see cref="TerminalDiscoveryClient"/>'s background receive loop —
    /// marshal to the UI thread before touching <see cref="_terminalListBox"/>. Guards against a
    /// beacon racing ahead of this form's own window handle: <c>Program.cs</c> calls
    /// <c>discoveryClient.Start()</c> before constructing <see cref="MainForm"/> at all, so in
    /// principle a beacon could arrive before this form has a handle to <see cref="Control.BeginInvoke(Delegate)"/>
    /// onto — vanishingly unlikely on a real LAN and impossible to verify timing for in this sandbox,
    /// but <see cref="_listRefreshTimer"/>'s own poll covers this exact update a moment later
    /// regardless, so silently skipping here rather than risking an exception is the safe choice.</summary>
    private void OnTerminalListChanged()
    {
        if (!IsHandleCreated) return;
        // Explicit new Action(...) rather than a bare method group: Control.BeginInvoke(Delegate)
        // takes the abstract Delegate base type, which a method group can't convert to directly —
        // same wrapping this repo's Terminal side already uses for the same reason
        // (OverlayWindow.BeginInvoke(new Action(...)) in PlaybackEngine.cs).
        BeginInvoke(new Action(RefreshTerminalList));
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
            .Select(t => new TerminalListEntry(t.DeviceId, t.DeviceName, t, _pairedTerminals.Find(t.DeviceId)?.PairedAt))
            .Concat(_pairedTerminals.All
                .Where(p => !liveIds.Contains(p.DeviceId))
                .OrderBy(p => p.DeviceName)
                .Select(p => new TerminalListEntry(p.DeviceId, p.DeviceName, null, p.PairedAt)))
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
                if (!PairingSecurity.IsSupportedKey(response.PairingKey, response.PairingKeyFormatVersion))
                {
                    MessageBox.Show(this, "终端机返回的配对密钥无效，请升级两端后重新配对。",
                        "配对失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                ShowPaired(terminal, outputIndex, response.PairingKey!, response.PairingKeyFormatVersion);
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
            // TerminalListEntry instance — while a pairing request was in flight (its default
            // timeout, now over 2 minutes to actually cover how long a human might take to click
            // through the Terminal's own confirmation dialog — see TerminalDiscoveryClient's own
            // comment on that fix — is far longer than a single refresh tick). If the selected
            // terminal's beacon happened to lapse during that wait, re-enabling the button without
            // this check would let the user immediately retry against what the list now shows as
            // offline.
            _startButton.Text = "开始投屏";
            _startButton.Enabled = _terminalListBox.SelectedItem is TerminalListEntry { Live: not null };
        }
    }

    private void ShowPaired(DiscoveredTerminal terminal, int outputIndex, string pairingKey, int keyFormatVersion)
    {
        // Remembered here, not just when the standby list was last built — this is the one point
        // where a pairing is actually confirmed successful, which is the right moment to persist it
        // for next time's "已配对直显" list (PLANNING.md §12), independent of whether/when the
        // standby list next happens to refresh.
        _pairedTerminals.Upsert(new PairedTerminal(
            terminal.DeviceId, terminal.DeviceName, DateTimeOffset.Now, pairingKey, keyFormatVersion));

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
            ? $"终端机确认: 已解码 {_liveCastSession.TerminalFramesDecoded} 帧，" +
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

    private void ShowStandby()
    {
        _liveCastStatsTimer.Stop();
        _liveCastSession?.Stop();
        _liveCastSession?.Dispose();
        _liveCastSession = null;
        _liveCastStatsLabel.Text = "";
        _privacyReminderLabel.Text = "♢  当前正在共享所选显示器";
        _elapsedLabel.Text = "00:00";
        _qualityLabel.Text = "●  正在建立连接…";

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
            _aacEncodeSelfTestButton.Text = "开始AAC编解码自检 (WASAPI loopback→AAC→PCM)";
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

    private void DrawTerminalCard(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _terminalListBox.Items.Count) return;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var entry = (TerminalListEntry)_terminalListBox.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        var card = e.Bounds;
        card.Inflate(-4, -5);
        using var path = RoundedCard(card, 13);
        using var fill = new SolidBrush(selected ? Color.FromArgb(39, 72, 112) : ModernUi.SurfaceRaised);
        using var border = new Pen(selected ? ModernUi.Accent : ModernUi.Border, selected ? 2F : 1F);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);

        var iconRect = new Rectangle(card.Left + 18, card.Top + 18, 42, 32);
        using var iconPen = new Pen(Color.FromArgb(174, 198, 235), 3F);
        e.Graphics.DrawRectangle(iconPen, iconRect);
        e.Graphics.DrawLine(iconPen, iconRect.Left + 13, iconRect.Bottom + 8, iconRect.Right - 13, iconRect.Bottom + 8);
        e.Graphics.DrawLine(iconPen, iconRect.Left + 21, iconRect.Bottom, iconRect.Left + 21, iconRect.Bottom + 8);

        using var titleFont = new Font("Segoe UI Semibold", 11F);
        using var metaFont = new Font("Segoe UI", 9F);
        TextRenderer.DrawText(e.Graphics, entry.DeviceName, titleFont,
            new Rectangle(card.Left + 78, card.Top + 13, card.Width - 120, 27), ModernUi.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        string meta = entry.Live != null ? "在线 · 可投屏" : "离线 · 等待终端上线";
        TextRenderer.DrawText(e.Graphics, meta, metaFont,
            new Rectangle(card.Left + 78, card.Top + 40, card.Width - 120, 24),
            entry.Live != null ? ModernUi.Muted : Color.FromArgb(128, 143, 164),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        using var stateBrush = new SolidBrush(entry.Live != null ? ModernUi.Success : Color.FromArgb(91, 108, 130));
        e.Graphics.FillEllipse(stateBrush, card.Right - 34, card.Top + 31, 12, 12);
        if (selected)
        {
            using var checkFont = new Font("Segoe UI Symbol", 12F, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, "✓", checkFont,
                new Rectangle(card.Right - 48, card.Top + 22, 32, 32), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedCard(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // MainForm doesn't own _discoveryClient (Program.cs's `using` does, and disposes it
            // after this form) — unsubscribing here just prevents a stale TerminalListChanged
            // handler from firing BeginInvoke against a form that's mid-teardown, not a leak of
            // _discoveryClient itself.
            _discoveryClient.TerminalListChanged -= OnTerminalListChanged;
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
