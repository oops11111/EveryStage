using System.Drawing.Drawing2D;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// PLANNING.md §8.2's 文件面板 ("默认首页"): thumbnail grid, category tabs (全部/图片/视频/文档/音频),
/// click a file to play it. Supports both an "导入" file picker and dragging files in from Explorer
/// (§11 "拖拽添加") for getting content into the library in the first place, plus a "移除" button for
/// taking a file back out of the library — the counterpart operation, previously entirely absent
/// from this panel (see this project's README's history on `FileOperationLogger` having a
/// `LogFileRemoved` method with no caller anywhere in the repo; there was no UI path to actually
/// remove a library file at all, not just a missing log call).
///
/// Removing a library entry never touches any <see cref="Activity"/> that already contains a copy of
/// it: both <c>ActivitiesPanel</c>'s "添加文件到活动" and this panel's own <see cref="OnAddToActivityClick"/>
/// deep-copy a <see cref="MediaFile"/> (<see cref="MediaFile.Clone"/>) rather than referencing the
/// library entry by <see cref="MediaFile.Id"/>, so an activity's files have no dependency on the
/// library entry they originated from still existing — see this project's README ("已知风险" #22) on
/// this copy-not-reference behavior being a deliberate, previously-documented choice, not something
/// decided here.
///
/// PLANNING.md §11's "批量选择" now covers two of its three actions: <see cref="_grid"/> allows
/// <c>MultiSelect</c>, "移除" (<see cref="OnRemoveClick"/>) handles any number of selected files at
/// once, and "加入活动" (<see cref="OnAddToActivityClick"/>, added once <see cref="ActivityPickerDialog"/>
/// gave this panel a way to pick a destination scenario/activity — see this project's README) does
/// too — both reuse the existing always-visible toolbar rather than a separate floating one, since it
/// already only enables once something is selected, the same enable-on-selection spirit §11's "选中后
/// 悬浮工具栏出现" describes. The remaining action, "统一设置属性", is NOT implemented — it needs a
/// product decision PLANNING.md doesn't specify (how a multi-file property editor should show/resolve
/// conflicting existing values across the selection) that this round didn't attempt; see this
/// project's README "尚未开始".
///
/// PLANNING.md §8.2's audio "横向播放条" (播放/进度/音量/循环) exists as the「音频播放区」card below
/// the grid (<see cref="_audioBar"/>, an <see cref="AudioPlayerBar"/>), matching the design and
/// always shown — it binds to the track picked in its「文件名 ▾」selector (a newly started track
/// takes over; otherwise the first audio file), and shows an「导入音频」placeholder when there is none. Audio files stay out of the
/// "全部" grid. See <see cref="RefreshAudioBar"/> for how it is rebound and
/// <see cref="OnAddAudioToActivityTrack"/> for its「＋ 加入活动音轨」button (adds a copy to an activity
/// as background audio).
///
/// Not implemented (see this project's README "已知风险"/"尚未开始" for the full writeup of why):
/// real video/PDF/audio thumbnails (video and document items show a generic placeholder icon —
/// building a real one means decoding a frame/first page, which is extra work beyond what a file
/// browser strictly needs), and dragging a library item onto an activity (there is no activity list
/// UI yet in this same window for it to be dragged onto).
/// </summary>
public sealed partial class FilesPanel : UserControl
{
    private string? _flashPath;
    private readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 150 };
    public void FlashOnAir(MediaFile file)
    {
        _flashPath = file.SourcePath;
        _flashTimer.Tick -= EndOnAirFlash;
        _flashTimer.Tick += EndOnAirFlash;
        _flashTimer.Stop();
        _flashTimer.Start();
        Invalidate(true);
    }
    private void EndOnAirFlash(object? sender, EventArgs e)
    {
        _flashTimer.Stop();
        _flashPath = null;
        Invalidate(true);
    }
    private readonly FileLibraryStore _library;
    private readonly FileOperationLogger _fileOpLog;
    private readonly ScenarioStore _scenarioStore;
    private readonly ScenarioRepository _scenarioRepository;
    // Not readonly — see AttachPlaybackEngine's own doc comment: PLANNING.md §5's runtime
    // monitor-hot-plug binding needs to replace this from null to a real PlaybackEngine well after
    // this panel is already constructed.
    private PlaybackEngine? _playback;
    private readonly MediaGrid _grid;
    // Managed keyed images are reliable for owner-drawn Tile items even when the native
    // LargeImageList is intentionally not assigned.
    private readonly Dictionary<string, Image> _thumbnailByFileId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _visibleThumbnailKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _visibleFileCheckTimer = new() { Interval = 3000 };
    private bool _visibleFileCheckRunning;
    private CancellationTokenSource? _thumbnailLoad;
    private CancellationToken _thumbnailPassToken; // token of the thumbnail pass already running (or done)
    private bool _thumbnailPassRunning;
    private readonly SemaphoreSlim _thumbnailWorker = new(1, 1);
    private readonly Dictionary<string, Image> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _thumbnailCacheOrder = new();
    private const int ThumbnailCacheCapacity = 128;
    private ToolStripMenuItem _removeMenuItem = null!;
    private ToolStripMenuItem _addToActivityMenuItem = null!;
    private ToolStripMenuItem _previewMenuItem = null!;
    private ToolStripMenuItem _openLocationMenuItem = null!;
    private readonly ContextMenuStrip _fileContextMenu;
    private readonly ContextMenuStrip _toolbarMenu;
    private MediaFile? _contextFile;
    private readonly PillButton _outputStatusChip;
    private MediaKind? _activeFilter;
    private readonly Label _countLabel;
    private readonly SearchBox _searchBox;
    /// <summary>Card size: 0 = 小, 1 = 中 (default), 2 = 大. Session-only.</summary>
    private int _cardSize = 1;
    private string? _lastProgramPath;
    private bool _outputActive;

    /// <summary>PLANNING.md §8.2's audio "横向播放条" — now a single large player bar
    /// (<see cref="AudioPlayerBar"/>) matching the design, instead of the old per-file
    /// <c>FlowLayoutPanel</c> of rows. The bar binds to the track picked in its file-name selector
    /// (a newly started track takes over) and is always shown, with an import placeholder when the library
    /// has no audio — see <see cref="AudioPlayerBar"/>'s own doc comment.</summary>
    private readonly AudioPlayerBar _audioBar;

    /// <summary>The audio bar is also the controller behind MainWindow's sidebar「音频独立播放区」card
    /// (<see cref="AudioSidebarCard"/>), so both always show and drive the same track.</summary>
    internal AudioPlayerBar AudioBar => _audioBar;
    private readonly System.Windows.Forms.Timer _audioBarRefreshTimer;
    private readonly Label _emptyStateLabel;

    /// <summary>Card width per size (小 / 中 / 大), each 20% larger than the one before; the gap stays fixed.</summary>
    private static readonly int[] CardWidths = { 85, 102, 122 };

    /// <summary>What Program shows right now (null when nothing local is on air) — the 「大屏」 badge.</summary>
    public Func<MediaFile?>? ProgramFile { get; set; }
    public Func<PlaybackEngine?>? ProgramPlayback { get; set; }

    /// <summary>Raised when the user double-clicks a file to play it standalone (no activity
    /// context — see <c>PlaybackEngine.RequestPlay(MediaFile)</c>'s own doc comment on what that
    /// means for completion actions). Also raised by <see cref="_audioBar"/>'s play button when its
    /// file isn't loaded yet (internal Preview playback).</summary>
    public event Action<MediaFile>? FilePlayRequested;

    /// <summary>"停止预览": stop the Preview monitor (Program is unaffected).</summary>
    public event Action? PlaybackStopRequested;

    /// <summary>"投到屏幕": put this file on the extended display (via Preview → Program).</summary>
    public event Action<MediaFile>? TakeRequested;

    /// <summary>"停止输出": take the extended display off air (Preview is unaffected).</summary>
    public event Action? StopProgramRequested;

    /// <summary>Audio files dropped or imported here: they go to the background-music playlist
    /// (MainWindow slides the 歌单 drawer up with them added), never into the library.</summary>
    public event Action<IReadOnlyList<string>>? AudioDropped;

    /// <summary>Whether Program is currently on air — enables the context menu's "停止输出".</summary>
    public Func<bool>? IsProgramLive { get; set; }

    /// <summary>A plain click on a card: 预览, or the big screen with 「单击即上屏」 (MainWindow decides).</summary>
    public event Action<MediaFile>? FileClicked;

    /// <summary>设置 「单击即上屏」 (MainWindow routes the click to the big screen instead of 预览).</summary>
    public Func<bool>? ClickTakesToAir { get; set; }

    public FilesPanel(
        FileLibraryStore library, FileOperationLogger fileOpLog, ScenarioStore scenarioStore,
        ScenarioRepository scenarioRepository, PlaybackEngine? playback)
    {
        _library = library;
        _fileOpLog = fileOpLog;
        _scenarioStore = scenarioStore;
        _scenarioRepository = scenarioRepository;
        _playback = playback;
        if (_playback != null)
        {
            _playback.FileStarted += OnFileStarted;
            _playback.StateChanged += OnPreviewStateChanged;
        }
        Dock = DockStyle.Fill;
        AllowDrop = true;
        // Transparent: the gaps between the 文件 card and the 音频播放区 card show the page glass behind.
        BackColor = Color.Transparent;
        Padding = new Padding(0);

        // One header row (design 「文件库」): title · count · type filter …… search · card size · ＋ 导入.
        // Laid out by hand in LayoutHeader: a narrow page drops the count, then shrinks the search box,
        // then compacts the filter segments.
        var header = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.Transparent };
        var pageTitle = ModernUi.SetForegroundRole(new Label
        {
            Text = "文件库", AutoSize = true,
            Font = new Font(ModernUi.UiFontSemibold, 15F), BackColor = Color.Transparent,
        }, UiForegroundRole.Text);
        _countLabel = ModernUi.SetForegroundRole(new Label { AutoSize = true, Font = new Font(ModernUi.UiFont, 9.5F), BackColor = Color.Transparent }, UiForegroundRole.Muted);
        // No 音频 tab: audio belongs to the background music (歌单), never to the library.
        MediaKind?[] filters = { null, MediaKind.Image, MediaKind.Video, MediaKind.Document };
        var filterTabs = new SegmentedFilter(new[] { "全部", "图片", "视频", "文档" }) { Height = 34 };
        filterTabs.SelectedIndexChanged += (_, _) =>
        {
            _activeFilter = filters[filterTabs.SelectedIndex];
            Refresh_();
        };

        _toolbarMenu = BuildToolbarMenu();

        _searchBox = new SearchBox { Height = 34 };
        _searchBox.TextChanged += (_, _) => Refresh_();

        var sizeTabs = new SegmentedFilter(new[] { "小", "中", "大" }) { Height = 34, Compact = true, SelectedIndex = 1 };
        sizeTabs.AccessibleName = "卡片大小";
        sizeTabs.SelectedIndexChanged += (_, _) =>
        {
            _cardSize = sizeTabs.SelectedIndex;
            if (_grid != null) _grid.CardWidth = CardWidths[_cardSize];
        };

        var importButton = new RoundedActionButton { Text = "导入", IconGlyph = "\uE710", Height = 34 };
        ModernUi.StyleButton(importButton, primary: true);
        importButton.Click += (_, _) => ImportViaDialog();

        header.Controls.AddRange(new Control[] { pageTitle, _countLabel, filterTabs, _searchBox, sizeTabs, importButton });
        header.Resize += (_, _) => LayoutHeader();
        void LayoutHeader()
        {
            const int gap = 12;
            int w = header.ClientSize.Width, cy = (header.ClientSize.Height - 6) / 2;
            importButton.Width = importButton.PreferredContentWidth();
            pageTitle.Location = new Point(0, cy - pageTitle.Height / 2);
            int fullFilter = Math.Min(filterTabs.MaximumWidth, 5 * 58);
            // Narrow pages give up, in order: the count, the wide filter segments, the card-size
            // buttons, and finally search width (never below 90px) — controls never overlap.
            for (int step = 0; ; step++)
            {
                bool showCount = step < 1, wideFilter = step < 2, showSize = step < 3;
                filterTabs.Compact = !wideFilter;
                int filterWidth = wideFilter ? fullFilter : filterTabs.MinimumWidth;
                int right = w - importButton.Width - (showSize ? gap + sizeTabs.MinimumWidth : 0);
                int left = pageTitle.Right + gap + (showCount ? _countLabel.PreferredWidth + gap : 0);
                int searchRoom = right - gap - (left + filterWidth + gap);
                if (searchRoom < 160 && step < 3) continue;

                _countLabel.Visible = showCount;
                sizeTabs.Visible = showSize;
                importButton.Location = new Point(w - importButton.Width, cy - importButton.Height / 2);
                sizeTabs.Bounds = new Rectangle(importButton.Left - gap - sizeTabs.MinimumWidth, cy - sizeTabs.Height / 2, sizeTabs.MinimumWidth, sizeTabs.Height);
                _countLabel.Location = new Point(pageTitle.Right + gap, cy - _countLabel.Height / 2 + 2);
                filterTabs.Bounds = new Rectangle(left, cy - filterTabs.Height / 2, filterWidth, filterTabs.Height);
                int searchWidth = Math.Clamp(searchRoom, 90, 240);
                _searchBox.Bounds = new Rectangle(right - gap - searchWidth, cy - _searchBox.Height / 2, searchWidth, _searchBox.Height);
                break;
            }
            foreach (Control c in header.Controls) c.Invalidate();
        }

        _outputStatusChip = new PillButton
        {
            Text = "○  待机", AutoSize = true, Height = 38, Visible = false,
            Margin = new Padding(4, 1, 8, 0), Enabled = false,
        };

        // The card grid: our own layout (columns re-flow with the card size and the width), selection,
        // ⋯ / right-click menus and drag-to-reorder — see MediaGrid.
        _grid = new MediaGrid
        {
            Dock = DockStyle.Fill,
            Gap = CardGap,
            CardWidth = CardWidths[_cardSize],
            TileHeightFor = TileHeightFor,
            Font = new Font(ModernUi.UiFont, 10F),
        };
        _fileContextMenu = BuildFileContextMenu();
        _grid.DrawCard += DrawMediaCard;
        _grid.ViewportChanged += OnThumbnailViewportChanged;
        _grid.SelectionChanged += () =>
        {
            bool hasSelection = _grid.SelectedItems.Count > 0;
            _removeMenuItem.Enabled = hasSelection;
            _addToActivityMenuItem.Enabled = hasSelection;
            _previewMenuItem.Enabled = hasSelection;
            _openLocationMenuItem.Enabled = hasSelection;
        };
        // Single click: show the file in the 预览 monitor (the card's ⋯ opens its menu instead).
        // Double click: put it straight on the big screen.
        _grid.ItemClicked += (item, e) =>
        {
            int index = _grid.Items.ToList().IndexOf(item);
            if (index >= 0 && CardLayout(_grid.CardBounds(index)).More.Contains(e.Location))
            {
                _contextFile = item.File;
                _fileContextMenu.Show(_grid, e.Location);
                return;
            }
            FileClicked?.Invoke(item.File);
        };
        // 单击即上屏 (设置): the click already took it to the big screen; a double-click adds nothing.
        _grid.ItemDoubleClicked += (item, _) => { if (ClickTakesToAir?.Invoke() != true) TakeRequested?.Invoke(item.File); };
        _grid.ContextMenuRequested += (item, point) =>
        {
            if (item == null) { _toolbarMenu.Show(_grid, point); return; }
            _contextFile = item.File;
            _fileContextMenu.Show(_grid, point);
        };
        _grid.ReorderRequested += OnReorder;
        // The saved order is the whole library's: with a filter or search hiding cards, a drop position
        // would be ambiguous, so dragging is refused and the reason shown at the pointer.
        _grid.CanReorder = () => _activeFilter == null && _searchBox.Text.Trim().Length == 0;
        var reorderHint = new ToolTip();
        _grid.ReorderBlocked += point =>
            reorderHint.Show("筛选或搜索时不能拖动排序。请切回「全部」并清空搜索后再拖动。", _grid, point.X + 12, point.Y + 12, 3000);

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _grid.DragEnter += OnDragEnter;
        _grid.DragDrop += OnDragDrop;
        _grid.AllowDrop = true;

        // 效果图的「音频播放区」是一张独立卡片：标题行（音频播放区 + 说明）+ 内嵌播放条，常驻显示——
        // 没有音频时播放条显示「导入音频」占位，而不是整块隐藏。
        _audioBar = new AudioPlayerBar(library, fileOpLog, playback) { Dock = DockStyle.Top };
        _audioBar.AddToActivityTrackRequested += OnAddAudioToActivityTrack;
        _audioBar.ImportRequested += ImportAudioViaDialog;
        _audioBar.OpenLocationRequested += OpenLocation;
        _audioBar.RemoveRequested += file => RemoveFromLibrary(new[] { file });
        _audioBar.PlayRequested += file =>
        {
            FilePlayRequested?.Invoke(file); // internal Preview playback — never an external player
            _audioBar.RefreshLiveState();
        };

        // Control.Controls.Add order determines Dock z-order for same-DockStyle siblings: the LAST
        // one added claims its edge first. The grid (Fill) goes first so it takes whatever is left;
        // the header claims the top, the audio card and its gap the bottom.
        Controls.Add(_grid);
        Controls.Add(header);
        // No audio card here any more: background music has its own bar and playlists (MainWindow);
        // audio files are reachable through the 音频 filter and join playlists from their menu.

        _emptyStateLabel = ModernUi.SetBackgroundRole(ModernUi.SetForegroundRole(new Label
        {
            Text = "文件库还是空的\n\n点击“＋ 导入”或将文件拖到这里",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(ModernUi.UiFont, 12F),
            Size = new Size(420, 110),
            Visible = false,
        }, UiForegroundRole.Muted), UiBackgroundRole.Surface);
        Controls.Add(_emptyStateLabel);
        _emptyStateLabel.BringToFront();
        Resize += (_, _) => PositionEmptyState();

        // Not tied to this panel's own show/hide — MainWindow.ShowPanel fully removes/re-adds panels
        // from _contentHost on every tab switch rather than hiding them (see that method), so there's
        // no "became visible" hook to start/stop this against the way FloatingPreviewWindow.
        // ShowForActiveOutput/HideForIdleOutput can for its own timer. Left running for this control's
        // whole lifetime instead — same always-on-regardless-of-visibility precedent
        // OverlayWindow._topMostReasserter already sets in this codebase, at a comparable 500ms/2s
        // cadence to FloatingPreviewWindow's own polling.
        _audioBarRefreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _audioBarRefreshTimer.Tick += (_, _) =>
        {
            _audioBar.RefreshLiveState();
            // Repaint the grid only when what is on the big screen changed (the 「大屏」 badge).
            string? programPath = ProgramFile?.Invoke()?.SourcePath;
            if (programPath == _lastProgramPath && (!Visible ||
                (ProgramPlayback?.Invoke() is not { State: PlaybackChannelState.Playing or PlaybackChannelState.Paused }
                 && _playback is not { State: PlaybackChannelState.Playing or PlaybackChannelState.Paused }))) return;
            _lastProgramPath = programPath;
            _grid.Invalidate();
        };
        _audioBarRefreshTimer.Start();

        _visibleFileCheckTimer.Tick += (_, _) => _ = RefreshVisibleFileStatesAsync();
        _visibleFileCheckTimer.Start();

        Refresh_();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _flashTimer.Dispose();
        if (disposing)
        {
            _thumbnailLoad?.Cancel();
            _thumbnailLoad?.Dispose();
            _thumbnailLoad = null;
            _visibleFileCheckTimer.Dispose();
            _toolbarMenu.Dispose();
            foreach (var image in _thumbnailByFileId.Values) image.Dispose();
            _thumbnailByFileId.Clear();
            _visibleThumbnailKeys.Clear();
            foreach (var image in _thumbnailCache.Values) image.Dispose();
            _thumbnailCache.Clear();
            _thumbnailCacheOrder.Clear();
            _fileContextMenu.Dispose();
            if (_playback != null)
            {
                _playback.FileStarted -= OnFileStarted;
                _playback.StateChanged -= OnPreviewStateChanged;
            }
            _audioBarRefreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>PLANNING.md §5's runtime monitor-hot-plug binding — called (at most once) from
    /// <c>MainWindow.AttachPlaybackEngine</c> when a display shows up after this Terminal already
    /// started with none bound. The audio bar reads its playback engine fresh each tick and is also
    /// re-pointed here via <see cref="AudioPlayerBar.AttachPlaybackEngine"/>. The current-file
    /// highlight does subscribe to <see cref="PlaybackEngine.FileStarted"/>, so this method also
    /// moves that subscription from any previous engine to the newly attached one. <see cref="_grid"/>'s own double-click doesn't read <see cref="_playback"/> at all —
    /// it only raises <see cref="FilePlayRequested"/>, which <c>MainWindow</c>'s own subscriber
    /// resolves against ITS <see cref="_playback"/> field, already covered by
    /// <c>MainWindow.AttachPlaybackEngine</c> separately.</summary>
    public void AttachPlaybackEngine(PlaybackEngine playback)
    {
        if (ReferenceEquals(_playback, playback)) return;
        if (_playback != null) _playback.FileStarted -= OnFileStarted;
        _playback = playback;
        _playback.FileStarted += OnFileStarted;
        _audioBar.AttachPlaybackEngine(playback);
        HighlightCurrentFile();
    }

    public void SetOutputActive(bool active)
    {
        if (_outputActive == active) return;
        _outputActive = active;
        _outputStatusChip.Text = active ? "◉  输出中" : "○  待机";
        _outputStatusChip.Selected = active;
        _outputStatusChip.Invalidate();
        HighlightCurrentFile();
    }

    private void OpenSelectedLocation()
    {
        if (_grid.SelectedItems.Count == 0) return;
        var file = _grid.SelectedItems[0].File;
        OpenLocation(file);
    }

    private void OpenLocation(MediaFile file)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file.SourcePath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开文件位置：\n\n{ex.Message}", "打开失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private ContextMenuStrip BuildFileContextMenu()
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = ModernUi.Background,
            ForeColor = ModernUi.Text,
            Renderer = new ThemedMenuRenderer(),
        };
        // Everything above the last separator stays inside EveryStage (Preview / Program). The only
        // hand-off to Windows is the explicit last item — never a side effect of playing.
        menu.Items.Add("预览", null, (_, _) => { if (_contextFile != null) FilePlayRequested?.Invoke(_contextFile); });
        menu.Items.Add("投到大屏", null, (_, _) => { if (_contextFile != null) TakeRequested?.Invoke(_contextFile); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("循环播放", null, (_, _) => SetCompletionAndPlay(CompletionAction.Loop));
        menu.Items.Add("播完切黑", null, (_, _) => SetCompletionAndPlay(CompletionAction.BlackScreen));
        menu.Items.Add("播完回主 KV", null, (_, _) => SetCompletionAndPlay(CompletionAction.MainKeyVisual));
        menu.Items.Add("顺序播放", null, (_, _) => StartSequentialPlayback());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("停止预览", null, (_, _) => PlaybackStopRequested?.Invoke());
        var stopOutputItem = new ToolStripMenuItem("停止输出", null, (_, _) => StopProgramRequested?.Invoke());
        menu.Items.Add(stopOutputItem);
        menu.Items.Add(new ToolStripSeparator());
        var documentSettingsItem = new ToolStripMenuItem("文档播放设置...", null, (_, _) => EditDocumentSettings());
        menu.Items.Add(documentSettingsItem);
        menu.Items.Add("加入流程...", null, OnAddToActivityClick);
        menu.Items.Add("从文件库移除", null, (_, _) => OnRemoveClick(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开文件所在位置", null, (_, _) => OpenSelectedLocation());
        menu.Items.Add("使用系统默认程序打开", null, (_, _) =>
        {
            if (_contextFile != null) ExternalOpener.OpenWithDefaultApp(FindForm(), _contextFile.SourcePath);
        });
        menu.Opening += (_, _) =>
        {
            stopOutputItem.Enabled = IsProgramLive?.Invoke() == true;
            documentSettingsItem.Enabled = _contextFile?.Kind == MediaKind.Document;
            menu.BackColor = ModernUi.Surface;
            menu.ForeColor = ModernUi.Text;
            foreach (ToolStripItem item in menu.Items)
                item.ForeColor = ModernUi.Text;
        };
        return menu;
    }

    /// <summary>PDF / Word / presentation playback settings for a library file (copied into activities
    /// together with the file).</summary>
    private void EditDocumentSettings()
    {
        if (_contextFile is not { Kind: MediaKind.Document } file) return;
        using var dialog = new DocumentSettingsDialog(file);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var old = file.Document;
        file.Document = dialog.Result;
        try { _library.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            file.Document = old;
            MessageBox.Show(this, $"文档播放设置未保存：{DataDirectory.Describe(ex)}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.Document), null, DocumentSettingsDialog.Summarize(file));
    }

    private ContextMenuStrip BuildToolbarMenu()
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = ModernUi.Surface,
            ForeColor = ModernUi.Text,
            Renderer = new ThemedMenuRenderer(),
        };
        menu.Items.Add("导入文件...", null, (_, _) => ImportViaDialog());
        menu.Items.Add(new ToolStripSeparator());
        _previewMenuItem = new ToolStripMenuItem("播放 / 预览", null, (_, _) => PlayOrPreviewSelected()) { Enabled = false };
        _addToActivityMenuItem = new ToolStripMenuItem("加入流程...", null, OnAddToActivityClick) { Enabled = false };
        _removeMenuItem = new ToolStripMenuItem("移除所选文件", null, OnRemoveClick) { Enabled = false };
        menu.Items.Add(_previewMenuItem);
        menu.Items.Add(_addToActivityMenuItem);
        menu.Items.Add(_removeMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        _openLocationMenuItem = new ToolStripMenuItem("打开文件所在位置", null, (_, _) => OpenSelectedLocation()) { Enabled = false };
        menu.Items.Add(_openLocationMenuItem);
        menu.Items.Add("重新载入文件库", null, (_, _) => Refresh_());
        menu.Opening += (_, _) =>
        {
            menu.BackColor = ModernUi.Surface;
            menu.ForeColor = ModernUi.Text;
            foreach (ToolStripItem item in menu.Items) item.ForeColor = ModernUi.Text;
        };
        return menu;
    }

    private List<MediaFile> SelectedFiles() => _grid.SelectedItems.Select(item => item.File).ToList();

    /// <summary>Drag to reorder: the library order changes (and is saved); the grid follows it.</summary>
    private void OnReorder(IReadOnlyList<MediaGrid.Item> moving, MediaGrid.Item? before)
    {
        try { _library.Move(moving.Select(item => item.File.Id).ToList(), before?.File.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"顺序未保存：{DataDirectory.Describe(ex)}", "文件库保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Refresh_();
    }

    private void SetCompletionAndPlay(CompletionAction action)
    {
        var file = _contextFile;
        if (file == null) return;
        var oldValue = file.OnCompletion;
        if (oldValue != action)
        {
            file.OnCompletion = action;
            try { _library.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                file.OnCompletion = oldValue;
                MessageBox.Show(this, $"无法保存播放方式：{DataDirectory.Describe(ex)}", "保存失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _fileOpLog.LogPlaybackPropertyChanged(file.Id, nameof(MediaFile.OnCompletion), oldValue.ToString(), action.ToString());
        }
        FilePlayRequested?.Invoke(file);
    }

    private void StartSequentialPlayback()
    {
        if (_contextFile == null) return;
        if (_playback == null)
        {
            FilePlayRequested?.Invoke(_contextFile);
            return;
        }

        var files = _grid.Items.Select(item => item.File).ToList();
        int startIndex = files.FindIndex(file => file.Id == _contextFile.Id);
        if (startIndex < 0) return;

        // The library grid has no persisted activity/list object of its own. Use a transient queue
        // of copies so sequential playback follows the current visible library order without
        // mutating saved per-file settings or the activity store.
        var queue = new Activity { Name = "文件区顺序播放", DefaultPlayMode = PlayMode.SequentialAuto };
        foreach (var file in files)
        {
            var copy = file.Clone(preserveId: true);
            copy.PlayModeOverride = null;
            copy.OnCompletion = CompletionAction.NextItem;
            queue.Files.Add(copy);
        }
        _playback.RequestPlay(queue, startIndex);
    }


    public void Refresh_()
    {
        _thumbnailLoad?.Cancel();
        _thumbnailLoad?.Dispose();
        _thumbnailLoad = new CancellationTokenSource();
        foreach (var image in _thumbnailByFileId.Values) image.Dispose();
        _thumbnailByFileId.Clear();
        _visibleThumbnailKeys.Clear();
        var items = new List<MediaGrid.Item>();

        // Audio has its own persistent horizontal player area; keep it out of the visual-media
        // thumbnail grid so the "全部" view does not duplicate audio as generic file cards.
        string search = _searchBox.Text.Trim();
        foreach (var file in _library.Files.Where(f => f.Kind != MediaKind.Audio
                     && (_activeFilter == null || f.Kind == _activeFilter)
                     && (search.Length == 0 || Path.GetFileName(f.SourcePath).Contains(search, StringComparison.OrdinalIgnoreCase))))
        {
            // Meta is read when painting, so it is worked out here once (painting never touches the disk).
            items.Add(new MediaGrid.Item { File = file, Meta = CardMeta(file), ToolTip = CardToolTip(file) });
        }
        _grid.SetItems(items);

        _countLabel.Text = $"{_library.Files.Count(f => f.Kind != MediaKind.Audio)} 个文件";
        RefreshAudioBar();
        HighlightCurrentFile();
        _emptyStateLabel.Text = search.Length > 0 ? $"没有名称包含“{search}”的文件"
            : _library.Files.All(f => f.Kind == MediaKind.Audio) ? "文件库还是空的\n\n点击“＋ 导入”或将文件拖到这里（音频会进入下方的背景音乐歌单）"
            : "这一类还没有文件";
        _emptyStateLabel.Visible = _grid.Items.Count == 0;
        PositionEmptyState();
        if (IsHandleCreated) _ = LoadThumbnailsAsync(_thumbnailLoad.Token);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_thumbnailLoad != null) _ = LoadThumbnailsAsync(_thumbnailLoad.Token);
    }


    private void PositionEmptyState()
    {
        _emptyStateLabel.Location = new Point(
            Math.Max(0, (ClientSize.Width - _emptyStateLabel.Width) / 2),
            Math.Max(76, (ClientSize.Height - _emptyStateLabel.Height) / 2));
    }

    private void OnFileStarted(MediaFile file)
    {
        if (InvokeRequired)
        {
            BeginInvoke(HighlightCurrentFile);
            return;
        }
        HighlightCurrentFile();
    }

    /// <summary>Links the floating preview's current output back to the library without changing
    /// the operator's multi-selection. Activity files are clones, so source path is the stable link
    /// when reference identity does not match a library entry.</summary>
    private void OnPreviewStateChanged(PlaybackChannelState state)
    {
        HighlightCurrentFile();
        _grid.Invalidate();
    }

    private void HighlightCurrentFile()
    {
        // Follows what is loaded in the Preview monitor (independent of whether Program is on air).
        MediaFile? current = _playback is { State: not PlaybackChannelState.Idle } preview ? preview.CurrentFile : null;
        if (current == null) return;
        var playing = _grid.Items.FirstOrDefault(item => SameFile(item.File, current));
        if (playing != null) _grid.EnsureVisible(playing);
        _grid.Invalidate();
    }

    /// <summary>Rebinds the single <see cref="AudioPlayerBar"/> to the current audio files — called
    /// from <see cref="Refresh_()"/> on the same cadence that rebuilds <see cref="_grid"/>. With no
    /// audio files the bar stays visible and shows its「导入音频」placeholder.</summary>
    private void RefreshAudioBar()
    {
        _audioBar.SetAudioFiles(_library.Files.Where(f => f.Kind == MediaKind.Audio).ToList());
    }

    /// <summary>音频区「＋ 加入活动音轨」：选一个活动，把这首音频的副本作为背景音乐
    /// （<see cref="MediaFile.IsBackgroundAudio"/>）加入——与 <see cref="OnAddToActivityClick"/> 一样是
    /// 独立副本，不引用文件库条目。</summary>
    private void OnAddAudioToActivityTrack(MediaFile file)
    {
        if (_scenarioStore.Scenarios.Count == 0)
        {
            MessageBox.Show(this, "还没有活动方案，请先在「活动」页创建活动。", "加入活动音轨",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new ActivityPickerDialog(_scenarioStore.Scenarios, _scenarioStore.CurrentScenarioId);
        if (dialog.ShowDialog(this) != DialogResult.OK
            || dialog.SelectedScenario == null || dialog.SelectedActivity == null)
            return;

        var addition = file.Clone();
        addition.IsBackgroundAudio = true;
        dialog.SelectedActivity.Files.Add(addition);
        try { _scenarioRepository.Save(_scenarioStore); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dialog.SelectedActivity.Files.Remove(addition);
            MessageBox.Show(this, $"未加入活动音轨：{DataDirectory.Describe(ex)}", "方案保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _fileOpLog.LogActivityModified(dialog.SelectedScenario.Id, dialog.SelectedActivity.Id, dialog.SelectedActivity.Name);

        MessageBox.Show(this,
            $"已将 \"{Path.GetFileName(file.SourcePath)}\" 作为背景音乐加入活动 \"{dialog.SelectedActivity.Name}\"。",
            "加入活动音轨", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }



    private void PlayOrPreviewSelected()
    {
        if (_grid.SelectedItems.Count > 0 && _grid.SelectedItems[0].File is { } file)
            FilePlayRequested?.Invoke(file);
    }


}
