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
/// PLANNING.md §11's "批量选择" now covers two of its three actions: <see cref="_listView"/> allows
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
public sealed class FilesPanel : UserControl
{
    private readonly FileLibraryStore _library;
    private readonly FileOperationLogger _fileOpLog;
    private readonly ScenarioStore _scenarioStore;
    private readonly ScenarioRepository _scenarioRepository;
    // Not readonly — see AttachPlaybackEngine's own doc comment: PLANNING.md §5's runtime
    // monitor-hot-plug binding needs to replace this from null to a real PlaybackEngine well after
    // this panel is already constructed.
    private PlaybackEngine? _playback;
    private readonly ListView _listView;
    private readonly ImageList _thumbnails;
    // Managed keyed images are reliable for owner-drawn Tile items even when the native
    // LargeImageList is intentionally not assigned.
    private readonly Dictionary<string, Image> _thumbnailByFileId = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Minimum card width per size (小 / 中 / 大); columns = grid width / this.</summary>
    private static readonly int[] MinCardWidths = { 140, 180, 250 };

    /// <summary>What Program shows right now (null when nothing local is on air) — the 「大屏」 badge.</summary>
    public Func<MediaFile?>? ProgramFile { get; set; }

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

    /// <summary>Whether Program is currently on air — enables the context menu's "停止输出".</summary>
    public Func<bool>? IsProgramLive { get; set; }

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
        var pageTitle = new Label
        {
            Text = "文件库", AutoSize = true, ForeColor = ModernUi.Text,
            Font = new Font(ModernUi.UiFontSemibold, 15F), BackColor = Color.Transparent,
        };
        _countLabel = new Label { AutoSize = true, ForeColor = ModernUi.Muted, Font = new Font(ModernUi.UiFont, 9.5F), BackColor = Color.Transparent };
        MediaKind?[] filters = { null, MediaKind.Image, MediaKind.Video, MediaKind.Document, MediaKind.Audio };
        var filterTabs = new SegmentedFilter(new[] { "全部", "图片", "视频", "文档", "音频" }) { Height = 34 };
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
            UpdateTileSize();
            _listView?.Invalidate();
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

        // Not assigned to _listView.LargeImageList on purpose: this ListView is fully OwnerDraw and
        // DrawMediaCard looks thumbnails up directly from _thumbnails.Images[key], so the ListView
        // never needs to realize an ImageList handle. Assigning it made WinForms realize that handle
        // on the ListView's own handle creation, which throws "Parameter is not valid" while the
        // list is still empty (an ImageList with no images yet) — a caught-but-logged ThreadException
        // that spammed the crash log on every empty-library show. Keeping the ImageList purely as a
        // keyed bitmap cache for owner-draw avoids that entirely.
        _thumbnails = new ImageList { ImageSize = new Size(240, 150), ColorDepth = ColorDepth.Depth32Bit };
        _listView = new MediaCardGridView
        {
            Dock = DockStyle.Fill,
            View = View.Tile,
            TileSize = new Size(200, 144),
            OwnerDraw = true,
            // PLANNING.md §11 "批量选择": Ctrl/Shift+点击 multi-select now works, and both "删除"
            // (below) and "加入活动" (OnAddToActivityClick) handle any number of selected items —
            // see class doc comment for why the remaining action, "统一设置属性", still isn't
            // attempted, and why this reuses the existing always-visible toolbar buttons rather than
            // building a separate floating one for the same enable/disable-on-selection behavior
            // "选中后悬浮工具栏出现" already describes in spirit.
            MultiSelect = true,
            ShowItemToolTips = true,
            BackColor = ModernUi.Background,
            ForeColor = ModernUi.Text,
            BorderStyle = BorderStyle.None,
            Font = new Font(ModernUi.UiFont, 10F),
        };
        _listView.DrawItem += DrawMediaCard;
        _listView.SelectedIndexChanged += (_, _) =>
        {
            bool hasSelection = _listView.SelectedItems.Count > 0;
            _removeMenuItem.Enabled = hasSelection;
            _addToActivityMenuItem.Enabled = hasSelection;
            _previewMenuItem.Enabled = hasSelection;
            _openLocationMenuItem.Enabled = hasSelection;
        };
        // Single click: show the file in the 预览 monitor. Double click: put it straight on the big screen.
        // Ctrl/Shift clicks keep multi-selecting and the card's ⋯ keeps opening its menu.
        _listView.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || (ModifierKeys & (Keys.Control | Keys.Shift)) != 0) return;
            if (_listView.GetItemAt(e.X, e.Y) is not { } item || CardLayout(item.Bounds).More.Contains(e.Location)) return;
            PlayOrPreviewSelected();
        };
        _listView.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || (ModifierKeys & (Keys.Control | Keys.Shift)) != 0) return;
            if (_listView.GetItemAt(e.X, e.Y) is not { Tag: MediaFile file } item || CardLayout(item.Bounds).More.Contains(e.Location)) return;
            TakeRequested?.Invoke(file);
        };
        _fileContextMenu = BuildFileContextMenu();
        _listView.MouseUp += OnFileListMouseUp;

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _listView.DragEnter += OnDragEnter;
        _listView.DragDrop += OnDragDrop;
        _listView.AllowDrop = true;

        // Columns follow the grid width and the chosen card size (see UpdateTileSize).
        _listView.SizeChanged += (_, _) => UpdateTileSize();

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
        var audioTitleFont = new Font(ModernUi.UiFontSemibold, 10.5F);
        var audioHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = TextRenderer.MeasureText("音频播放区 Ag", audioTitleFont).Height + 8,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            BackColor = Color.Transparent, Margin = Padding.Empty, Padding = new Padding(2, 2, 0, 0),
        };
        audioHeader.Controls.Add(new Label { Text = "音频播放区", AutoSize = true, ForeColor = ModernUi.Text, Font = audioTitleFont, Margin = new Padding(0, 0, 12, 0) });
        audioHeader.Controls.Add(new Label { Text = "管理音频文件，用于背景音乐播放，可加入活动音轨", AutoSize = true, ForeColor = ModernUi.Muted, Font = new Font(ModernUi.UiFont, 9F), Margin = new Padding(0, 3, 0, 0) });
        var audioCard = new GlassPanel
        {
            Dock = DockStyle.Bottom, Padding = new Padding(10, 4, 10, 8), CornerRadius = 10,
            GlassTint = Color.FromArgb(190, 12, 29, 49),
        };
        audioCard.Controls.Add(_audioBar);
        audioCard.Controls.Add(audioHeader);
        // The bar picks its own height (one row, or two when narrow); the card wraps it.
        void FitAudioCard() => audioCard.Height = audioCard.Padding.Vertical + audioHeader.Height + _audioBar.Height;
        _audioBar.SizeChanged += (_, _) => FitAudioCard();
        FitAudioCard();
        var audioCardGap = new Panel { Dock = DockStyle.Bottom, Height = 8, BackColor = Color.Transparent };

        // Control.Controls.Add order determines Dock z-order for same-DockStyle siblings: the LAST
        // one added claims its edge first. The grid (Fill) goes first so it takes whatever is left;
        // the header claims the top, the audio card and its gap the bottom.
        Controls.Add(_listView);
        Controls.Add(header);
        Controls.Add(audioCardGap);
        Controls.Add(audioCard);

        _emptyStateLabel = new Label
        {
            Text = "文件库还是空的\n\n点击“＋ 导入”或将文件拖到这里",
            ForeColor = ModernUi.Muted,
            BackColor = ModernUi.Surface,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(ModernUi.UiFont, 12F),
            Size = new Size(420, 110),
            Visible = false,
        };
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
            if (programPath == _lastProgramPath) return;
            _lastProgramPath = programPath;
            _listView.Invalidate();
        };
        _audioBarRefreshTimer.Start();

        Refresh_();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnailLoad?.Cancel();
            _thumbnailLoad?.Dispose();
            _thumbnailLoad = null;
            _thumbnails.Dispose();
            _toolbarMenu.Dispose();
            foreach (var image in _thumbnailByFileId.Values) image.Dispose();
            _thumbnailByFileId.Clear();
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
    /// moves that subscription from any previous engine to the newly attached one. <see cref="_listView"/>'s own double-click doesn't read <see cref="_playback"/> at all —
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
        if (_listView.SelectedItems.Count == 0 || _listView.SelectedItems[0].Tag is not MediaFile file) return;
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

    private void OnFileListMouseUp(object? sender, MouseEventArgs e)
    {
        var hit = _listView.HitTest(e.Location).Item;
        if (hit?.Tag is not MediaFile file)
        {
            // Right-click on empty grid space: the library-level actions (import, reload, ...).
            if (e.Button == MouseButtons.Right) _toolbarMenu.Show(_listView, e.Location);
            return;
        }
        bool moreClicked = e.Button == MouseButtons.Left && CardLayout(hit.Bounds).More.Contains(e.Location);
        if (e.Button != MouseButtons.Right && !moreClicked) return;

        if (!hit.Selected)
        {
            foreach (ListViewItem item in _listView.SelectedItems) item.Selected = false;
            hit.Selected = true;
        }
        _listView.FocusedItem = hit;
        _contextFile = file;
        _fileContextMenu.Show(_listView, e.Location);
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

        var files = _listView.Items.Cast<ListViewItem>()
            .Select(item => item.Tag as MediaFile)
            .Where(file => file != null)
            .Cast<MediaFile>()
            .ToList();
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

    internal sealed class FileMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => ModernUi.Surface;
        // Windows 11 draws its own outline around a rounded menu window; a square inner border would
        // show at the corners. Hover is a soft fill without an accent outline, as in Win11 menus.
        public override Color MenuBorder => WindowsAppearance.IsWindows11 ? ModernUi.Surface : ModernUi.Border;
        public override Color MenuItemBorder => ModernUi.SurfaceRaised;
        public override Color MenuItemSelected => ModernUi.SurfaceRaised;
        public override Color MenuItemSelectedGradientBegin => ModernUi.SurfaceRaised;
        public override Color MenuItemSelectedGradientEnd => ModernUi.SurfaceRaised;
        public override Color ImageMarginGradientBegin => ModernUi.Surface;
        public override Color ImageMarginGradientMiddle => ModernUi.Surface;
        public override Color ImageMarginGradientEnd => ModernUi.Surface;
        public override Color SeparatorDark => ModernUi.Border;
        public override Color SeparatorLight => ModernUi.Surface;
    }

    private sealed class MediaCardGridView : ListView
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0014 && m.WParam != IntPtr.Zero) // WM_ERASEBKGND
            {
                // Paint the hosting card behind the grid, so the grid reads as part of the 文件 card
                // rather than an opaque rectangle inside it.
                using var graphics = Graphics.FromHdc(m.WParam);
                if (Parent != null)
                {
                    var state = graphics.Save();
                    graphics.TranslateTransform(-Left, -Top);
                    using (var args = new PaintEventArgs(graphics, Bounds))
                        InvokePaintBackground(Parent, args);
                    graphics.Restore(state);
                }
                else
                {
                    using var background = new SolidBrush(ModernUi.Background);
                    graphics.FillRectangle(background, ClientRectangle);
                }
                m.Result = new IntPtr(1);
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>效果图的分段筛选「全部 | 图片 | 视频 | 文档」：一个圆角容器，选中段填主蓝，未选中段之间
    /// 有细分隔线。左右方向键切换。</summary>
    private sealed class SegmentedFilter : Control
    {
        private readonly string[] _labels;
        private int _selectedIndex;

        public event EventHandler? SelectedIndexChanged;

        public SegmentedFilter(string[] labels)
        {
            _labels = labels;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            Font = new Font(ModernUi.UiFont, 10F);
            AccessibleRole = AccessibleRole.PageTabList;
            AccessibleName = "文件类型筛选";
        }

        /// <summary>Narrow-window fallback: segments may shrink to fit just their two-character labels.</summary>
        public bool Compact { get; set; }

        public int MinimumWidth => _labels.Length * (Compact ? 44 : 56);
        public int MaximumWidth => _labels.Length * 76;

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                value = Math.Clamp(value, 0, _labels.Length - 1);
                if (value == _selectedIndex) return;
                _selectedIndex = value;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private Rectangle Segment(int index)
        {
            var inner = Rectangle.Inflate(ClientRectangle, -3, -3);
            int left = inner.Left + inner.Width * index / _labels.Length;
            int right = inner.Left + inner.Width * (index + 1) / _labels.Length;
            return Rectangle.FromLTRB(left, inner.Top, right, inner.Bottom);
        }

        protected override void OnPaintBackground(PaintEventArgs e) =>
            ButtonRenderer.DrawParentBackground(e.Graphics, ClientRectangle, this);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var outer = Rectangle.Inflate(ClientRectangle, -1, -1);
            outer.Width -= 1;
            outer.Height -= 1;
            using (var path = RoundedCard(outer, 8))
            using (var fill = new SolidBrush(ModernUi.Surface))
            using (var border = new Pen(ModernUi.Border))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            using var divider = new Pen(ModernUi.Border);
            for (int i = 0; i < _labels.Length; i++)
            {
                var segment = Segment(i);
                bool selected = i == _selectedIndex;
                if (selected)
                {
                    using var path = RoundedCard(segment, 6);
                    using var accent = new SolidBrush(ModernUi.Accent);
                    g.FillPath(accent, path);
                }
                else if (i > 0 && i - 1 != _selectedIndex)
                {
                    g.DrawLine(divider, segment.Left, segment.Top + 6, segment.Left, segment.Bottom - 6);
                }
                TextRenderer.DrawText(g, _labels[i], Font, segment, selected ? ModernUi.AccentForeground : ModernUi.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(Segment(_selectedIndex), -3, -3));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            for (int i = 0; i < _labels.Length; i++)
                if (Segment(i).Contains(e.Location)) SelectedIndex = i;
        }

        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Left or Keys.Right || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Left or Keys.Right)
            {
                SelectedIndex += e.KeyCode == Keys.Left ? -1 : 1;
                e.Handled = true;
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    private void ImportViaDialog()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "导入文件到文件库" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ImportPaths(dialog.FileNames);
    }

    /// <summary>音频区空状态的「导入音频」：同 <see cref="ImportViaDialog"/>，只是默认筛选音频格式。</summary>
    private void ImportAudioViaDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Multiselect = true, Title = "导入音频到文件库",
            Filter = "音频文件|*.mp3;*.wav;*.flac;*.aac;*.m4a;*.wma|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ImportPaths(dialog.FileNames);
    }

    /// <summary>Sizes tiles so the row fills the grid width with as many cards as the chosen size
    /// allows. File date lives in the item tooltip set in <see cref="Refresh_"/>.</summary>
    private void UpdateTileSize()
    {
        // Fill the full grid width (cards then line up with the header on both sides); only when the
        // rows overflow is the vertical scrollbar width set aside.
        int minimum = LogicalToDeviceUnits(MinCardWidths[_cardSize]);
        (int Columns, Size Tile) Fit(int available)
        {
            int columns = Math.Max(1, available / minimum);
            int width = Math.Max(minimum / 2, available / columns);
            return (columns, new Size(width, TileHeightFor(width)));
        }

        var fit = Fit(_listView.Width - 2);
        int rows = (_listView.Items.Count + fit.Columns - 1) / fit.Columns;
        if (rows * fit.Tile.Height > _listView.Height)
            fit = Fit(_listView.Width - SystemInformation.VerticalScrollBarWidth - 2);
        if (_listView.TileSize != fit.Tile) _listView.TileSize = fit.Tile;
    }

    // One file card inside its grid tile, per the design: 12px between cards, the thumbnail (16:10)
    // edge to edge at the top, then the name (with ⋯) and a muted meta line.
    private const int CardGap = 12;
    private const int NameRowHeight = 20;
    private const int MetaRowHeight = 16;

    private readonly record struct CardGeometry(Rectangle Card, Rectangle Thumb, Rectangle Name, Rectangle Meta, Rectangle More);

    /// <summary>Card geometry shared by painting (<see cref="DrawMediaCard"/>), the ⋯ hit test and
    /// tile sizing, so they cannot drift apart.</summary>
    private static CardGeometry CardLayout(Rectangle tile)
    {
        var card = Rectangle.Inflate(tile, -CardGap / 2, -CardGap / 2);
        var thumb = new Rectangle(card.Left, card.Top, Math.Max(1, card.Width), Math.Max(1, card.Width * 10 / 16));
        var more = new Rectangle(card.Right - 28, thumb.Bottom + 6, 24, NameRowHeight);
        var name = Rectangle.FromLTRB(card.Left + 9, more.Top, more.Left - 2, more.Bottom);
        var meta = new Rectangle(card.Left + 9, name.Bottom, Math.Max(1, card.Width - 18), MetaRowHeight);
        return new CardGeometry(card, thumb, name, meta, more);
    }

    private static int TileHeightFor(int tileWidth) =>
        CardLayout(new Rectangle(0, 0, tileWidth, 10_000)).Meta.Bottom + 8 + CardGap / 2;

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            ImportPaths(paths);
    }

    /// <summary>PLANNING.md §11 "批量选择" — the "删除" half of it (see class doc comment for the
    /// other two actions §11 describes for the same selection). Handles any number of selected
    /// items, not just one, now that <see cref="_listView"/> allows <c>MultiSelect</c>.</summary>
    private void OnRemoveClick(object? sender, EventArgs e)
    {
        RemoveFromLibrary(_listView.SelectedItems.Cast<ListViewItem>()
            .Select(item => item.Tag as MediaFile)
            .Where(file => file != null)
            .Cast<MediaFile>()
            .ToList());
    }

    /// <summary>Confirm, remove, log, refresh — shared by the grid (selection) and the audio
    /// selector (one audio file).</summary>
    private void RemoveFromLibrary(IReadOnlyList<MediaFile> files)
    {
        if (files.Count == 0) return;

        string prompt = files.Count == 1
            ? $"确定要从文件库中移除 \"{Path.GetFileName(files[0].SourcePath)}\" 吗？"
            : $"确定要从文件库中移除选中的 {files.Count} 个文件吗？";
        var confirm = MessageBox.Show(this,
            prompt + "\n已经添加到某个活动里的副本不受影响——活动保存的是独立拷贝，不是对这个文件库条目的引用。",
            "移除文件", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        foreach (var file in files)
        {
            try { _library.Remove(file.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"移除未完成：{DataDirectory.Describe(ex)}", "文件库保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
            }
            _fileOpLog.LogFileRemoved(file.Id, file.SourcePath);
        }
        Refresh_();
    }

    /// <summary>PLANNING.md §11 "批量选择"'s "加入活动" half — see class doc comment for why this
    /// was previously left unimplemented, and <see cref="ActivityPickerDialog"/> for the picker this
    /// needed that didn't exist until now. Handles any number of selected files at once, same as
    /// <see cref="OnRemoveClick"/>. Deep-copies each selected file (<see cref="MediaFile.Clone"/>)
    /// into the chosen activity — same "always an independent copy, never a shared reference"
    /// behavior <c>ActivitiesPanel</c>'s own "添加文件..." already established (see this project's
    /// README "已知风险" #22). Doesn't refresh <c>ActivitiesPanel</c>'s own tree directly: this
    /// panel has no reference to it, and <c>MainWindow.ShowPanel</c> already calls
    /// <c>ActivitiesPanel.RefreshTree()</c> every time the operator navigates there, which is the
    /// same lazy-refresh-on-show convention every panel in this app already relies on (see e.g.
    /// <c>SettingsPanel.Refresh_</c>'s own doc comment on why only the 显示 tab needs it) rather than
    /// pushing a live update across panels that aren't currently visible anyway.</summary>
    private void OnAddToActivityClick(object? sender, EventArgs e)
    {
        var files = _listView.SelectedItems.Cast<ListViewItem>()
            .Select(item => item.Tag as MediaFile)
            .Where(file => file != null)
            .Cast<MediaFile>()
            .ToList();
        if (files.Count == 0 || _scenarioStore.Scenarios.Count == 0) return;

        using var dialog = new ActivityPickerDialog(_scenarioStore.Scenarios, _scenarioStore.CurrentScenarioId);
        if (dialog.ShowDialog(this) != DialogResult.OK
            || dialog.SelectedScenario == null || dialog.SelectedActivity == null)
            return;

        var additions = files.Select(file => file.Clone()).ToList();
        dialog.SelectedActivity.Files.AddRange(additions);
        try { _scenarioRepository.Save(_scenarioStore); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var addition in additions) dialog.SelectedActivity.Files.Remove(addition);
            MessageBox.Show(this, $"未加入活动：{DataDirectory.Describe(ex)}", "方案保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _fileOpLog.LogActivityModified(dialog.SelectedScenario.Id, dialog.SelectedActivity.Id, dialog.SelectedActivity.Name);

        MessageBox.Show(this,
            files.Count == 1
                ? $"已将 \"{Path.GetFileName(files[0].SourcePath)}\" 加入活动 \"{dialog.SelectedActivity.Name}\"。"
                : $"已将选中的 {files.Count} 个文件加入活动 \"{dialog.SelectedActivity.Name}\"。",
            "加入活动", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ImportPaths(IEnumerable<string> paths)
    {
        var rejected = new List<string>();
        foreach (var path in paths)
        {
            MediaFile? imported;
            try { imported = _library.Import(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"导入未完成：{DataDirectory.Describe(ex)}\n此前成功导入的文件已保留。", "文件库保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
            }
            if (imported == null) rejected.Add(Path.GetFileName(path));
            else _fileOpLog.LogFileImported(imported.Id, imported.SourcePath);
        }
        Refresh_();

        if (rejected.Count > 0)
        {
            MessageBox.Show(this,
                $"以下文件的类型不受支持，未导入：\n{string.Join("\n", rejected)}",
                "导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Call after the library changes from outside this control (e.g. a file removed
    /// elsewhere) to re-pull the list. Named with a trailing underscore only to avoid colliding
    /// with <see cref="Control.Refresh"/>, which does something unrelated (repaint).</summary>
    public void Refresh_()
    {
        _thumbnailLoad?.Cancel();
        _thumbnailLoad?.Dispose();
        _thumbnailLoad = new CancellationTokenSource();
        foreach (var image in _thumbnailByFileId.Values) image.Dispose();
        _thumbnailByFileId.Clear();
        _thumbnails.Images.Clear();
        _listView.Items.Clear();

        // Audio has its own persistent horizontal player area; keep it out of the visual-media
        // thumbnail grid so the "全部" view does not duplicate audio as generic file cards.
        string search = _searchBox.Text.Trim();
        foreach (var file in _library.Files.Where(f => (_activeFilter == null
                         ? f.Kind != MediaKind.Audio
                         : f.Kind == _activeFilter)
                     && (search.Length == 0 || Path.GetFileName(f.SourcePath).Contains(search, StringComparison.OrdinalIgnoreCase))))
        {
            string key = file.Id.ToString();
            // ImageList.Images.Add copies the bitmap's pixel data into its own native image list
            // handle — it does not take ownership of (or ever dispose) the Image instance passed in,
            // so without this `using`, every BuildThumbnail() result here would leak its GDI+ handle
            // until the next GC finalizer pass happens to run. This matters more than a one-off leak
            // would: Refresh_() re-runs on every filter tab click, import, and removal (MainWindow
            // also calls it every time the user switches to the 文件 tab), on what PLANNING.md §14.4
            // frames as an always-on, unattended device — repeated small leaks like this are exactly
            // the shape of thing that eventually exhausts the process's GDI object quota on a machine
            // that's never restarted.
            var item = new ListViewItem(Path.GetFileName(file.SourcePath), key) { Tag = file, ToolTipText = CardToolTip(file) };
            item.SubItems.Add(CardMeta(file)); // read by DrawMediaCard, so painting never touches the disk
            _listView.Items.Add(item);
        }

        _countLabel.Text = $"{_library.Files.Count} 个文件";
        UpdateTileSize();
        RefreshAudioBar();
        HighlightCurrentFile();
        _emptyStateLabel.Text = search.Length > 0 ? $"没有名称包含“{search}”的文件"
            : _library.Files.Count == 0 ? "文件库还是空的\n\n点击“＋ 导入”或将文件拖到这里"
            : _activeFilter == null && _library.Files.All(f => f.Kind == MediaKind.Audio) ? "暂无图片、视频或文档\n\n音频文件在「音频」筛选和下方播放区"
            : "这一类还没有文件";
        _emptyStateLabel.Visible = _listView.Items.Count == 0;
        PositionEmptyState();
        if (IsHandleCreated) _ = LoadThumbnailsAsync(_thumbnailLoad.Token);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_thumbnailLoad != null) _ = LoadThumbnailsAsync(_thumbnailLoad.Token);
    }

    private async Task LoadThumbnailsAsync(CancellationToken token)
    {
        // One running pass per token: Refresh_ and OnHandleCreated (handles are recreated on some property
        // changes) can both start a pass with the current token, and two interleaved passes raced the cache
        // eviction below (audit B-7). A later call with the same token after the pass ended runs again.
        if (_thumbnailPassRunning && _thumbnailPassToken == token) return;
        _thumbnailPassToken = token;
        _thumbnailPassRunning = true;
        var files = _listView.Items.Cast<ListViewItem>().Select(item => (MediaFile)item.Tag!).ToArray();
        try
        {
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                await _thumbnailWorker.WaitAsync(token);
                Image thumbnail;
                string? cacheKey = null;
                try
                {
                    cacheKey = await Task.Run(() => ThumbnailCacheKey(file), token);
                    token.ThrowIfCancellationRequested();
                    if (cacheKey != null && _thumbnailCache.TryGetValue(cacheKey, out var cached))
                        thumbnail = (Image)cached.Clone();
                    else
                        thumbnail = await Task.Run(() =>
                        {
                            // One unreadable file gets a warning tile; an exception type BuildThumbnail's
                            // per-kind filters miss (e.g. InvalidDataException from a PDF with bad page
                            // sizes) used to end this whole pass, leaving every later card blank.
                            try { return BuildThumbnail(file); }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Trace.TraceError("Thumbnail for {0} failed: {1}", file.SourcePath, ex);
                                return BuildKindTile("预览不可用", Color.FromArgb(120, 40, 46), Color.FromArgb(186, 55, 65));
                            }
                        }, token);
                }
                finally { _thumbnailWorker.Release(); }
                using (thumbnail)
                {
                    if (token.IsCancellationRequested || IsDisposed || Disposing) return;
                    if (cacheKey != null && !_thumbnailCache.ContainsKey(cacheKey))
                    {
                        while (_thumbnailCache.Count >= ThumbnailCacheCapacity && _thumbnailCacheOrder.TryDequeue(out var oldest))
                        {
                            _thumbnailCache.Remove(oldest, out var evicted);
                            evicted?.Dispose();
                        }
                        _thumbnailCache.Add(cacheKey, (Image)thumbnail.Clone());
                        _thumbnailCacheOrder.Enqueue(cacheKey);
                    }
                    string key = file.Id.ToString();
                    if (!_thumbnailByFileId.ContainsKey(key))
                        _thumbnailByFileId.Add(key, (Image)thumbnail.Clone());
                    if (!_thumbnails.Images.ContainsKey(key)) _thumbnails.Images.Add(key, thumbnail);
                    _listView.Invalidate();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Thumbnail loading failed: {0}", ex);
        }
        finally
        {
            if (_thumbnailPassToken == token) _thumbnailPassRunning = false;
        }
    }

    private static string? ThumbnailCacheKey(MediaFile file)
    {
        try
        {
            var info = new FileInfo(file.SourcePath);
            return info.Exists ? $"{file.Kind}|{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
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
        _listView.Invalidate();
    }

    private void HighlightCurrentFile()
    {
        // Follows what is loaded in the Preview monitor (independent of whether Program is on air).
        MediaFile? current = _playback is { State: not PlaybackChannelState.Idle } preview ? preview.CurrentFile : null;
        foreach (ListViewItem item in _listView.Items)
        {
            bool playing = item.Tag is MediaFile file && current != null
                && (ReferenceEquals(file, current)
                    || string.Equals(file.SourcePath, current.SourcePath, StringComparison.OrdinalIgnoreCase));
            string fileName = item.Tag is MediaFile tagged ? Path.GetFileName(tagged.SourcePath) : item.Text.TrimStart('▶', ' ');
            item.Text = playing ? $"▶ {fileName}" : fileName;
            if (playing) item.EnsureVisible();
        }
    }

    /// <summary>Rebinds the single <see cref="AudioPlayerBar"/> to the current audio files — called
    /// from <see cref="Refresh_()"/> on the same cadence that rebuilds <see cref="_listView"/>. With no
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

    internal static Image BuildThumbnail(MediaFile file)
    {
        const int tw = 240, th = 150;
        if (file.Kind == MediaKind.Document && new[] { ".docx", ".pptx", ".xlsx" }.Contains(Path.GetExtension(file.SourcePath), StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var embedded = EveryStage.Terminal.ContentEngine.OfficeThumbnailReader.Read(file.SourcePath, new Size(tw, th));
                if (embedded != null)
                {
                    var thumbnail = new Bitmap(tw, th);
                    try
                    {
                        using var graphics = Graphics.FromImage(thumbnail);
                        graphics.Clear(Color.FromArgb(20, 37, 58));
                        graphics.DrawImageUnscaled(embedded, (tw - embedded.Width) / 2, (th - embedded.Height) / 2);
                        return thumbnail;
                    }
                    catch { thumbnail.Dispose(); throw; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
            {
                return BuildKindTile("文档预览不可用", Color.FromArgb(120, 40, 46), Color.FromArgb(186, 55, 65));
            }
        }
        if (file.Kind == MediaKind.Document && string.Equals(Path.GetExtension(file.SourcePath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var page = EveryStage.Terminal.ContentEngine.PdfContentRenderer.CreateThumbnail(file.SourcePath, new Size(tw, th));
                var thumbnail = new Bitmap(tw, th);
                try
                {
                    using var graphics = Graphics.FromImage(thumbnail);
                    graphics.Clear(Color.FromArgb(20, 37, 58));
                    graphics.DrawImageUnscaled(page, (tw - page.Width) / 2, (th - page.Height) / 2);
                    return thumbnail;
                }
                catch { thumbnail.Dispose(); throw; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException
                or PdfiumViewer.PdfException or DllNotFoundException)
            {
                return BuildKindTile("PDF 预览不可用", Color.FromArgb(120, 40, 46), Color.FromArgb(186, 55, 65));
            }
        }
        if (file.Kind == MediaKind.Image)
        {
            try
            {
                using var stream = File.OpenRead(file.SourcePath);
                using var loaded = Image.FromStream(stream);
                EveryStage.Terminal.ContentEngine.ExifOrientation.Apply(loaded); // same orientation as playback
                var thumbnail = new Bitmap(tw, th);
                using var graphics = Graphics.FromImage(thumbnail);
                graphics.Clear(Color.FromArgb(20, 37, 58));
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                // Cover-fill so the design's edge-to-edge thumbnail look holds for any aspect ratio.
                float scale = Math.Max((float)tw / loaded.Width, (float)th / loaded.Height);
                int width = Math.Max(1, (int)(loaded.Width * scale));
                int height = Math.Max(1, (int)(loaded.Height * scale));
                graphics.DrawImage(loaded, (tw - width) / 2, (th - height) / 2, width, height);
                return thumbnail;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException or ArgumentException)
            {
                // Missing/moved/corrupt file since it was imported — show a warning tile rather
                // than let a bad file crash the whole library view.
                return BuildKindTile("文件不可用", Color.FromArgb(120, 40, 46), Color.FromArgb(186, 55, 65));
            }
        }

        if (file.Kind == MediaKind.Video)
        {
            try
            {
                using var shellPreview = EveryStage.Terminal.ContentEngine.ShellThumbnailReader.Read(file.SourcePath, new Size(tw, th));
                if (shellPreview != null)
                {
                    var thumbnail = new Bitmap(tw, th);
                    try
                    {
                        using var graphics = Graphics.FromImage(thumbnail);
                        graphics.Clear(Color.FromArgb(20, 37, 58));
                        float scale = Math.Max((float)tw / shellPreview.Width, (float)th / shellPreview.Height);
                        int width = Math.Max(1, (int)(shellPreview.Width * scale));
                        int height = Math.Max(1, (int)(shellPreview.Height * scale));
                        graphics.DrawImage(shellPreview, (tw - width) / 2, (th - height) / 2, width, height);
                        return thumbnail;
                    }
                    catch { thumbnail.Dispose(); throw; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException)
            {
                return BuildKindTile("视频预览不可用", Color.FromArgb(120, 40, 46), Color.FromArgb(186, 55, 65));
            }
        }

        // Non-image kinds: a clean tinted tile; the colored type badge is drawn over it by
        // DrawMediaCard (so the tile itself no longer carries a letter/caption).
        return file.Kind switch
        {
            MediaKind.Video => BuildKindTile(null, Color.FromArgb(28, 24, 54), Color.FromArgb(40, 34, 78)),
            MediaKind.Document => BuildKindTile(null, Color.FromArgb(30, 44, 70), Color.FromArgb(24, 36, 58)),
            MediaKind.Audio => BuildKindTile(null, Color.FromArgb(20, 44, 42), Color.FromArgb(18, 38, 40)),
            _ => BuildKindTile(null, Color.FromArgb(24, 42, 66), Color.FromArgb(20, 37, 58)),
        };
    }

    private static bool SameFile(MediaFile a, MediaFile b) =>
        ReferenceEquals(a, b) || string.Equals(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase);

    private void DrawMediaCard(object? sender, DrawListViewItemEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var layout = CardLayout(e.Bounds);
        bool selected = e.Item.Selected;
        var media = e.Item.Tag as MediaFile;
        bool inPreview = media != null && _playback is { State: not PlaybackChannelState.Idle, CurrentFile: { } current } && SameFile(media, current);
        bool onProgram = media != null && ProgramFile?.Invoke() is { } programFile && SameFile(media, programFile);

        // Card: surface fill; the border says where the file is — 大屏 (danger), 预览 (accent),
        // selected (accent, thinner), otherwise a faint line.
        var cardRect = layout.Card;
        cardRect.Width -= 1;
        cardRect.Height -= 1;
        Color borderColor = onProgram ? ModernUi.Danger : inPreview || selected ? ModernUi.Accent : Blend(ModernUi.Surface, ModernUi.Border, 0.45);
        float borderWidth = onProgram || inPreview ? 2F : selected ? 1.6F : 1F;
        using (var path = RoundedCard(cardRect, 9))
        {
            using (var fill = new SolidBrush(selected ? Blend(ModernUi.Surface, ModernUi.Accent, 0.10) : ModernUi.Surface))
                g.FillPath(fill, path);

            var state = g.Save();
            g.SetClip(path);
            var imageRect = layout.Thumb;
            using (var imgBg = new SolidBrush(Color.FromArgb(20, 37, 58)))
                g.FillRectangle(imgBg, imageRect);
            if (e.Item.ImageKey.Length > 0 && _thumbnailByFileId.TryGetValue(e.Item.ImageKey, out var image))
            {
                // Cover-fill the thumbnail area (crop to fill), edge to edge as in the design.
                float scale = Math.Max((float)imageRect.Width / image.Width, (float)imageRect.Height / image.Height);
                int dw = (int)(image.Width * scale), dh = (int)(image.Height * scale);
                g.DrawImage(image, imageRect.Left + (imageRect.Width - dw) / 2, imageRect.Top + (imageRect.Height - dh) / 2, dw, dh);
            }
            g.Restore(state);

            using var border = new Pen(borderColor, borderWidth);
            g.DrawPath(border, path);
        }

        var thumb = layout.Thumb;
        // Video: a translucent dark play disc in the middle of the thumbnail.
        if (media is { Kind: MediaKind.Video })
        {
            int d = Math.Min(30, thumb.Height - 8);
            var playCircle = new Rectangle(thumb.Left + (thumb.Width - d) / 2, thumb.Top + (thumb.Height - d) / 2, d, d);
            using var playBrush = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
            g.FillEllipse(playBrush, playCircle);
            using var triBrush = new SolidBrush(Color.White);
            float cx = playCircle.Left + d / 2f + 1, cy = playCircle.Top + d / 2f, s = d * 0.2f;
            g.FillPolygon(triBrush, new[] { new PointF(cx - s, cy - s * 1.2f), new PointF(cx - s, cy + s * 1.2f), new PointF(cx + s * 1.3f, cy) });
        }

        using var badgeFont = new Font(ModernUi.UiFont, 8F, FontStyle.Bold);
        // Type chip (JPG / MP4 / PPT / PDF / DOC …) bottom-left; 大屏 / 预览 tag top-left.
        if (media != null)
        {
            var (badgeText, badgeColor) = FileTypeBadge.For(media);
            DrawChip(g, badgeText, badgeFont, new Point(thumb.Left + 6, thumb.Bottom - 6 - 17), badgeColor, Color.White);
        }
        if (onProgram) DrawChip(g, "大屏", badgeFont, new Point(thumb.Left + 6, thumb.Top + 6), ModernUi.Danger, Color.White);
        else if (inPreview) DrawChip(g, "预览", badgeFont, new Point(thumb.Left + 6, thumb.Top + 6), ModernUi.Accent, ModernUi.AccentForeground);

        // Name (with ⋯ on the same row) and the meta line (kind · size).
        string name = media != null ? Path.GetFileName(media.SourcePath) : e.Item.Text;
        using (var nameFont = new Font(ModernUi.UiFont, 9.5F))
            TextRenderer.DrawText(g, name, nameFont, layout.Name, ModernUi.Text,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        if (e.Item.SubItems.Count > 1)
            using (var metaFont = new Font(ModernUi.UiFont, 8F))
                TextRenderer.DrawText(g, e.Item.SubItems[1].Text, metaFont, layout.Meta, ModernUi.Muted,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);

        using (var dots = new SolidBrush(ModernUi.Muted))
        {
            int dy = layout.More.Top + layout.More.Height / 2;
            for (int i = -1; i <= 1; i++)
                g.FillEllipse(dots, layout.More.Left + layout.More.Width / 2 + i * 5 - 1.5f, dy - 1.5f, 3.2f, 3.2f);
        }
    }

    private static void DrawChip(Graphics g, string text, Font font, Point topLeft, Color fill, Color fore)
    {
        var size = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding);
        var chip = new Rectangle(topLeft.X, topLeft.Y, size.Width + 10, 17);
        using (var path = RoundedCard(chip, 4))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        TextRenderer.DrawText(g, text, font, chip, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    /// <summary>The card's muted second line: 「图片 · 4.2 MB」.</summary>
    private static string CardMeta(MediaFile file)
    {
        try
        {
            var info = new FileInfo(file.SourcePath);
            return info.Exists ? $"{KindLabel(file.Kind)} · {FormatBytes(info.Length)}" : $"{KindLabel(file.Kind)} · 文件不可用";
        }
        catch (Exception) { return KindLabel(file.Kind); }
    }

    private static string CardToolTip(MediaFile file)
    {
        string name = Path.GetFileName(file.SourcePath);
        try
        {
            var info = new FileInfo(file.SourcePath);
            return info.Exists ? $"{name}\n{info.LastWriteTime:yyyy/MM/dd}    {FormatBytes(info.Length)}" : $"{name}\n文件不可用";
        }
        catch (Exception) { return $"{name}\n{KindLabel(file.Kind)}"; }
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount),
        (int)(from.B + (to.B - from.B) * amount));

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

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024L => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B",
    };

    private static string KindLabel(MediaKind kind) => kind switch
    {
        MediaKind.Image => "图片",
        MediaKind.Video => "视频",
        MediaKind.Document => "文档",
        MediaKind.Audio => "音频",
        _ => "文件",
    };

    private void PlayOrPreviewSelected()
    {
        if (_listView.SelectedItems.Count > 0 && _listView.SelectedItems[0].Tag is MediaFile file)
            FilePlayRequested?.Invoke(file);
    }

    /// <summary>A clean 240×150 gradient tile for non-image kinds (and the missing-file case). The
    /// colored type badge is drawn separately over the tile by <see cref="DrawMediaCard"/>; only the
    /// missing-file case passes a caption so the operator can see why a tile is blank.</summary>
    private static Bitmap BuildKindTile(string? caption, Color top, Color bottom)
    {
        var bitmap = new Bitmap(240, 150);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 240, 150), top, bottom, 60F))
            graphics.FillRectangle(bg, 0, 0, 240, 150);
        if (caption != null)
        {
            using var captionBrush = new SolidBrush(Color.FromArgb(210, 225, 240));
            using var captionFont = new Font(ModernUi.UiFont, 11F);
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(caption, captionFont, captionBrush, new RectangleF(0, 0, 240, 150), centered);
        }
        return bitmap;
    }

    /// <summary>「搜索文件名」: a borderless text box inside a rounded field with a magnifier glyph.</summary>
    private sealed class SearchBox : Control
    {
        private readonly TextBox _text;

        public SearchBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            _text = new TextBox
            {
                BorderStyle = BorderStyle.None, BackColor = ModernUi.Surface, ForeColor = ModernUi.Text,
                Font = new Font(ModernUi.UiFont, 10F), PlaceholderText = "搜索文件名",
            };
            _text.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
            _text.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                _text.Clear();
                e.SuppressKeyPress = true;
            };
            Controls.Add(_text);
            Cursor = Cursors.IBeam;
            AccessibleName = "搜索文件名";
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string Text
        {
            get => _text?.Text ?? "";
            set { if (_text != null) _text.Text = value; }
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _text.Focus(); }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_text == null) return;
            int left = 30;
            _text.Bounds = new Rectangle(left, (Height - _text.PreferredHeight) / 2, Math.Max(10, Width - left - 10), _text.PreferredHeight);
        }

        protected override void OnPaintBackground(PaintEventArgs e) =>
            ButtonRenderer.DrawParentBackground(e.Graphics, ClientRectangle, this);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (_text.BackColor != ModernUi.Surface) _text.BackColor = ModernUi.Surface;
            if (_text.ForeColor != ModernUi.Text) _text.ForeColor = ModernUi.Text;
            using (var path = RoundedCard(rect, 8))
            using (var fill = new SolidBrush(ModernUi.Surface))
            using (var border = new Pen(_text.Focused ? ModernUi.Accent : ModernUi.Border))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            using var pen = new Pen(ModernUi.Muted, 1.6F);
            int cy = Height / 2;
            g.DrawEllipse(pen, 10, cy - 7, 10, 10);
            g.DrawLine(pen, 18.5F, cy + 1.5F, 22F, cy + 5F);
        }
    }
}
