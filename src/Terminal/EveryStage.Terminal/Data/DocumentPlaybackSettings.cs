using EveryStage.Terminal.Documents;

namespace EveryStage.Terminal.Data;

/// <summary>Which application opens a Word document.</summary>
public enum OfficeOpenMode
{
    /// <summary>WPS if installed, otherwise Microsoft Word.</summary>
    Auto,
    Wps,
    MicrosoftOffice,
    /// <summary>No editor: a converted, read-only page view (works where the editor is unavailable).</summary>
    ReadOnlyPreview,
}

/// <summary>How a presentation plays.</summary>
public enum PresentationMode
{
    /// <summary>Native slide show when the file has animations, transitions or media; else static.</summary>
    Auto,
    /// <summary>A real PowerPoint/WPS slide show (animations, transitions, embedded media).</summary>
    Native,
    /// <summary>Slides converted to page images and played inside EveryStage (no animations or media).</summary>
    Static,
}

/// <summary>
/// Per-item document playback settings (activities and the file library). Null on a <see cref="MediaFile"/>
/// means defaults. Page and slide numbers are 1-based as operators see them. The completion action is the
/// file's own <see cref="MediaFile.OnCompletion"/>.
/// </summary>
public sealed class DocumentPlaybackSettings
{
    // ── PDF ──
    public PdfDisplayMode ReadingMode { get; set; } = PdfDisplayMode.Continuous;
    /// <summary>First page (PDF) or first slide (presentation).</summary>
    public int StartPage { get; set; } = 1;
    /// <summary>Position inside the start page, 0..1 (PDF).</summary>
    public double StartOffset { get; set; }
    /// <summary>Last page/slide to play before the completion action (null = the end of the document).</summary>
    public int? EndPage { get; set; }
    public PdfZoomMode ZoomMode { get; set; } = PdfZoomMode.FitWidth;
    public double CustomZoom { get; set; } = 1.0;
    /// <summary>Program follows Preview immediately (true) or only when "更新大屏" is pressed (false).</summary>
    public bool LiveSync { get; set; } = true;
    /// <summary>Automatic progress: seconds per page (PDF: continuous scroll speed or page interval;
    /// presentation: seconds per slide). Null = manual.</summary>
    public double? SecondsPerPage { get; set; }

    // ── Word ──
    public OfficeOpenMode OpenMode { get; set; } = OfficeOpenMode.Auto;
    public bool AllowEdit { get; set; } = true;
    /// <summary>Initial vertical scroll position, 0..100 (%).</summary>
    public double InitialScrollPercent { get; set; }

    // ── Presentation ──
    public PresentationMode PresentationMode { get; set; } = PresentationMode.Auto;
    public bool Loop { get; set; }

    public DocumentPlaybackSettings Clone() => (DocumentPlaybackSettings)MemberwiseClone();

    public PdfViewportState InitialPdfState() => new(
        Math.Max(0, StartPage - 1), Math.Clamp(StartOffset, 0, 0.999), ReadingMode, ZoomMode, CustomZoom);
}
