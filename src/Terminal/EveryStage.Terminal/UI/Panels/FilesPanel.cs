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
/// PLANNING.md §8.2's audio "横向播放条" (播放/进度/音量/循环/独立投屏) exists as a single large
/// player bar (<see cref="_audioBar"/>, an <see cref="AudioPlayerBar"/>) docked at the bottom of
/// this panel, matching the design — it binds to whichever audio file is currently playing (or the
/// first audio file otherwise). An audio file also still appears as a generic tile in "全部"/"音频"
/// like before; this bar is additive, not a replacement. See <see cref="RefreshAudioBar"/> for how
/// it is rebound and <see cref="OnCastFromAudioBar"/> for what "独立投屏" does (exactly what
/// double-clicking a tile already does — <see cref="FilePlayRequested"/>, no new playback
/// behavior).
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
    private bool _outputActive;

    /// <summary>PLANNING.md §8.2's audio "横向播放条" — now a single large player bar
    /// (<see cref="AudioPlayerBar"/>) matching the design, instead of the old per-file
    /// <c>FlowLayoutPanel</c> of rows. The bar binds to whichever audio file is currently playing
    /// (or the first one otherwise); all playback data flow (pause/volume/loop/cast) is unchanged —
    /// see <see cref="AudioPlayerBar"/>'s own doc comment.</summary>
    private readonly AudioPlayerBar _audioBar;
    private readonly Label _audioHeading;
    private readonly System.Windows.Forms.Timer _audioBarRefreshTimer;
    private readonly Label _emptyStateLabel;

    /// <summary>Raised when the user double-clicks a file to play it standalone (no activity
    /// context — see <c>PlaybackEngine.RequestPlay(MediaFile)</c>'s own doc comment on what that
    /// means for completion actions). Also raised by <see cref="_audioBar"/>'s "投屏" button
    /// (<see cref="OnCastFromAudioBar"/>) — confirmed with the user that button should mean exactly
    /// this and nothing more.</summary>
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
        BackColor = ModernUi.Background;
        Padding = new Padding(0);

        var heading = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent, Padding = new Padding(4, 0, 0, 0) };
        var pageTitle = new Label
        {
            Text = "文件", Dock = DockStyle.Top, Height = 22, ForeColor = ModernUi.Text,
            Font = new Font("Segoe UI Semibold", 13F), TextAlign = ContentAlignment.MiddleLeft,
        };
        var pageDescription = new Label
        {
            Text = "管理本地媒体文件，用于活动播放", Dock = DockStyle.Fill,
            ForeColor = ModernUi.Muted, Font = new Font("Segoe UI", 8.5F), TextAlign = ContentAlignment.MiddleLeft,
        };
        heading.Controls.Add(pageDescription);
        heading.Controls.Add(pageTitle);

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            MinimumSize = new Size(0, 48),
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(2, 4, 2, 4),
            BackColor = ModernUi.Background,
        };
        // 筛选胶囊按内容定宽，其余宽度全给右侧操作区——原先 54%/46% 平分时左栏有空余、右栏却放不下，
        //「播放触发 · 双击播放」在默认窗口里被截成半句「· 双击」。
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        var filterBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = ModernUi.Background,
        };
        filterBar.Controls.Add(MakeFilterButton("全部", null));
        filterBar.Controls.Add(MakeFilterButton("图片", MediaKind.Image));
        filterBar.Controls.Add(MakeFilterButton("视频", MediaKind.Video));
        filterBar.Controls.Add(MakeFilterButton("文档", MediaKind.Document));
        toolbar.Controls.Add(filterBar, 0, 0);

        var toolbarActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.RightToLeft,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = ModernUi.Background,
        };
        var moreButton = new PillButton { Text = "⋮", Width = 42, Height = 38, Margin = new Padding(2, 1, 2, 0) };
        _toolbarMenu = BuildToolbarMenu();
        moreButton.Click += (_, _) => _toolbarMenu.Show(moreButton, new Point(0, moreButton.Height));
        toolbarActions.Controls.Add(moreButton);

        var importButton = new RoundedActionButton { Text = "⇧ 导入文件", Width = 94, Height = 34, Margin = new Padding(3, 2, 3, 0) };
        ModernUi.StyleButton(importButton, primary: true);
        importButton.Click += (_, _) => ImportViaDialog();
        toolbarActions.Controls.Add(importButton);

        var addToActivityButton = new RoundedActionButton { Text = "＋ 加入活动", Width = 94, Height = 34, Margin = new Padding(3, 2, 3, 0), Enabled = false };
        ModernUi.StyleButton(addToActivityButton);
        addToActivityButton.Click += OnAddToActivityClick;
        toolbarActions.Controls.Add(addToActivityButton);

        var playbackHint = new Label
        {
            Text = "播放触发 · 双击播放", AutoSize = true,
            Margin = new Padding(2, 11, 8, 0), ForeColor = ModernUi.Muted,
        };
        toolbarActions.Controls.Add(playbackHint);
        // Too narrow for the whole hint → hide it outright rather than let the right-to-left flow clip
        // it into a meaningless fragment.
        toolbarActions.Resize += (_, _) =>
        {
            int needed = toolbarActions.Controls.Cast<Control>().Where(c => c != playbackHint)
                .Sum(c => c.Width + c.Margin.Horizontal) + playbackHint.PreferredWidth + playbackHint.Margin.Horizontal;
            playbackHint.Visible = needed <= toolbarActions.ClientSize.Width;
        };

        _outputStatusChip = new PillButton
        {
            Text = "○  待机", AutoSize = true, Height = 38,
            Margin = new Padding(4, 1, 8, 0), Enabled = false,
        };
        toolbar.Controls.Add(toolbarActions, 1, 0);

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
            BackColor = ModernUi.Surface,
            ForeColor = ModernUi.Text,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 10F),
            Padding = new Padding(14),
        };
        _listView.DrawItem += DrawMediaCard;
        _listView.SelectedIndexChanged += (_, _) =>
        {
            bool hasSelection = _listView.SelectedItems.Count > 0;
            addToActivityButton.Enabled = hasSelection;
            _removeMenuItem.Enabled = hasSelection;
            _addToActivityMenuItem.Enabled = hasSelection;
            _previewMenuItem.Enabled = hasSelection;
            _openLocationMenuItem.Enabled = hasSelection;
        };
        _listView.DoubleClick += (_, _) =>
        {
            PlayOrPreviewSelected();
        };
        _fileContextMenu = BuildFileContextMenu();
        _listView.MouseUp += OnFileListMouseUp;

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _listView.DragEnter += OnDragEnter;
        _listView.DragDrop += OnDragDrop;
        _listView.AllowDrop = true;

        // Audio remains a distinct horizontal playback zone, separate from the visual-media cards.
        _audioBar = new AudioPlayerBar(library, fileOpLog, playback) { Dock = DockStyle.Bottom };
        _audioBar.CastRequested += OnCastFromAudioBar;
        _audioBar.PlayRequested += file =>
        {
            FilePlayRequested?.Invoke(file); // internal Preview playback — never an external player
            _audioBar.RefreshLiveState();
        };
        var audioBarSpacer = new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Color.Transparent };
        _audioHeading = new Label
        {
            Text = "音频播放区　·　管理音频文件，用于背景音播放，可加入活动",
            Dock = DockStyle.Bottom, Height = 24, ForeColor = ModernUi.Muted,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(5, 0, 0, 0),
        };

        // Control.Controls.Add order determines Dock z-order for same-DockStyle siblings: the LAST
        // one added claims its edge first. _listView (Fill) is added first so it takes whatever's
        // left; the audio bar and its spacer claim the bottom edge; toolbar claims the top.
        Controls.Add(_listView);
        Controls.Add(_audioHeading);
        Controls.Add(_audioBar);
        Controls.Add(audioBarSpacer);
        Controls.Add(toolbar);
        Controls.Add(heading);

        _emptyStateLabel = new Label
        {
            Text = "文件库还是空的\n\n点击“＋ 导入文件”或将文件拖到这里",
            ForeColor = ModernUi.Muted,
            BackColor = ModernUi.Surface,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 12F),
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
        _audioBarRefreshTimer.Tick += (_, _) => _audioBar.RefreshLiveState();
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
            Renderer = new ToolStripProfessionalRenderer(new FileMenuColorTable()),
        };
        // Everything above the last separator stays inside EveryStage (Preview / Program). The only
        // hand-off to Windows is the explicit last item — never a side effect of playing.
        menu.Items.Add("在本地预览", null, (_, _) => { if (_contextFile != null) FilePlayRequested?.Invoke(_contextFile); });
        menu.Items.Add("投到屏幕", null, (_, _) => { if (_contextFile != null) TakeRequested?.Invoke(_contextFile); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("循环播放", null, (_, _) => SetCompletionAndPlay(CompletionAction.Loop));
        menu.Items.Add("顺序播放", null, (_, _) => StartSequentialPlayback());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("停止预览", null, (_, _) => PlaybackStopRequested?.Invoke());
        var stopOutputItem = new ToolStripMenuItem("停止输出", null, (_, _) => StopProgramRequested?.Invoke());
        menu.Items.Add(stopOutputItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("加入活动...", null, OnAddToActivityClick);
        menu.Items.Add("从文件库移除", null, (_, _) => OnRemoveClick(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("使用系统默认程序打开", null, (_, _) =>
        {
            if (_contextFile != null) ExternalOpener.OpenWithDefaultApp(FindForm(), _contextFile.SourcePath);
        });
        menu.Opening += (_, _) =>
        {
            stopOutputItem.Enabled = IsProgramLive?.Invoke() == true;
            menu.BackColor = ModernUi.Surface;
            menu.ForeColor = ModernUi.Text;
            foreach (ToolStripItem item in menu.Items)
                item.ForeColor = ModernUi.Text;
        };
        return menu;
    }

    private ContextMenuStrip BuildToolbarMenu()
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            BackColor = ModernUi.Surface,
            ForeColor = ModernUi.Text,
            Renderer = new ToolStripProfessionalRenderer(new FileMenuColorTable()),
        };
        menu.Items.Add("导入文件...", null, (_, _) => ImportViaDialog());
        menu.Items.Add(new ToolStripSeparator());
        _previewMenuItem = new ToolStripMenuItem("播放 / 预览", null, (_, _) => PlayOrPreviewSelected()) { Enabled = false };
        _addToActivityMenuItem = new ToolStripMenuItem("加入活动...", null, OnAddToActivityClick) { Enabled = false };
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
        if (hit?.Tag is not MediaFile file) return;
        bool moreClicked = e.Button == MouseButtons.Left && GetMoreBounds(hit).Contains(e.Location);
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

    private static Rectangle GetMoreBounds(ListViewItem item)
    {
        var card = item.Bounds;
        card.Inflate(-8, -8);
        const int pad = 7;
        int previewWidth = card.Width - pad * 2;
        int previewHeight = Math.Max(1, (int)Math.Round(previewWidth * 0.42));
        int nameBottom = card.Top + pad + previewHeight + 3 + 18;
        return new Rectangle(card.Right - pad - 22, nameBottom + 1, 22, 14);
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
                MessageBox.Show(this, $"无法保存播放方式：{ex.Message}", "保存失败",
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
        public override Color MenuBorder => ModernUi.Border;
        public override Color MenuItemBorder => ModernUi.Accent;
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
                using var graphics = Graphics.FromHdc(m.WParam);
                using var background = new SolidBrush(ModernUi.Background);
                graphics.FillRectangle(background, ClientRectangle);
                m.Result = new IntPtr(1);
                return;
            }
            base.WndProc(ref m);
        }
    }

    private PillButton MakeFilterButton(string label, MediaKind? filter)
    {
        var button = new PillButton { Text = label, AutoSize = true, Height = 38, Margin = new Padding(4, 0, 4, 0), Selected = _activeFilter == filter };
        button.Click += (_, _) =>
        {
            _activeFilter = filter;
            if (button.Parent != null) foreach (Control control in button.Parent.Controls)
            {
                if (control is PillButton filterButton && filterButton.Tag != null)
                {
                    filterButton.Selected = ReferenceEquals(filterButton, button);
                    filterButton.Invalidate();
                }
            }
            Refresh_();
        };
        button.Tag = filter.HasValue ? filter.Value : "all";
        return button;
    }

    private void ImportViaDialog()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "导入文件到文件库" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ImportPaths(dialog.FileNames);
    }

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
        var files = _listView.SelectedItems.Cast<ListViewItem>()
            .Select(item => item.Tag as MediaFile)
            .Where(file => file != null)
            .Cast<MediaFile>()
            .ToList();
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
                MessageBox.Show(this, $"移除未完成：{ex.Message}", "文件库保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            MessageBox.Show(this, $"未加入活动：{ex.Message}", "方案保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                MessageBox.Show(this, $"导入未完成：{ex.Message}\n此前成功导入的文件已保留。", "文件库保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        foreach (var file in _library.Files.Where(f => _activeFilter == null
                     ? f.Kind != MediaKind.Audio
                     : f.Kind == _activeFilter))
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
            var item = new ListViewItem(Path.GetFileName(file.SourcePath), key) { Tag = file };
            _listView.Items.Add(item);
        }

        RefreshAudioBar();
        HighlightCurrentFile();
        _emptyStateLabel.Text = _library.Files.Any(f => f.Kind == MediaKind.Audio)
            ? "暂无图片、视频或文档\n\n音频文件在下方独立播放区管理"
            : "文件库还是空的\n\n点击“＋ 导入文件”或将文件拖到这里";
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
                        thumbnail = await Task.Run(() => BuildThumbnail(file), token);
                }
                finally { _thumbnailWorker.Release(); }
                using (thumbnail)
                {
                    if (token.IsCancellationRequested || IsDisposed || Disposing) return;
                    if (cacheKey != null && !_thumbnailCache.ContainsKey(cacheKey))
                    {
                        while (_thumbnailCache.Count >= ThumbnailCacheCapacity)
                        {
                            string oldest = _thumbnailCacheOrder.Dequeue();
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
    /// from <see cref="Refresh_()"/> on the same cadence that rebuilds <see cref="_listView"/>. The
    /// bar hides itself when there are no audio files (see <see cref="AudioPlayerBar.SetAudioFiles"/>).</summary>
    private void RefreshAudioBar()
    {
        var audioFiles = _library.Files.Where(f => f.Kind == MediaKind.Audio).ToList();
        _audioBar.SetAudioFiles(audioFiles);
        _audioHeading.Visible = audioFiles.Count > 0;
    }

    /// <summary>"独立投屏" — unchanged behavior: raise the same <see cref="FilePlayRequested"/> event
    /// <c>MainWindow</c> already wires to <c>PlaybackEngine.RequestPlay(MediaFile)</c>, then refresh
    /// the bar's live state immediately (the subscriber calls RequestPlay synchronously).</summary>
    private void OnCastFromAudioBar(MediaFile file)
    {
        TakeRequested?.Invoke(file);
        _audioBar.RefreshLiveState();
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

    private void DrawMediaCard(object? sender, DrawListViewItemEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var card = e.Bounds;
        card.Inflate(-8, -8);
        bool selected = e.Item.Selected;
        var media = e.Item.Tag as MediaFile;
        bool playing = media != null && _playback is { State: not PlaybackChannelState.Idle, CurrentFile: { } current }
            && (ReferenceEquals(media, current)
                || string.Equals(media.SourcePath, current.SourcePath, StringComparison.OrdinalIgnoreCase));

        // Card background — design uses a flat raised surface with a blue ring when selected.
        using (var path = RoundedCard(card, 10))
        using (var fill = new SolidBrush(selected || playing ? Color.FromArgb(24, 46, 78) : ModernUi.SurfaceRaised))
        using (var border = new Pen(playing ? ModernUi.Success : selected ? ModernUi.Accent : Color.FromArgb(38, 60, 90),
                   selected || playing ? 2F : 1F))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        int pad = 7;
        int previewWidth = Math.Max(1, card.Width - pad * 2);
        int previewHeight = Math.Max(1, (int)Math.Round(previewWidth * 0.42));
        var imageRect = new Rectangle(card.Left + pad, card.Top + pad, previewWidth, previewHeight);
        using (var imagePath = RoundedCard(imageRect, 8))
        {
            var state = g.Save();
            g.SetClip(imagePath);
            using (var imgBg = new SolidBrush(Color.FromArgb(20, 37, 58)))
                g.FillRectangle(imgBg, imageRect);
            if (e.Item.ImageKey.Length > 0 && _thumbnailByFileId.TryGetValue(e.Item.ImageKey, out var image))
            {
                // Cover-fill the image area (crop to fill), matching the design's edge-to-edge thumbnails.
                float scale = Math.Max((float)imageRect.Width / image.Width, (float)imageRect.Height / image.Height);
                int dw = (int)(image.Width * scale), dh = (int)(image.Height * scale);
                g.DrawImage(image, imageRect.Left + (imageRect.Width - dw) / 2, imageRect.Top + (imageRect.Height - dh) / 2, dw, dh);
            }
            g.Restore(state);
        }

        // Video play badge, centered.
        if (media is { Kind: MediaKind.Video })
        {
            var playCircle = new Rectangle(imageRect.Left + imageRect.Width / 2 - 13, imageRect.Top + imageRect.Height / 2 - 13, 26, 26);
            using var playBrush = new SolidBrush(Color.FromArgb(150, 8, 20, 38));
            g.FillEllipse(playBrush, playCircle);
            using var triBrush = new SolidBrush(Color.White);
            var tri = new[]
            {
                new PointF(playCircle.Left + 10, playCircle.Top + 7),
                new PointF(playCircle.Left + 10, playCircle.Top + 19),
                new PointF(playCircle.Left + 19, playCircle.Top + 13),
            };
            g.FillPolygon(triBrush, tri);
        }

        // Colored type badge in the image's bottom-left (P / W / X / PDF / image / ♫).
        if (media != null)
            DrawTypeBadge(g, new Rectangle(imageRect.Left + 7, imageRect.Bottom - 27, 21, 21), media);

        // Selection checkbox — always visible top-right, filled when selected (design shows the ☑).
        var box = new Rectangle(imageRect.Right - 24, imageRect.Top + 5, 16, 16);
        using (var boxPath = RoundedCard(box, 5))
        {
            using var boxFill = new SolidBrush(selected ? ModernUi.Accent : Color.FromArgb(150, 10, 24, 42));
            using var boxPen = new Pen(selected ? ModernUi.Accent : Color.FromArgb(150, 180, 205, 235), 1.6F);
            g.FillPath(boxFill, boxPath);
            g.DrawPath(boxPen, boxPath);
            if (selected)
            {
                using var check = new Pen(Color.White, 2F) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(check, new[]
                {
                    new PointF(box.Left + 5, box.Top + 10),
                    new PointF(box.Left + 9, box.Top + 14),
                    new PointF(box.Left + 15, box.Top + 6),
                });
            }
        }

        // File name.
        string name = media != null ? Path.GetFileName(media.SourcePath) : e.Item.Text;
        var nameRect = new Rectangle(card.Left + pad, imageRect.Bottom + 3, card.Width - pad * 2, 18);
        using (var nameFont = new Font("Segoe UI Semibold", 10F))
            TextRenderer.DrawText(g, name, nameFont, nameRect,
                playing ? ModernUi.Success : ModernUi.Text,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

        // Meta line (date · size) with a trailing ⋯ affordance on the right.
        string meta = "";
        if (media != null)
        {
            try
            {
                var info = new FileInfo(media.SourcePath);
                meta = $"{info.LastWriteTime:yyyy/MM/dd}    {FormatBytes(info.Length)}";
            }
            catch (Exception) { meta = KindLabel(media.Kind); }
        }
        var metaRect = new Rectangle(card.Left + pad, nameRect.Bottom + 1, card.Width - pad * 2 - 24, 14);
        using (var metaFont = new Font("Segoe UI", 8F))
            TextRenderer.DrawText(g, meta, metaFont, metaRect,
                ModernUi.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

        var moreRect = new Rectangle(card.Right - pad - 22, nameRect.Bottom + 1, 22, 14);
        using (var moreFont = new Font("Segoe UI", 11F, FontStyle.Bold))
            TextRenderer.DrawText(g, "⋯", moreFont, moreRect, ModernUi.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Colored rounded type badge (P/W/X/PDF/图片/♫) drawn in a media card's thumbnail
    /// corner, matching the design's app-color chips. Colors mirror <see cref="BuildKindTile"/>.</summary>
    private static void DrawTypeBadge(Graphics g, Rectangle r, MediaFile file)
    {
        (string text, Color color) = file.Kind switch
        {
            MediaKind.Video => ("▶", Color.FromArgb(124, 58, 237)),
            MediaKind.Audio => ("♫", Color.FromArgb(20, 160, 130)),
            MediaKind.Image => ("", Color.FromArgb(37, 99, 235)),
            MediaKind.Document => (DocumentBadge(file.SourcePath), DocumentColor(file.SourcePath)),
            _ => ("•", ModernUi.Accent),
        };
        using var path = RoundedCard(r, 8);
        using var fill = new SolidBrush(color);
        g.FillPath(fill, path);
        if (file.Kind == MediaKind.Image)
        {
            // A small "mountain + sun" image glyph rather than a letter.
            using var pen = new Pen(Color.White, 1.6F) { LineJoin = LineJoin.Round };
            g.DrawEllipse(pen, r.Left + 6, r.Top + 6, 4, 4);
            using var tri = new SolidBrush(Color.White);
            g.FillPolygon(tri, new[]
            {
                new PointF(r.Left + 5, r.Bottom - 6),
                new PointF(r.Left + 11, r.Top + 12),
                new PointF(r.Left + 18, r.Bottom - 6),
            });
            return;
        }
        using var badgeFont = new Font("Segoe UI Semibold", text.Length > 1 ? 9F : 13F, FontStyle.Bold);
        TextRenderer.DrawText(g, text, badgeFont, r, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static Color DocumentColor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => Color.FromArgb(219, 68, 55),
        ".ppt" or ".pptx" => Color.FromArgb(211, 71, 39),
        ".xls" or ".xlsx" => Color.FromArgb(33, 160, 96),
        ".doc" or ".docx" => Color.FromArgb(41, 105, 214),
        _ => Color.FromArgb(44, 113, 218),
    };

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

    private static string DocumentBadge(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "PDF",
        ".ppt" or ".pptx" => "P",
        ".xls" or ".xlsx" => "X",
        ".doc" or ".docx" => "W",
        _ => "DOC",
    };

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
            using var captionFont = new Font("Segoe UI", 11F);
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(caption, captionFont, captionBrush, new RectangleF(0, 0, 240, 150), centered);
        }
        return bitmap;
    }
}
