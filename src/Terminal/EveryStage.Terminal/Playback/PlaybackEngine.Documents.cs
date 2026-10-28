using System.Drawing;
using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Documents;
using EveryStage.Terminal.Logging;

namespace EveryStage.Terminal.Playback;

/// <summary>How the current document is being played.</summary>
public enum DocumentPlaybackMode
{
    None,
    /// <summary>A PDF in the internal continuous / single-page viewer.</summary>
    Pdf,
    /// <summary>A presentation converted to page images (no animations, transitions or media).</summary>
    StaticSlides,
    /// <summary>A Word document converted to a read-only page view (no editor available or allowed).</summary>
    ReadOnlyPages,
    /// <summary>A Word document live in a WPS/Word editor, captured.</summary>
    WriterLive,
    /// <summary>A presentation as a native PowerPoint/WPS slide show, captured.</summary>
    NativeShow,
    /// <summary>Only the embedded cover (spreadsheets).</summary>
    Cover,
}

/// <summary>Snapshot of one channel's document state for the control bar and status line.</summary>
public sealed record DocumentStatus(
    MediaFile File,
    DocumentFormat Format,
    DocumentPlaybackMode Mode,
    int Page,
    int PageCount,
    PdfViewportState? PdfState,
    SlideShowPosition? Slide,
    bool LiveSync,
    bool Frozen,
    OfficeSessionStatus? SessionStatus,
    string? Notice,
    bool DialogPending,
    bool ReadOnly,
    string? SuiteName);

/// <summary>
/// Document playback for one channel (Preview or Program): PDF (continuous or single-page viewer), Word (live
/// editor capture or read-only conversion), presentations (native slide show or static slides) and the
/// Program-side sync flags. Office documents are shared sessions (<see cref="OfficeSessionManager"/>): the
/// Preview and Program channels show the same editor / slide show, and Program can follow it live or be
/// frozen.
/// </summary>
public sealed partial class PlaybackEngine
{
    private PdfDocumentView? _pdfView;
    private OfficeSession? _officeSession;
    private SharedFrame? _officeFrame;
    private int _officeFramePosted;
    private PresenterView? _presenter;
    private int _presenterBrowseSlide;
    private DocumentPlaybackMode _documentMode;
    private string? _documentNotice;
    private System.Windows.Forms.Timer? _documentAutoTimer;
    private DateTime _documentAutoLastTick;
    private bool _documentAutoPaused;
    private bool _userEndedShow;
    private readonly List<Action> _retiring = new();
    private PdfViewportState? _pendingPdfState;
    private int? _pendingSlide;

    /// <summary>Office sessions shared with the other channel (tests use their own manager).</summary>
    public OfficeSessionManager OfficeSessions { get; set; } = OfficeSessionManager.Shared;

    /// <summary>Converted PDFs for static slides / read-only Word pages.</summary>
    public DocumentConversionCache ConversionCache { get; set; } = DocumentConversionCache.Shared;

    /// <summary>Program: follow Preview's document position / the live editor (true), or change only on
    /// "更新大屏" (false). Starts from the item's <see cref="DocumentPlaybackSettings.LiveSync"/>.</summary>
    public bool DocumentLiveSync { get; set; } = true;

    /// <summary>Program: hold the current picture whatever happens in Preview or the editor.</summary>
    public bool DocumentFrozen { get; private set; }

    /// <summary>Raised on the UI thread when the document position, session state or sync flags change.</summary>
    public event Action? DocumentChanged;

    /// <summary>Raised on the UI thread when the operator moved the PDF view (Preview → Program sync).</summary>
    public event Action<PdfViewportState>? PdfViewMovedByUser;

    public PdfDocumentView? PdfView => _pdfView;
    public OfficeSession? OfficeSession => _officeSession;

    /// <summary>Sequence number of the captured frame this output shows (-1: none) — self-tests use it to
    /// see whether a frozen Program really kept its picture.</summary>
    internal long OfficeFrameSequence => _officeFrame?.Sequence ?? -1;
    public DocumentPlaybackMode DocumentMode => _currentFile?.Kind == MediaKind.Document ? _documentMode : DocumentPlaybackMode.None;

    public DocumentStatus? Document
    {
        get
        {
            if (_currentFile is not { Kind: MediaKind.Document } file) return null;
            var slide = (_officeSession as SlideShowSession)?.Position;
            int page = _pdfView?.CurrentPage ?? slide?.Slide ?? 0;
            int count = _pdfView?.PageCount ?? (_officeSession as SlideShowSession)?.SlideCount ?? 0;
            var writer = _officeSession as WriterSession;
            string? notice = _documentNotice;
            if (_officeSession is { } session)
            {
                if (session.Status == OfficeSessionStatus.Disconnected) notice = session.StatusMessage ?? "文档窗口已断开";
                else if (session.DialogPending) notice = $"{session.SuiteName} 弹出了对话框，请在编辑窗口中处理（大屏不受影响）";
                else if (session.Status is OfficeSessionStatus.Editing or OfficeSessionStatus.Ended or OfficeSessionStatus.Opening) notice = session.StatusMessage ?? notice;
                else if (writer?.ReadOnlyReason is { } reason) notice = "只读：" + reason;
            }
            return new DocumentStatus(file, DocumentFormats.Of(file.SourcePath), _documentMode, page, count, _pdfView?.State, slide,
                DocumentLiveSync, DocumentFrozen, _officeSession?.Status, notice, _officeSession?.DialogPending == true,
                writer?.ReadOnly == true, _officeSession?.SuiteName);
        }
    }

    private void RaiseDocumentChanged()
    {
        foreach (var handler in DocumentChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action)handler)(); } catch (Exception) { }
        }
    }

    private DocumentLogger DocLog => OfficeSessions.Log;

    // ── Start ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>PlayFile's Document branch. The previous document keeps showing until the new one has
    /// its first picture (pre-roll), then it is released.</summary>
    private void PlayDocument(MediaFile file)
    {
        var settings = file.Document ?? new DocumentPlaybackSettings();
        var format = DocumentFormats.Of(file.SourcePath);
        _documentNotice = null;
        _userEndedShow = false;
        DocumentLiveSync = settings.LiveSync;
        DocumentFrozen = false;
        var pdfState = _pendingPdfState;
        int? slide = _pendingSlide;
        switch (format)
        {
            case DocumentFormat.Pdf:
                _documentMode = DocumentPlaybackMode.Pdf;
                _ = PlayPdfAsync(file, file.SourcePath, pdfState ?? settings.InitialPdfState(), settings);
                break;
            case DocumentFormat.Word:
                if (settings.OpenMode != OfficeOpenMode.ReadOnlyPreview && OfficeInstallation.PickWriter(settings.OpenMode) is { } suite)
                {
                    _documentMode = DocumentPlaybackMode.WriterLive;
                    _ = PlayWriterAsync(file, suite, settings);
                }
                else
                {
                    _documentMode = DocumentPlaybackMode.ReadOnlyPages;
                    _documentNotice = settings.OpenMode == OfficeOpenMode.ReadOnlyPreview ? "只读预览（按活动设置）" : "未安装 WPS/Word 编辑组件：只读预览";
                    _ = PlayConvertedAsync(file, settings, pdfState ?? new PdfViewportState(0, 0, PdfDisplayMode.Continuous, PdfZoomMode.FitWidth));
                }
                break;
            case DocumentFormat.Presentation:
                var mode = settings.PresentationMode;
                if (mode == PresentationMode.Auto)
                    mode = PresentationInspector.Inspect(file.SourcePath).NeedsNativeShow ? PresentationMode.Native : PresentationMode.Static;
                int startSlide = slide ?? Math.Max(1, settings.StartPage);
                if (mode == PresentationMode.Native && OfficeInstallation.PickPresentation() is { } showSuite)
                {
                    _documentMode = DocumentPlaybackMode.NativeShow;
                    _ = PlayNativeShowAsync(file, showSuite, settings, startSlide);
                }
                else
                {
                    _documentMode = DocumentPlaybackMode.StaticSlides;
                    _documentNotice = mode == PresentationMode.Native
                        ? "未安装 PowerPoint/WPS 演示：以静态幻灯片播放（不含动画、转场和嵌入媒体）"
                        : "静态幻灯片：不含动画、转场和嵌入媒体";
                    _ = PlayConvertedAsync(file, settings, new PdfViewportState(startSlide - 1, 0, PdfDisplayMode.SinglePage, PdfZoomMode.FitPage));
                }
                break;
            default:
                _documentMode = DocumentPlaybackMode.Cover;
                CompleteDocumentRetirement();
                _output.ShowImageSurface();
                ShowOfficeDocumentPreview(file);
                break;
        }
        RaiseDocumentChanged();
    }

    private async Task PlayPdfAsync(MediaFile file, string pdfPath, PdfViewportState initial, DocumentPlaybackSettings settings)
    {
        int version = _contentLoadVersion;
        PdfDocumentView view;
        try
        {
            view = await PdfDocumentView.OpenAsync(pdfPath, interactive: Channel == PlaybackChannel.Preview, initial);
        }
        catch (Exception ex)
        {
            if (version == _contentLoadVersion && ReferenceEquals(file, _currentFile))
            {
                DocLog.LogFailure("打开文件", file.SourcePath, "PDFium", null, ex);
                CompleteDocumentRetirement();
                OnImageOrDocumentFailed(file, ex);
            }
            return;
        }
        if (version != _contentLoadVersion || !ReferenceEquals(file, _currentFile))
        {
            view.Dispose();
            return;
        }
        _pdfView = view;
        view.StateChanged += (state, user) =>
        {
            if (!ReferenceEquals(view, _pdfView)) return;
            if (user) PdfViewMovedByUser?.Invoke(state);
            CheckPdfEndPage(file, settings);
            RaiseDocumentChanged();
        };
        view.PageRenderFailed += (page, ex) =>
        {
            DocLog.LogFailure("渲染页面", file.SourcePath, "PDFium", null, ex);
            _documentNotice = $"第 {page + 1} 页渲染失败";
            RaiseDocumentChanged();
        };
        _output.ShowImageSurface();
        _output.ContentSurface.SetPainter(view);
        CompleteDocumentRetirement();
        SetState(IsPaused ? PlaybackChannelState.Paused : PlaybackChannelState.Playing);
        if (settings.SecondsPerPage is > 0) ArmDocumentAutoTimer(file, settings);
        else ArmStayDurationTimer(file);
        RaiseDocumentChanged();
    }

    private async Task PlayConvertedAsync(MediaFile file, DocumentPlaybackSettings settings, PdfViewportState initial)
    {
        int version = _contentLoadVersion;
        DocumentConversionCache.Entry entry;
        try
        {
            entry = ConversionCache.TryGet(file.SourcePath) ?? await ConversionCache.GetOrConvertAsync(file.SourcePath, DocumentFormats.Of(file.SourcePath));
        }
        catch (Exception ex)
        {
            if (version == _contentLoadVersion && ReferenceEquals(file, _currentFile))
            {
                CompleteDocumentRetirement();
                // Nothing can render it: fall back to the embedded cover, with the reason as the notice.
                _documentMode = DocumentPlaybackMode.Cover;
                _documentNotice = ex.Message;
                if (DocumentFormats.Of(file.SourcePath) == DocumentFormat.Presentation && OfficeThumbnailReader.Read(file.SourcePath, new Size(640, 360)) is { } cover)
                {
                    cover.Dispose();
                    ShowOfficeDocumentPreview(file);
                    RaiseDocumentChanged();
                }
                else OnImageOrDocumentFailed(file, ex);
            }
            return;
        }
        if (version != _contentLoadVersion || !ReferenceEquals(file, _currentFile)) return;
        await PlayPdfAsync(file, entry.PdfPath, initial, settings);
    }

    private async Task PlayWriterAsync(MediaFile file, OfficeSuite suite, DocumentPlaybackSettings settings)
    {
        int version = _contentLoadVersion;
        var session = OfficeSessions.AcquireWriter(file.SourcePath, suite);
        try
        {
            await session.EnsureOpenAsync(settings);
        }
        catch (Exception ex)
        {
            _ = OfficeSessions.ReleaseAsync(session);
            if (version == _contentLoadVersion && ReferenceEquals(file, _currentFile))
            {
                CompleteDocumentRetirement();
                OnImageOrDocumentFailed(file, ex);
            }
            return;
        }
        if (version != _contentLoadVersion || !ReferenceEquals(file, _currentFile))
        {
            _ = OfficeSessions.ReleaseAsync(session);
            return;
        }
        AttachOfficeSession(session);
        ShowLatestOfficeFrame(force: true);
    }

    private async Task PlayNativeShowAsync(MediaFile file, OfficeSuite suite, DocumentPlaybackSettings settings, int startSlide, bool triedOther = false)
    {
        int version = _contentLoadVersion;
        var bounds = _output.OutputScreenBounds;
        var session = OfficeSessions.AcquireSlideShow(file.SourcePath, suite, startSlide, settings.EndPage, settings.Loop,
            forProgram: bounds != null, programBounds: bounds ?? OfficeSessionManager.ParkingBounds);
        try
        {
            await session.EnsureOpenAsync(settings);
        }
        catch (Exception ex)
        {
            _ = OfficeSessions.ReleaseAsync(session);
            if (version != _contentLoadVersion || !ReferenceEquals(file, _currentFile)) return;
            // PowerPoint refuses some files that WPS Presentation opens (and the other way round): try the
            // other installed engine once before giving up.
            var other = suite == OfficeSuite.MicrosoftOffice ? OfficeSuite.Wps : OfficeSuite.MicrosoftOffice;
            bool otherInstalled = OfficeInstallation.IsRegistered(other == OfficeSuite.Wps ? OfficeInstallation.WpsPresentation : OfficeInstallation.PowerPoint);
            if (!triedOther && otherInstalled && ex is OfficeOperationException { Stage: OfficeStage.OpenFile or OfficeStage.StartSlideShow or OfficeStage.GetWindow })
            {
                DocLog.LogEvent("show_engine_fallback", file.SourcePath, session.SuiteName, session.Version, ex.Message);
                await PlayNativeShowAsync(file, other, settings, startSlide, triedOther: true);
                return;
            }
            if (ex is OfficeOperationException office)
            {
                // No engine could run the show: play the slides statically instead of failing.
                _documentMode = DocumentPlaybackMode.StaticSlides;
                _documentNotice = $"原生放映失败（{OfficeOperationException.StageName(office.Stage)}：{office.Message}），已改为静态幻灯片";
                RaiseDocumentChanged();
                await PlayConvertedAsync(file, settings, new PdfViewportState(startSlide - 1, 0, PdfDisplayMode.SinglePage, PdfZoomMode.FitPage));
                return;
            }
            CompleteDocumentRetirement();
            OnImageOrDocumentFailed(file, ex);
            return;
        }
        if (version != _contentLoadVersion || !ReferenceEquals(file, _currentFile))
        {
            _ = OfficeSessions.ReleaseAsync(session);
            return;
        }
        AttachOfficeSession(session);
        if (Channel == PlaybackChannel.Preview)
        {
            _presenter = new PresenterView();
            _presenterBrowseSlide = 0;
        }
        ShowLatestOfficeFrame(force: true);
        if (settings.SecondsPerPage is > 0) ArmDocumentAutoTimer(file, settings);
        _ = WatchForBlankShowAsync(file, session, settings, startSlide, version);
    }

    /// <summary>Some slide-show engines (WPS Presentation's GPU-rendered show on this test machine) cannot
    /// be captured: every frame is black. If the show stays completely black for four seconds, switch the
    /// item to static slides instead of putting a black screen on Program.</summary>
    private async Task WatchForBlankShowAsync(MediaFile file, SlideShowSession session, DocumentPlaybackSettings settings, int startSlide, int version)
    {
        await Task.Delay(4000);
        if (version != _contentLoadVersion || !ReferenceEquals(session, _officeSession) || session.Status != OfficeSessionStatus.Ready) return;
        var frame = session.AcquireLatestFrame();
        bool blank;
        try { blank = frame == null || CaptureFrames.IsBlank(frame.Bitmap); }
        finally { frame?.Release(); }
        if (!blank) return;
        DocLog.LogEvent("show_capture_blank", file.SourcePath, session.SuiteName, session.Version, "falling back to static slides");
        await FallBackToStaticSlidesAsync(file, settings, Math.Max(startSlide, session.Position.Slide),
            $"{session.SuiteName} 的放映画面无法捕获，已改为静态幻灯片（不含动画、转场和嵌入媒体）");
    }

    /// <summary>The native show cannot continue (capture blank, application crashed): replace it with
    /// static slides at <paramref name="slide"/>, keeping the reason on screen.</summary>
    private async Task FallBackToStaticSlidesAsync(MediaFile file, DocumentPlaybackSettings settings, int slide, string notice)
    {
        RetireDocument();
        _documentMode = DocumentPlaybackMode.StaticSlides;
        _documentNotice = notice;
        await PlayConvertedAsync(file, settings, new PdfViewportState(Math.Max(1, slide) - 1, 0, PdfDisplayMode.SinglePage, PdfZoomMode.FitPage));
    }

    // ── Office frames and session state ──────────────────────────────────────────────────────────

    private void AttachOfficeSession(OfficeSession session)
    {
        _officeSession = session;
        session.FrameAvailable += OnOfficeFrameAvailable;
        session.Changed += OnOfficeSessionChanged;
    }

    private void DetachOfficeSession(bool release)
    {
        var session = _officeSession;
        if (session == null) return;
        session.FrameAvailable -= OnOfficeFrameAvailable;
        session.Changed -= OnOfficeSessionChanged;
        _officeSession = null;
        if (release)
        {
            if (Channel == PlaybackChannel.Program && session is SlideShowSession show && session.References > 1) OfficeSessions.UnbindProgram(show);
            _ = OfficeSessions.ReleaseAsync(session);
        }
    }

    private void OnOfficeFrameAvailable()
    {
        if (Interlocked.Exchange(ref _officeFramePosted, 1) == 1) return;
        _output.Post(() =>
        {
            Interlocked.Exchange(ref _officeFramePosted, 0);
            ShowLatestOfficeFrame(force: false);
        });
    }

    /// <summary>Puts the session's newest frame on this output — unless this is Program and it is frozen
    /// or not following live (it keeps its current frame until "更新大屏").</summary>
    private void ShowLatestOfficeFrame(bool force)
    {
        var session = _officeSession;
        if (session == null || _currentFile == null) return;
        bool hold = Channel == PlaybackChannel.Program && _officeFrame != null && (DocumentFrozen || !DocumentLiveSync);
        if (hold && !force) return;
        var frame = session.AcquireLatestFrame();
        if (frame == null) return;
        var previous = _officeFrame;
        _officeFrame = frame;
        if (_presenter != null)
        {
            _presenter.SetCurrent(frame.Bitmap);
            if (!ReferenceEquals(_output.ContentSurface.Painter, _presenter))
            {
                _output.ShowImageSurface();
                _output.ContentSurface.SetPainter(_presenter);
                _ = RefreshPresenterAsync();
            }
            else _output.ContentSurface.Invalidate();
        }
        else
        {
            _output.ShowImageSurface();
            _output.ContentSurface.SetFrame(frame.Bitmap, live: true);
        }
        previous?.Release();
        if (State == PlaybackChannelState.Loading)
        {
            CompleteDocumentRetirement();
            SetState(IsPaused ? PlaybackChannelState.Paused : PlaybackChannelState.Playing);
            RaiseDocumentChanged();
        }
    }

    private int _lastPresenterSlide = -1;

    private void OnOfficeSessionChanged()
    {
        _output.Post(() =>
        {
            var session = _officeSession;
            if (session == null || _currentFile == null) return;
            if (session.Status == OfficeSessionStatus.Failed && session.Error is { } error)
            {
                if (session is SlideShowSession crashed && _documentMode == DocumentPlaybackMode.NativeShow && error.Stage == OfficeStage.Control)
                {
                    // The presentation application died mid-show.
                    DocLog.LogEvent("show_application_exited", _currentFile.SourcePath, crashed.SuiteName, crashed.Version, "falling back to static slides");
                    _ = FallBackToStaticSlidesAsync(_currentFile, _currentFile.Document ?? new DocumentPlaybackSettings(), crashed.Position.Slide,
                        $"{error.Message}，已改为静态幻灯片（不含动画、转场和嵌入媒体）");
                    return;
                }
                OnImageOrDocumentFailed(_currentFile, error);
                return;
            }
            if (session is SlideShowSession show)
            {
                if (show.Status == OfficeSessionStatus.Ended && !_userEndedShow && State == PlaybackChannelState.Playing && Channel == PlaybackChannel.Program)
                    HandleCompletion(_currentFile);
                if (_presenter != null && show.Position.Slide != _lastPresenterSlide) _ = RefreshPresenterAsync();
            }
            RaiseDocumentChanged();
        });
    }

    /// <summary>Updates the presenter view's next slide, notes and position lines.</summary>
    private async Task RefreshPresenterAsync()
    {
        if (_presenter is not { } presenter || _officeSession is not SlideShowSession show) return;
        int slide = _presenterBrowseSlide > 0 ? _presenterBrowseSlide : Math.Max(1, show.Position.Slide);
        _lastPresenterSlide = show.Position.Slide;
        var next = await show.GetSlideImageAsync(slide + 1, 640);
        string notes = await show.NotesAsync(slide);
        Bitmap? browse = _presenterBrowseSlide > 0 ? await show.GetSlideImageAsync(_presenterBrowseSlide, 960) : null;
        if (!ReferenceEquals(presenter, _presenter)) { next?.Dispose(); browse?.Dispose(); return; }
        presenter.SetNext(next);
        presenter.SetBrowseImage(browse);
        var p = show.Position;
        string header = _presenterBrowseSlide > 0
            ? $"浏览第 {_presenterBrowseSlide} / {show.SlideCount} 张（大屏：第 {p.Slide} 张）"
            : $"第 {p.Slide} / {show.SlideCount} 张" + (p.Clicks > 0 ? $" · 动画 {p.Click}/{p.Clicks}" : "");
        string state = show.Status switch
        {
            OfficeSessionStatus.Editing => "编辑中",
            OfficeSessionStatus.Ended => "放映已结束",
            OfficeSessionStatus.Disconnected => "放映窗口已断开",
            _ => p.Screen switch
            {
                SlideShowScreen.Black => "黑屏",
                SlideShowScreen.White => "白屏",
                SlideShowScreen.Paused => "已暂停",
                _ => $"放映中（{show.SuiteName}）",
            },
        };
        presenter.SetInfo(header, state, notes);
    }

    // ── Program sync (called by the Preview→Program coordinator and the control bar) ─────────────

    /// <summary>Moves this channel's PDF view (Program following Preview).</summary>
    public void ApplyPdfState(PdfViewportState state)
    {
        if (_pdfView == null) return;
        _pdfView.ApplyState(state);
    }

    public void SetDocumentLiveSync(bool live)
    {
        DocumentLiveSync = live;
        if (live) DocumentFrozen = false;
        if (live) ShowLatestOfficeFrame(force: true);
        RaiseDocumentChanged();
    }

    /// <summary>"冻结大屏": hold the current picture.</summary>
    public void FreezeDocumentOutput()
    {
        DocumentFrozen = true;
        RaiseDocumentChanged();
    }

    /// <summary>"更新大屏" for a live document: take the editor's / show's newest picture now and follow
    /// live again when live sync is on.</summary>
    public void UpdateDocumentOutput()
    {
        DocumentFrozen = false;
        ShowLatestOfficeFrame(force: true);
        RaiseDocumentChanged();
    }

    /// <summary>Program adopting a shared Office session that Preview already opened (Take).</summary>
    internal bool SharesSessionWith(PlaybackEngine other) => _officeSession != null && ReferenceEquals(_officeSession, other._officeSession);

    // ── Operator commands ────────────────────────────────────────────────────────────────────────

    /// <summary>Runs a document command, turning failures into a status notice instead of an exception.</summary>
    private async Task<bool> RunDocumentCommandAsync(Func<Task> command, string what)
    {
        try
        {
            await command();
            _documentNotice = null;
            RaiseDocumentChanged();
            return true;
        }
        catch (Exception ex)
        {
            if (_currentFile != null) DocLog.LogFailure(what, _currentFile.SourcePath, _officeSession?.SuiteName, _officeSession?.Version, ex);
            _documentNotice = ex is OfficeOperationException office ? $"{OfficeOperationException.StageName(office.Stage)}失败：{office.Message}" : $"{what}失败：{ex.Message}";
            RaiseDocumentChanged();
            return false;
        }
    }

    public Task<bool> SaveDocumentAsync() => _officeSession is WriterSession writer
        ? RunDocumentCommandAsync(writer.SaveAsync, "保存") : Task.FromResult(false);

    public Task<bool> SaveDocumentAsAsync(string path) => _officeSession is WriterSession writer
        ? RunDocumentCommandAsync(() => writer.SaveAsAsync(path), "另存为") : Task.FromResult(false);

    public void OpenDocumentEditor()
    {
        if (_officeSession is WriterSession writer) writer.BringEditorToFront();
    }

    /// <summary>"适应页面" / "适应宽度" for a live Word document (the editor's zoom).</summary>
    public Task<bool> SetWriterPageFitAsync(bool wholePage) => _officeSession is WriterSession writer
        ? RunDocumentCommandAsync(() => writer.SetPageFitAsync(wholePage), wholePage ? "适应页面" : "适应宽度") : Task.FromResult(false);

    /// <summary>"原生 / 静态": replays the current presentation in the other mode at the same slide. Changes
    /// this item's setting in memory (saved with the activity / library like any other setting).</summary>
    public void SwitchPresentationMode(PresentationMode mode)
    {
        if (_currentFile is not { Kind: MediaKind.Document } file || DocumentFormats.Of(file.SourcePath) != DocumentFormat.Presentation) return;
        int slide = Math.Max(1, Document?.Page ?? 1);
        file.Document ??= new DocumentPlaybackSettings();
        file.Document.PresentationMode = mode;
        _pendingSlide = slide;
        _pendingPdfState = null;
        try { RetryCurrentFile(); }
        finally { _pendingSlide = null; }
    }

    /// <summary>"开始放映" after the show ended: restarts it at the slide it ended on.</summary>
    public Task<bool> RestartShowAsync() => _officeSession is SlideShowSession show
        ? RunDocumentCommandAsync(() => { _userEndedShow = false; return show.ReloadAsync(saveEdits: false); }, "开始放映")
        : Task.FromResult(false);

    /// <summary>"编辑": ends the show (Program keeps its last frame) and opens the editor.</summary>
    public Task<bool> EditPresentationAsync()
    {
        if (_officeSession is not SlideShowSession show) return Task.FromResult(false);
        _userEndedShow = true;
        return RunDocumentCommandAsync(show.EnterEditAsync, "编辑");
    }

    /// <summary>"重新载入放映" after editing; <paramref name="save"/> saves the edits first.</summary>
    public Task<bool> ReloadPresentationAsync(bool save) => _officeSession is SlideShowSession show
        ? RunDocumentCommandAsync(() => { _userEndedShow = false; return show.ReloadAsync(save); }, "重新载入放映")
        : Task.FromResult(false);

    public Task<bool> ReconnectDocumentAsync() => _officeSession is WriterSession writer
        ? RunDocumentCommandAsync(writer.ReconnectAsync, "重新连接")
        : _officeSession is SlideShowSession show ? RunDocumentCommandAsync(() => show.ReloadAsync(saveEdits: false), "重新连接") : Task.FromResult(false);

    public Task<bool> SlideCommandAsync(Func<SlideShowSession, Task> command, string what)
    {
        if (_officeSession is not SlideShowSession show) return Task.FromResult(false);
        _presenterBrowseSlide = 0;
        return RunDocumentCommandAsync(() => command(show), what);
    }

    /// <summary>"结束放映": ends the show; Program holds its last frame (no auto-advance).</summary>
    public Task<bool> EndShowAsync()
    {
        _userEndedShow = true;
        return SlideCommandAsync(s => s.EndShowAsync(), "结束放映");
    }

    /// <summary>Preview browsing slides without moving the live show (frozen / not live).</summary>
    public void BrowseSlide(int slide)
    {
        if (_officeSession is not SlideShowSession show) return;
        _presenterBrowseSlide = Math.Clamp(slide, 1, show.SlideCount);
        _ = RefreshPresenterAsync();
        RaiseDocumentChanged();
    }

    public int BrowsedSlide => _presenterBrowseSlide > 0 ? _presenterBrowseSlide : (_officeSession as SlideShowSession)?.Position.Slide ?? 0;

    // ── Automatic progress ───────────────────────────────────────────────────────────────────────

    /// <summary>Automatic progress for a document: continuous PDF scrolls smoothly at one page per
    /// <see cref="DocumentPlaybackSettings.SecondsPerPage"/>; single pages / static slides / the native show
    /// advance one step per interval. Reaching the end runs the completion action.</summary>
    private void ArmDocumentAutoTimer(MediaFile file, DocumentPlaybackSettings settings)
    {
        StopDocumentAutoTimer();
        double seconds = settings.SecondsPerPage ?? 0;
        if (seconds <= 0) return;
        bool smooth = _pdfView?.State.DisplayMode == PdfDisplayMode.Continuous && _documentMode is DocumentPlaybackMode.Pdf or DocumentPlaybackMode.ReadOnlyPages;
        var timer = new System.Windows.Forms.Timer { Interval = smooth ? 40 : Math.Max(200, (int)(seconds * 1000)) };
        _documentAutoLastTick = DateTime.UtcNow;
        timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(file, _currentFile) || _documentAutoPaused) return;
            if (smooth && _pdfView is { } view)
            {
                var now = DateTime.UtcNow;
                double elapsed = (now - _documentAutoLastTick).TotalSeconds;
                _documentAutoLastTick = now;
                if (view.AtEnd) { FinishDocument(file, settings); return; }
                double pageHeight = Math.Max(50, (_output.ContentSurface.ClientSize.Width) * 1.3);
                view.ScrollBy(pageHeight * elapsed / seconds, user: false);
                return;
            }
            if (_pdfView is { } pages)
            {
                if (!pages.NextPage(user: false))
                {
                    if (settings.Loop && _documentMode == DocumentPlaybackMode.StaticSlides) pages.GoToPage(1, user: false);
                    else FinishDocument(file, settings);
                }
                return;
            }
            if (_officeSession is SlideShowSession show && show.Status == OfficeSessionStatus.Ready) _ = show.NextStepAsync();
        };
        _documentAutoTimer = timer;
        _documentAutoPaused = false;
        timer.Start();
    }

    private void FinishDocument(MediaFile file, DocumentPlaybackSettings settings)
    {
        StopDocumentAutoTimer();
        HandleCompletion(file);
    }

    private void CheckPdfEndPage(MediaFile file, DocumentPlaybackSettings settings)
    {
        if (settings.EndPage is not { } end || _pdfView is not { } view || _documentAutoTimer == null) return;
        if (view.State.PageIndex >= end) FinishDocument(file, settings); // moved past the last page of the range
    }

    private void StopDocumentAutoTimer()
    {
        _documentAutoTimer?.Stop();
        _documentAutoTimer?.Dispose();
        _documentAutoTimer = null;
        _documentAutoPaused = false;
    }

    private bool PauseDocumentAuto()
    {
        if (_documentAutoTimer == null) return false;
        _documentAutoPaused = true;
        return true;
    }

    private bool ResumeDocumentAuto()
    {
        if (_documentAutoTimer == null) return false;
        _documentAutoPaused = false;
        _documentAutoLastTick = DateTime.UtcNow;
        return true;
    }

    // ── Navigation used by 上一项/下一项 ─────────────────────────────────────────────────────────

    /// <summary>Page / animation step inside the current document; false when there is nothing further
    /// (the caller then moves along the playlist).</summary>
    private bool TryStepDocument(int direction)
    {
        if (_currentFile?.Kind != MediaKind.Document) return false;
        if (_pdfView is { } view) return direction > 0 ? view.NextPage() : view.PreviousPage();
        if (_officeSession is SlideShowSession show && show.Status == OfficeSessionStatus.Ready)
        {
            var p = show.Position;
            if (direction > 0 && p.Slide >= show.SlideCount && p.Click >= p.Clicks) return false;
            if (direction < 0 && p.Slide <= 1 && p.Click <= 0) return false;
            _ = SlideCommandAsync(s => direction > 0 ? s.NextStepAsync() : s.PreviousStepAsync(), direction > 0 ? "下一步" : "上一步");
            return true;
        }
        return false;
    }

    // ── Release ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Moves the current document's resources aside: they keep drawing until the next picture is
    /// on screen (<see cref="CompleteDocumentRetirement"/>), which avoids a black gap between documents.</summary>
    private void RetireDocument()
    {
        StopDocumentAutoTimer();
        var view = _pdfView;
        var frame = _officeFrame;
        var presenter = _presenter;
        _pdfView = null;
        _officeFrame = null;
        _presenter = null;
        DetachOfficeSession(release: true);
        if (view == null && frame == null && presenter == null) return;
        _retiring.Add(() =>
        {
            var surface = _output.ContentSurface;
            if (view != null && ReferenceEquals(surface.Painter, view)) surface.SetFrame(null);
            if (presenter != null && ReferenceEquals(surface.Painter, presenter)) surface.SetFrame(null);
            if (frame != null && ReferenceEquals(surface.CurrentFrame, frame.Bitmap)) surface.SetFrame(null);
            view?.Dispose();
            presenter?.Dispose();
            frame?.Release();
        });
    }

    private void CompleteDocumentRetirement()
    {
        var pending = _retiring.ToArray();
        _retiring.Clear();
        foreach (var release in pending)
        {
            try { release(); } catch (Exception) { }
        }
    }

    /// <summary>Stop / device cast / output off / dispose: release the document now.</summary>
    private void ReleaseDocumentNow()
    {
        RetireDocument();
        CompleteDocumentRetirement();
        _documentMode = DocumentPlaybackMode.None;
        _documentNotice = null;
        DocumentFrozen = false;
    }
}
