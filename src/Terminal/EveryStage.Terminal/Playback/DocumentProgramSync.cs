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

    /// <summary>The engine the document control bar acts on.</summary>
    public PlaybackEngine? Target => _preview.Document != null ? _preview : Program?.Document != null ? Program : null;

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
