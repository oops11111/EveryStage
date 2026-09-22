using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI;

/// <summary>
/// The floating preview window (PLANNING.md §8.3): "仅在扩展屏有输出时出现，无输出时不存在" — shown
/// while <see cref="OutputStateMachine"/> is Active, hidden the rest of the time. Content: LIVE
/// badge, thumbnail preview, filename/kind, four buttons (上一项/暂停/下一项/切断), a volume row
/// (－/音量文本/＋), a seek row (◀10秒/位置文本/10秒▶) for standalone audio, and now a "保存" button
/// for an open Office document — see <see cref="_volumeDownButton"/>'s, <see cref="_seekBackButton"/>'s,
/// and <see cref="_saveButton"/>'s own doc comments for why each lives here rather than waiting for
/// §8.2's full "横向播放条" to exist.
///
/// Kept as one persistent <see cref="Form"/> instance rather than recreated per show — that's what
/// gives "支持拖动记忆位置" (dragged position remembered) for free: <see cref="Control.Location"/>
/// naturally survives a Hide()/Show() cycle without any bookkeeping of our own.
///
/// Refreshes its thumbnail/labels on a short polling timer rather than purely off
/// <see cref="PlaybackEngine.FileStarted"/>: that event fires synchronously the moment a file is
/// accepted, but for image/PDF the actual decoded frame lands slightly later (LoadAsync is
/// fire-and-forget from PlaybackEngine's point of view) — polling sidesteps that race entirely
/// instead of chasing a second "frame ready" event. The same polling now also drives
/// <see cref="_pageLabel"/> (<see cref="PlaybackEngine.DocumentPageInfo"/>), for the same reason:
/// a page turn changes the renderer's current frame without raising any event of its own.
///
/// 上一项/下一项 silently mean "turn a page" instead of "move to a different playlist item" while
/// viewing a multi-page Document (see <see cref="PlaybackEngine.NextManual"/>'s own doc comment) —
/// <see cref="_pageLabel"/> exists so that behavior change is visible rather than surprising: it
/// only shows up (as "第X页/共Y页") when there's actually more than one page to turn between.
/// </summary>
public sealed class FloatingPreviewWindow : Form
{
    private readonly PlaybackEngine _playback;
    private readonly OutputStateMachine _stateMachine;
    private readonly SettingsStore _settingsStore;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly Label _liveBadge;
    private readonly PictureBox _thumbnail;
    private readonly Label _fileLabel;
    private readonly Label _pageLabel;
    private readonly Button _previousButton;
    private readonly Button _pauseButton;
    private readonly Button _nextButton;
    private readonly Button _disconnectButton;
    private readonly Button _pinButton;

    /// <summary>Adjusts <see cref="PlaybackEngine.AudioVolume"/> in fixed 10% steps — a full "横向
    /// 播放条" (PLANNING.md §8.2, with a proper slider/percentage readout) doesn't exist anywhere in
    /// this repo yet and building one just for this window would be guessing at a UI this project's
    /// own README already documents as a bigger, deferred piece of work (see that README's "已知
    /// 风险"/"尚未开始" on the audio play bar). A pair of step buttons reuses the exact same
    /// Button-based interaction language every other control in this window already uses, rather
    /// than introducing a new control type (e.g. <c>TrackBar</c>) this codebase has never used
    /// anywhere — its layout/rendering behavior can't be verified without a real Windows build, and
    /// reusing an already-proven control type here avoids adding a second, independent unverified
    /// risk on top of that.</summary>
    private readonly Button _volumeDownButton;
    private readonly Label _volumeLabel;
    private readonly Button _volumeUpButton;

    /// <summary>Fixed ±10-second step, PLANNING.md §8.2's audio play-bar "进度" — same "reuse plain
    /// Buttons, not an unverified control type" reasoning as <see cref="_volumeDownButton"/>'s own
    /// doc comment (a draggable slider/<c>TrackBar</c> would be exactly the kind of never-used-
    /// anywhere-in-this-codebase control that class explicitly avoided introducing for volume, and
    /// the same avoidance applies here). Backed by <see cref="PlaybackEngine.SeekAudioRelative"/>,
    /// which is itself best-effort — see <c>ContentEngine.AudioContentController.TrySeekTo</c>'s own
    /// doc comment for why a seek can silently do nothing on some files.</summary>
    private readonly Button _seekBackButton;
    private readonly Label _positionLabel;
    private readonly Button _seekForwardButton;

    /// <summary>PLANNING.md §14.1's "编辑/保存" — only enabled while an Office document
    /// (<see cref="WpsDocumentController.IsOfficeDocument"/>) is the current file, i.e. while its own
    /// real WPS window is what's actually showing on the extended display. Calls
    /// <see cref="PlaybackEngine.TrySaveCurrentOfficeDocument"/> — a convenience, not the only way to
    /// save: the operator can always use WPS's own Ctrl+S directly in that same window (see
    /// <see cref="WpsDocumentController"/>'s own doc comment on why 翻页/编辑 are left entirely to
    /// WPS's real UI; 保存 is the one piece of PLANNING.md's "打开/翻页/编辑/保存" still worth wiring
    /// here). No dedicated feedback UI beyond a brief flash of <see cref="_pageLabel"/>'s text — this
    /// window's existing style is lightweight labels/buttons, not modal confirmation dialogs (see
    /// <see cref="_volumeDownButton"/>'s own doc comment on preferring the simplest control that
    /// already exists here over a new one).</summary>
    private readonly Button _saveButton;

    public FloatingPreviewWindow(PlaybackEngine playback, OutputStateMachine stateMachine, SettingsStore settingsStore)
    {
        _playback = playback;
        _stateMachine = stateMachine;
        _settingsStore = settingsStore;
        _settingsStore.SettingsChanged += OnSettingsChanged;

        Text = "EveryStage";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = ModernUi.Background;
        ForeColor = ModernUi.Text;
        Font = new Font("Segoe UI", 9F);
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40); // only used the very first time this instance is shown.

        _liveBadge = new Label
        {
            Text = "● LIVE",
            ForeColor = Color.Red,
            AutoSize = true,
            Location = new Point(8, 6),
        };

        _thumbnail = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
            Bounds = new Rectangle(8, 28, 204, 114),
        };

        _fileLabel = new Label
        {
            AutoEllipsis = true,
            Bounds = new Rectangle(8, 146, 204, 32),
        };

        // Empty text (AutoSize-less Label collapses to nothing visible) whenever
        // PlaybackEngine.DocumentPageInfo is null — see this class's doc comment.
        _pageLabel = new Label
        {
            ForeColor = Color.DimGray,
            Bounds = new Rectangle(8, 178, 204, 16),
        };

        // Shifted down 18px (was 182) from the original layout to make room for _pageLabel above —
        // ClientSize grown by the same 18px (was 214) to match, same "grow, don't reflow everything"
        // approach this repo's other absolutely-positioned WinForms panels already use.
        _previousButton = new Button { Text = "◀ 上一项", Bounds = new Rectangle(8, 200, 60, 24) };
        _pauseButton = new Button { Text = "暂停", Bounds = new Rectangle(72, 200, 44, 24) };
        _nextButton = new Button { Text = "下一项 ▶", Bounds = new Rectangle(120, 200, 60, 24) };
        _disconnectButton = new Button { Text = "断", ForeColor = Color.DarkRed, Bounds = new Rectangle(184, 200, 28, 24) };
        _pinButton = new Button { Text = "📌", Bounds = new Rectangle(184, 6, 24, 20) };

        // New row below the existing four buttons — grown ClientSize by the same 24px (row) + 4px
        // (gap) = 28px this file's own history already uses for adding a row without reflowing
        // anything above it (see the comment on _previousButton etc. above for the last time this
        // happened, for _pageLabel).
        _volumeDownButton = new Button { Text = "－", Bounds = new Rectangle(8, 228, 32, 24) };
        _volumeLabel = new Label
        {
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(44, 228, 128, 24),
        };
        _volumeUpButton = new Button { Text = "＋", Bounds = new Rectangle(180, 228, 32, 24) };

        // Third row added below the volume row — same "grow ClientSize by row height (24) + gap
        // (4) = 28px, reflow nothing above" approach this file's own history already used twice
        // (see _pageLabel's and _volumeDownButton's own comments above).
        _seekBackButton = new Button { Text = "◀ 10秒", Bounds = new Rectangle(8, 256, 56, 24) };
        _positionLabel = new Label
        {
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(68, 256, 84, 24),
        };
        _seekForwardButton = new Button { Text = "10秒 ▶", Bounds = new Rectangle(156, 256, 56, 24) };

        // Fourth row added below the seek row — same "grow ClientSize by row height (24) + gap (4) =
        // 28px, reflow nothing above" approach this file's own history already used three times (see
        // _pageLabel's/_volumeDownButton's/_seekBackButton's own comments above).
        _saveButton = new Button { Text = "保存", Bounds = new Rectangle(8, 284, 204, 24) };

        ClientSize = new Size(220, 316);

        Controls.AddRange(new Control[]
        {
            _liveBadge, _thumbnail, _fileLabel, _pageLabel,
            _previousButton, _pauseButton, _nextButton, _disconnectButton, _pinButton,
            _volumeDownButton, _volumeLabel, _volumeUpButton,
            _seekBackButton, _positionLabel, _seekForwardButton,
            _saveButton,
        });
        WindowsAppearance.UseDarkTitleBar(this);
        foreach (var button in Controls.OfType<Button>())
        {
            ModernUi.StyleButton(button, primary: ReferenceEquals(button, _saveButton),
                danger: ReferenceEquals(button, _disconnectButton));
            button.Padding = Padding.Empty;
        }

        _previousButton.Click += (_, _) => _playback.PreviousManual();
        _nextButton.Click += (_, _) => _playback.NextManual();
        _disconnectButton.Click += (_, _) => _stateMachine.Disconnect();
        _pauseButton.Click += (_, _) =>
        {
            if (_playback.IsPaused) _playback.Resume(); else _playback.Pause();
            RefreshFromEngine();
        };
        _pinButton.Click += (_, _) =>
        {
            TopMost = !TopMost;
            _pinButton.BackColor = TopMost ? ModernUi.Accent : ModernUi.Surface;
        };
        // 10% fixed step — matches this window's existing preference for simple, discrete controls
        // (see _volumeDownButton's own doc comment) over a continuously-adjustable one.
        _volumeDownButton.Click += (_, _) => { _playback.AudioVolume -= 0.1f; RefreshFromEngine(); };
        _volumeUpButton.Click += (_, _) => { _playback.AudioVolume += 0.1f; RefreshFromEngine(); };
        // Fixed 10-second step — same reasoning as the volume buttons' own fixed step (see
        // _seekBackButton's own doc comment). RefreshFromEngine's own poll would eventually pick up
        // the new position anyway, but refreshing immediately here avoids a visible up-to-500ms lag
        // between clicking and the position label updating.
        _seekBackButton.Click += (_, _) => { _playback.SeekAudioRelative(TimeSpan.FromSeconds(-10)); RefreshFromEngine(); };
        _seekForwardButton.Click += (_, _) => { _playback.SeekAudioRelative(TimeSpan.FromSeconds(10)); RefreshFromEngine(); };
        // Flashes a brief result into _pageLabel — see _saveButton's own doc comment on why there's
        // no dedicated confirmation UI. RefreshFromEngine's own 500ms poll overwrites this with the
        // normal "内容显示在独立的WPS窗口中" text on its very next tick either way, so this is
        // necessarily transient regardless of what it says.
        _saveButton.Click += (_, _) => _pageLabel.Text = _playback.TrySaveCurrentOfficeDocument() ? "已保存" : "保存失败（见README已知风险）";

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _refreshTimer.Tick += (_, _) => RefreshFromEngine();

        // Bug fixed here: without this, a failed image/document (re)load left _thumbnail.Image
        // holding whatever Bitmap it last captured from PlaybackEngine.CurrentThumbnail — which the
        // failing renderer's own LoadAsync had, by that point, already disposed (see
        // PlaybackEngine.OnImageOrDocumentFailed's own doc comment on this exact "a downstream
        // consumer holds a stale reference to a Bitmap the renderer just disposed" shape, found and
        // fixed for ContentSurface the same round). _refreshTimer's own 500ms poll would eventually
        // re-read CurrentThumbnail (now correctly null after that fix) and self-heal, but that still
        // leaves up to 500ms where any repaint of this window (a move, a restore from minimized —
        // not a rare trigger) would try to draw an already-disposed Bitmap. Subscribing here closes
        // that window immediately instead of waiting on the next poll; never unsubscribed since this
        // window and the PlaybackEngine it was constructed with share the same process-lifetime
        // (same convention as this app's other long-lived paired singletons, e.g. Program.cs's own
        // _stateMachine.StateChanged subscription).
        _playback.PlaybackAbnormallyInterrupted += (_, _) => RefreshFromEngine();

        // "意外关闭后从主面板召回" (PLANNING.md §8.3) — this comment used to justify not building a
        // recall affordance by saying the Phase 4 main panel didn't exist yet; MainWindow has existed
        // since an earlier round and that reasoning is now stale (see this project's README "已知
        // 风险"), but a dedicated recall UI still isn't built: PLANNING.md §16第5项 itself lists the
        // exact interaction ("悬浮预览窗与主面板预览缩略图的召回交互细节") as still "待确认/待验证",
        // naming a "主面板预览缩略图" this repo has no equivalent of yet — building one specific
        // recall UI now would mean guessing at an interaction PLANNING.md's own author hasn't settled
        // on. What this line still does — the window never actually closes on its own X button, only
        // hides — already substantially covers the practical concern ("意外关闭" can't really happen,
        // there's nothing to accidentally lose) even without a dedicated recall button.
        FormClosing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
        ThemeManager.Apply(this, settingsStore.Current.Theme);
    }

    private void OnSettingsChanged(AppSettings settings) => ThemeManager.Apply(this, settings.Theme);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _settingsStore.SettingsChanged -= OnSettingsChanged;
            _refreshTimer.Dispose();
            _thumbnail.Image?.Dispose();
            _thumbnail.Image = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>"投" — PLANNING.md §8.3's "仅在扩展屏有输出时出现".</summary>
    public void ShowForActiveOutput()
    {
        RefreshFromEngine();
        Show();
        _refreshTimer.Start();
    }

    /// <summary>"断" — "无输出时不存在".</summary>
    public void HideForIdleOutput()
    {
        _refreshTimer.Stop();
        Hide();
    }

    private void ReplacePreview(Bitmap? source)
    {
        // Own the preview bitmap: the renderer may replace/dispose its frame between timer ticks.
        Image? next = null;
        if (source != null)
        {
            float scale = Math.Min(204f / source.Width, 114f / source.Height);
            next = new Bitmap(source, new Size(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale))));
        }
        Image? previous = _thumbnail.Image;
        _thumbnail.Image = next;
        previous?.Dispose();
    }

    private void RefreshFromEngine()
    {
        MediaFile? file = _playback.CurrentFile;
        if (file == null)
        {
            _fileLabel.Text = "(无内容 / 音频类文件预览尚未支持)";
            ReplacePreview(null);
            _pauseButton.Enabled = false;
            _pageLabel.Text = "";
            _volumeDownButton.Enabled = false;
            _volumeUpButton.Enabled = false;
            _volumeLabel.Text = "";
            _seekBackButton.Enabled = false;
            _seekForwardButton.Enabled = false;
            _positionLabel.Text = "";
            _saveButton.Enabled = false;
            return;
        }

        _fileLabel.Text = $"{Path.GetFileName(file.SourcePath)}\n[{file.Kind}]";
        ReplacePreview(_playback.CurrentThumbnail);

        bool isStandaloneAudio = file.Kind == MediaKind.Audio && !file.IsBackgroundAudio;
        bool isOfficeDocument = file.Kind == MediaKind.Document && WpsDocumentController.IsOfficeDocument(file.SourcePath);

        // Pause is meaningful for image/PDF (freezes the stay-duration clock), standalone audio, and
        // now video too (all three now have real pause-in-place — see PlaybackEngine.Pause's doc
        // comment) — still not background audio (which never reaches PlayStandaloneAudio in the
        // first place, see that method's doc comment), so this mirrors PlaybackEngine.Pause's own
        // guard exactly rather than re-deriving a slightly different condition here. Also not an
        // Office document — PlaybackEngine.PlayOfficeDocument deliberately never arms the
        // stay-duration timer Pause would otherwise freeze (see that method's own doc comment), so
        // this button would just be a dead click for one; leaving it enabled would look like a bug
        // rather than the deliberate scope choice it is.
        _pauseButton.Enabled = (file.Kind is MediaKind.Image or MediaKind.Video
            || (file.Kind == MediaKind.Document && !isOfficeDocument)) || isStandaloneAudio;
        _pauseButton.Text = _playback.IsPaused ? "继续" : "暂停";

        _saveButton.Enabled = isOfficeDocument;

        // Volume only means anything for standalone audio — image/PDF/video have no audio track of
        // their own that PlaybackEngine.AudioVolume touches (a video's own audio plays through
        // VideoContentController's separate AudioPlaybackClock, entirely untouched by this).
        _volumeDownButton.Enabled = isStandaloneAudio;
        _volumeUpButton.Enabled = isStandaloneAudio;
        _volumeLabel.Text = isStandaloneAudio ? $"音量: {(int)Math.Round(_playback.AudioVolume * 100)}%" : "";

        // Same isStandaloneAudio-only scope as the volume row just above — see PlaybackEngine.
        // AudioPosition/AudioDuration/SeekAudioRelative's own doc comments. FormatPosition's
        // "/ total" suffix only appears when AudioDuration is non-null (see that property's own
        // doc comment on why it can be null — AudioDecodeSource.TryGetDuration is best-effort).
        _seekBackButton.Enabled = isStandaloneAudio;
        _seekForwardButton.Enabled = isStandaloneAudio;
        _positionLabel.Text = isStandaloneAudio ? FormatPosition(_playback.AudioPosition, _playback.AudioDuration) : "";

        // Repurposed for an Office document — see WpsDocumentController's class doc comment on why
        // there's nothing here to page-turn or preview at all (the real content is in WPS's own
        // separate window); this at least explains the otherwise-blank thumbnail rather than looking
        // like a bug. Otherwise null (empty text) for anything that isn't a multi-page PDF — see this
        // class's and PlaybackEngine.DocumentPageInfo's own doc comments on why that only shows up
        // when 上一项/下一项 actually mean "turn a page" right now.
        _pageLabel.Text = isOfficeDocument
            ? "内容显示在独立的WPS窗口中"
            : _playback.DocumentPageInfo is { } page ? $"第{page.CurrentPage}页/共{page.PageCount}页" : "";
    }

    /// <summary>"0:15" or, when <paramref name="duration"/> is known, "0:15 / 3:42" — m:ss rather
    /// than h:mm:ss, matching the scale PLANNING.md §8.2's audio play-bar is meant for (standalone
    /// audio clips, not hours-long files); a position past 59:59 would print an m:ss value with more
    /// than two digits before the colon rather than rolling into an hours field, which is an
    /// acceptable display quirk for content this short-form rather than something worth a second
    /// code path for.</summary>
    private static string FormatPosition(TimeSpan position, TimeSpan? duration)
    {
        string Format(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        return duration.HasValue ? $"{Format(position)} / {Format(duration.Value)}" : Format(position);
    }

}
