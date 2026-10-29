using EveryStage.Terminal.Data;
using EveryStage.Terminal.Documents;

namespace EveryStage.Terminal.Playback;

/// <summary>
/// Keeps the Program channel's document in step with the Preview channel when both show the same document:
/// <list type="bullet">
/// <item>PDF / static slides: Preview's view position (page, offset inside the page, zoom, mode) is copied to
///   Program as the operator moves — when Program is live-synced and not frozen — or on "更新大屏".</item>
/// <item>Word / native slide show: both channels already show one shared editor / show capture; Program
///   either follows it live or holds its frame until "更新大屏" (see <see cref="PlaybackEngine.UpdateDocumentOutput"/>).</item>
/// </list>
/// "同一文档" is the same source path. The control bar targets <see cref="Target"/>: the Preview document when
/// Preview shows one, otherwise the Program document (so the operator can still drive what is on air).
/// </summary>
public sealed class DocumentProgramSync
{
    private readonly PlaybackEngine _preview;
    private readonly Func<PlaybackEngine?> _program;

    public DocumentProgramSync(PlaybackEngine preview, Func<PlaybackEngine?> program)
    {
        _preview = preview;
        _program = program;
        _preview.PdfViewMovedByUser += OnPreviewMoved;
    }

    public PlaybackEngine Preview => _preview;
    public PlaybackEngine? Program => _program();

    /// <summary>The engine the document control bar acts on: Program while it runs the native slide show of
    /// the previewed presentation (the show exists only there), else the Preview document when Preview
    /// shows one, otherwise the Program document (so the operator can still drive what is on air).</summary>
    public PlaybackEngine? Target => ProgramRunsShowOfPreview ? Program
        : _preview.Document != null ? _preview : Program?.Document != null ? Program : null;

    /// <summary>Program runs the native show of the presentation Preview browses as static slides.</summary>
    public bool ProgramRunsShowOfPreview => ProgramShowsSameDocument && Program?.DocumentMode == DocumentPlaybackMode.NativeShow;

    /// <summary>Presenter follow: while Program runs the show of the previewed presentation, Preview's
    /// static slide follows the show's slide, so the operator screen shows where the big screen is.
    /// Called on the UI refresh tick.</summary>
    public void FollowProgramShow()
    {
        if (!ProgramRunsShowOfPreview || Program?.OfficeSession is not SlideShowSession show || _preview.PdfView is not { } view) return;
        int slide = show.Position.Slide;
        if (slide >= 1 && slide <= view.PageCount && view.CurrentPage != slide) view.GoToPage(slide, user: false);
    }

    /// <summary>Program is on air with the same document as Preview.</summary>
    public bool ProgramShowsSameDocument =>
        Program is { Document: { } programDoc } program
        && program.State is PlaybackChannelState.Playing or PlaybackChannelState.Paused
        && _preview.Document is { } previewDoc
        && string.Equals(programDoc.File.SourcePath, previewDoc.File.SourcePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Program follows (true) or waits for "更新大屏" (false).</summary>
    public bool LiveSync => Program?.DocumentLiveSync ?? true;
    public bool Frozen => Program?.DocumentFrozen ?? false;

    private void OnPreviewMoved(PdfViewportState state)
    {
        if (!ProgramShowsSameDocument || Program is not { } program) return;
        if (ProgramRunsShowOfPreview)
        {
            // Browsing the static slides in Preview moves the show when Program follows live.
            if (program.DocumentLiveSync && !program.DocumentFrozen)
                _ = program.SlideCommandAsync(s => s.GoToSlideAsync(state.PageIndex + 1), "跳转", SlideMove.Jump);
            return;
        }
        if (program.DocumentLiveSync && !program.DocumentFrozen) program.ApplyPdfState(state);
    }

    public void SetLiveSync(bool live)
    {
        if (Program is not { } program) return;
        program.SetDocumentLiveSync(live);
        if (live && ProgramShowsSameDocument && _preview.PdfView is { } view) program.ApplyPdfState(view.State);
    }

    public void ToggleFreeze()
    {
        if (Program is not { } program) return;
        if (program.DocumentFrozen) UpdateProgram();
        else program.FreezeDocumentOutput();
    }

    /// <summary>"更新大屏": Program takes Preview's position (PDF) or the newest editor / show picture.</summary>
    public void UpdateProgram()
    {
        if (Program is not { } program) return;
        if (ProgramShowsSameDocument && _preview.PdfView is { } view) program.ApplyPdfState(view.State);
        program.UpdateDocumentOutput();
    }
}
