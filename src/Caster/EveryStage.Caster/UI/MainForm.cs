using System.Drawing.Drawing2D;
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
public sealed partial class MainForm : GradientForm
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
    private readonly Label _liveStateLabel;
    private readonly Label _elapsedLabel;
    private readonly Label _qualityLabel;
    private readonly Label _privacyReminderLabel;
    private readonly Label _liveCastStatsLabel;
    private readonly Button _stopCastButton;
    // D-04 status band (see RefreshLiveCastStats/UpdateStatusBand in MainForm.LiveCast.cs) and the
    // diagnostics toggle/panel, promoted from constructor locals so the band can offer a
    // click-to-expand path into the diagnostics area.
    private readonly GlassPanel _statusBand;
    private readonly Label _statusBandLabel;
    private readonly Label _statusBandHintLabel;
    private readonly Button _diagnosticsToggle;
    private readonly GlassPanel _diagnosticsPanel;
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
    private sealed record MonitorComboItem(int OutputIndex, string Label, string? DeviceName = null)
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
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        WindowsAppearance.UseDarkTitleBar(this);
        WindowsAppearance.EnableGlass(this);

        // --- 待机态 (PLANNING.md §12) ---
        _terminalListBox = new ListBox
        {
            Size = new Size(472, 190),
            Margin = new Padding(0, 0, 0, 22),
            DisplayMember = nameof(TerminalListEntry.DisplayText), // else ListBox shows the record's generated ToString().
            Font = new Font(ModernUi.UiFont, 11F),
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = LogicalToDeviceUnits(84), // owner-drawn: not touched by auto-scaling
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
        };
        // P-04: owner-drawn cards flicker without double buffering; ListBox exposes
        // DoubleBuffered only as a protected property, so set it once via reflection rather
        // than growing a ListBox subclass for this one flag.
        typeof(ListBox).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(_terminalListBox, true);
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
            Text = "点击\"开始投屏\"后，将投放整个屏幕（全屏捕获），而不是仅本窗口或某个应用。\n" +
                   "画面经过设备认证，但未加密传输：请只在受信任的局域网中投屏。",
            ForeColor = Color.DimGray,
            Size = new Size(472, 52),
        };

        // PLANNING.md §12"选择捕获哪个显示器"——之前ScreenCaptureSource固定捕获outputIndex=0，
        // 多显示器场景完全没有UI选择。RefreshMonitorList()（下面）在构造函数末尾和每次回到待机态
        // 时都会重新枚举，跟这一轮刚修过的SettingsPanel显示器列表是同一个"别只枚举一次"教训。
        var standbyTitle = new Label { Text = "投屏器 · 待机", Font = new Font(ModernUi.UiFontSemibold, 16F), AutoSize = true, Location = new Point(0, 0) };
        var connected = new Label { Text = "●  已连接", ForeColor = ModernUi.Success, AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Bounds = new Rectangle(336, 2, 136, 24) };
        var targetLabel = new Label { Text = "选择终端", ForeColor = ModernUi.Muted, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        var monitorLabel = new Label { Text = "显示器", ForeColor = ModernUi.Muted, AutoSize = true, Size = new Size(472, 22), Margin = new Padding(0, 0, 0, 4) };
        _monitorComboBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Size = new Size(472, 36), Margin = new Padding(0, 0, 0, 22),
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = LogicalToDeviceUnits(30), FlatStyle = FlatStyle.Flat,
        };
        _monitorComboBox.DrawItem += DrawMonitorItem;

        _startButton = new Button
        {
            Text = "开始投屏",
            Enabled = false,
            Size = new Size(472, 50),
            Margin = new Padding(0, 0, 0, 16),
        };
        _startButton.Click += OnStartButtonClick;
        ModernUi.Primary(_startButton);

        _removePairingButton = new Button
        {
            Text = "移除配对",
            Enabled = false,
            Size = new Size(472, 34),
            Margin = new Padding(0, 0, 0, 36),
        };
        _removePairingButton.Click += OnRemovePairingClick;

        // D-01 第一步（顶层布局面板化）: the standby view's top-level stacking used to be nine
        // hand-placed absolute Y coordinates — every insertion meant re-measuring everything below
        // it (this constructor's own ClientSize comment above is a nine-step record of exactly that
        // pain). The top level now rides a vertical FlowLayoutPanel: children keep their width
        // (472) and height, and the old gaps between them are reproduced as bottom Margins. The
        // title + "● 已连接" pair travels as one fixed-height row container because the flow is
        // strictly top-down. Card interiors still use relative Bounds — converting those is a
        // later, per-card step, deliberately not bundled into this one. The flow's Padding
        // (16 horizontal on top of the panel's own 8) reproduces the old 24px left/right inset,
        // and its 22 top reproduces the old title Y of 30 (8 + 22).
        var standbyTitleRow = new Panel
        {
            Size = new Size(472, 28), Margin = new Padding(0, 0, 0, 22), BackColor = Color.Transparent,
        };
        standbyTitleRow.Controls.AddRange(new Control[] { standbyTitle, connected });
        var standbyFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = Color.Transparent, Padding = new Padding(16, 22, 16, 16),
        };
        standbyFlow.Controls.AddRange(new Control[]
        {
            standbyTitleRow, targetLabel, _terminalListBox, monitorLabel, _monitorComboBox,
            _startButton, _removePairingButton, privacyLabel,
        });
        _standbyPanel = new GlassPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(8), CornerRadius = 20, AutoScroll = true,
            GlassTint = Color.FromArgb(190, 12, 31, 52),
        };
        _standbyPanel.Controls.Add(standbyFlow);
        RefreshMonitorList();
        // Displays plugged in or removed while this window waits: DXGI output indices are positional, so
        // a stale list could make the chosen entry capture a different physical screen than the one the
        // operator picked, under a line promising "当前正在共享所选显示器" (audit A-13). Re-list on every
        // display change; SystemEvents may raise this on its own thread, hence BeginInvoke.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // --- 投屏中态：现在是真的在投屏（见类doc comment），不再是占位符 ---
        // 布局对齐设计图：左侧显示器图标，右侧上「终端名」下「计时」，再下一条质量行。
        _pairedWithLabel = new Label { Bounds = new Rectangle(120, 22, 344, 30), Font = new Font(ModernUi.UiFontSemibold, 15F) };
        _elapsedLabel = new Label
        {
            Text = "00:00", Bounds = new Rectangle(118, 48, 340, 60),
            Font = new Font(ModernUi.UiFontSemibold, 34F), ForeColor = Color.FromArgb(102, 163, 255),
        };
        _qualityLabel = new Label
        {
            Text = "●  正在建立连接…", Bounds = new Rectangle(28, 124, 430, 30),
            Font = new Font(ModernUi.UiFont, 10.5F), ForeColor = ModernUi.Success,
        };
        var liveCard = new GlassPanel
        {
            Size = new Size(472, 166), Margin = new Padding(0, 0, 0, 16), CornerRadius = 16,
            GlassTint = Color.FromArgb(215, 20, 39, 62),
        };
        var liveMonitorGlyph = new Panel { Bounds = new Rectangle(24, 40, 72, 60), BackColor = Color.Transparent };
        liveMonitorGlyph.Paint += (_, pe) => DrawMonitorGlyph(pe.Graphics,
            new Rectangle(0, 0, liveMonitorGlyph.Width, liveMonitorGlyph.Height), Color.FromArgb(160, 188, 226));
        liveCard.Controls.AddRange(new Control[]
        {
            liveMonitorGlyph,
            _pairedWithLabel, _elapsedLabel, _qualityLabel,
        });

        // PLANNING.md §12 "投屏中" 状态里的隐私提醒条 + 时长显示——之前只有开始投屏前那条一次性的
        // privacyLabel，投屏过程中完全没有任何持续提醒或计时，这两个都是这次新加的。
        _privacyReminderLabel = new Label
        {
            Text = "🛡  当前正在共享所选显示器",
            ForeColor = ModernUi.Warning,
            Font = new Font(ModernUi.UiFontSemibold, 11F),
            Bounds = new Rectangle(20, 10, 430, 26),
        };
        var encryptionNote = new Label
        {
            Text = "媒体经设备认证，未加密 · 请只在受信任的局域网中使用",
            ForeColor = ModernUi.Muted,
            Bounds = new Rectangle(20, 38, 430, 22),
        };
        var privacyCard = new GlassPanel
        {
            Size = new Size(472, 68), Margin = new Padding(0, 0, 0, 12), CornerRadius = 14,
            GlassTint = Color.FromArgb(220, 60, 46, 20),
        };
        privacyCard.Controls.Add(_privacyReminderLabel);
        privacyCard.Controls.Add(encryptionNote);

        // Height grown from 90 to 108 (+18) to fit RefreshLiveCastStats' new always-visible
        // "（仅代表状态通道送达...）" caveat line without clipping the 5 lines already packed in
        // here — every control below this one shifted down by that same 18px.
        _liveCastStatsLabel = new Label { Bounds = new Rectangle(12, 38, 430, 120), ForeColor = ModernUi.Muted };

        _stopCastButton = new Button { Text = "停止投屏", Size = new Size(472, 50), Margin = new Padding(0, 0, 0, 18) };
        _stopCastButton.Click += (_, _) => ShowStandby();
        ModernUi.Primary(_stopCastButton, danger: true);

        var castingTitle = new Label { Text = "投屏器 · 投屏中", Font = new Font(ModernUi.UiFontSemibold, 16F), AutoSize = true, Location = new Point(0, 0) };
        _liveStateLabel = new Label { Text = "●  已连接 · 等待终端投到大屏", ForeColor = ModernUi.Warning, AutoSize = true, Location = new Point(268, 10) };

        // D-04 状态带: the paired view's health used to be one small line of text at the title's
        // right (_liveStateLabel) plus another inside liveCard (_qualityLabel) — easy to miss at a
        // glance across a room. This band promotes the same signal to a full-width strip directly
        // under the title: RefreshLiveCastStats (MainForm.LiveCast.cs) drives it from the same
        // IsTerminalAlive source, so the three can never disagree. The small labels keep their
        // existing jobs (title-row liveness detail, in-card quality/RTT detail) — the band only
        // adds the at-a-glance layer, plus the "what to do next" hint the small labels had no room
        // for. Clicking the band toggles the diagnostics area (that's why the toggle/panel below
        // are fields now, not constructor locals).
        _statusBandLabel = new Label
        {
            Text = "●  正在建立连接 · 等待终端确认",
            Font = new Font(ModernUi.UiFontSemibold, 11.5F),
            ForeColor = ModernUi.Warning,
            Bounds = new Rectangle(16, 7, 440, 24),
        };
        _statusBandHintLabel = new Label
        {
            Text = "终端长时间无响应时，检查终端机是否开着，或展开下方诊断工具定位（点此状态带也可展开）。",
            Font = new Font(ModernUi.UiFont, 9F),
            ForeColor = ModernUi.Muted,
            Bounds = new Rectangle(16, 33, 440, 20),
        };
        _statusBand = new GlassPanel
        {
            Size = new Size(472, 60), Margin = new Padding(0, 0, 0, 12), CornerRadius = 14,
            GlassTint = Color.FromArgb(220, 62, 48, 18), Cursor = Cursors.Hand,
        };
        _statusBand.Controls.AddRange(new Control[] { _statusBandLabel, _statusBandHintLabel });
        // The band's click-to-expand wiring lives further down, right after _diagnosticsToggle /
        // _diagnosticsPanel are assigned — creating that delegate here, before the fields it
        // dereferences exist, is both a nullable-analysis error and a worse read.

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

        _diagnosticsToggle = new Button { Text = "诊断工具  ▾", Size = new Size(472, 36), Margin = new Padding(0, 0, 0, 8) };
        _diagnosticsPanel = new GlassPanel
        {
            Size = new Size(472, 130),
            AutoScroll = true,
            AutoScrollMinSize = new Size(0, 1040),
            Visible = false,
        };
        _diagnosticsToggle.Click += (_, _) =>
        {
            _diagnosticsPanel.Visible = !_diagnosticsPanel.Visible;
            _diagnosticsToggle.Text = _diagnosticsPanel.Visible ? "诊断工具  ▴" : "诊断工具  ▾";
        };
        _diagnosticsPanel.Controls.AddRange(new Control[]
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

        EventHandler toggleDiagnostics = (_, _) =>
        {
            _diagnosticsPanel.Visible = !_diagnosticsPanel.Visible;
            _diagnosticsToggle.Text = _diagnosticsPanel.Visible ? "诊断工具  ▴" : "诊断工具  ▾";
        };
        _statusBand.Click += toggleDiagnostics;
        _statusBandLabel.Click += toggleDiagnostics;
        _statusBandHintLabel.Click += toggleDiagnostics;

        // D-01 第一步（同待机面板）: the paired view's top level rides the same vertical flow —
        // title row container, then the D-04 状态带 as an ordinary flow item, then the cards and
        // buttons in their old visual order, old gaps reproduced as bottom Margins. liveCard /
        // privacyCard / _diagnosticsPanel interiors keep their relative Bounds untouched.
        var pairedTitleRow = new Panel
        {
            Size = new Size(472, 30), Margin = new Padding(0, 0, 0, 10), BackColor = Color.Transparent,
        };
        pairedTitleRow.Controls.AddRange(new Control[] { castingTitle, _liveStateLabel });
        var pairedFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = Color.Transparent, Padding = new Padding(16, 22, 16, 16),
        };
        pairedFlow.Controls.AddRange(new Control[]
        {
            pairedTitleRow, _statusBand, liveCard, privacyCard,
            _stopCastButton, _diagnosticsToggle, _diagnosticsPanel,
        });
        _pairedPanel = new GlassPanel
        {
            Dock = DockStyle.Fill, Visible = false, Padding = new Padding(8), CornerRadius = 20, AutoScroll = true,
            GlassTint = Color.FromArgb(190, 12, 31, 52),
        };
        _pairedPanel.Controls.Add(pairedFlow);

        // Observed WinForms docking: the BACK-most control docks FIRST and claims its edge; the
        // FRONT-most gets the remainder. Send the title bar to the back so it owns the full top
        // strip, and bring the fill panels to the front so they fill everything below it.
        var titleBar = new AppTitleBar(this);
        Controls.Add(_pairedPanel);
        Controls.Add(_standbyPanel);
        Controls.Add(titleBar);
        titleBar.SendToBack();
        _pairedPanel.BringToFront();
        _standbyPanel.BringToFront();
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

        // Every bound above is in 96-DPI pixels. AutoScaleMode alone scaled nothing (no
        // AutoScaleDimensions means a factor of 1), so on a 125–150 % laptop only the fonts grew and
        // text was clipped. Declaring the design DPI scales the form and its controls once, here.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        ResumeLayout(true);
        // Scaled up, the minimum can exceed a small laptop's work area (1366×768 at 125 %): keep the
        // window on screen rather than forcing its bottom edge below the task bar.
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, workingArea.Width), Math.Min(MinimumSize.Height, workingArea.Height));
        Size = new Size(Math.Min(Width, workingArea.Width), Math.Min(Height, workingArea.Height));
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // MainForm doesn't own _discoveryClient (Program.cs's `using` does, and disposes it
            // after this form) — unsubscribing here just prevents a stale TerminalListChanged
            // handler from firing BeginInvoke against a form that's mid-teardown, not a leak of
            // _discoveryClient itself.
            _discoveryClient.TerminalListChanged -= OnTerminalListChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged; // static event: would keep this form alive
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
