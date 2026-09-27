using System.Drawing;
using EveryStage.Terminal.ContentEngine;

namespace EveryStage.Terminal.Display;

/// <summary>
/// Where a <see cref="Playback.PlaybackEngine"/> draws. Two implementations:
/// <list type="bullet">
/// <item><see cref="OverlayWindow"/> — the extended display's full-screen window (Program).</item>
/// <item><see cref="PreviewSurface"/> — the in-app "本地预览 / 播控窗口" (Preview).</item>
/// </list>
/// Both expose the same two layers the engine switches between: a GDI+ <see cref="ContentSurface"/>
/// for images/PDF pages/audio visuals, and a video host HWND that a <see cref="VideoSurface"/>'s swap
/// chain presents into.
/// </summary>
public interface IPlaybackOutput
{
    ContentSurface ContentSurface { get; }

    void ShowImageSurface();

    void ShowVideoSurface();

    /// <summary>Runs <paramref name="action"/> on the UI thread that owns this output. Controllers
    /// raise completion/failure on their own worker threads; the engine marshals through this.</summary>
    void Post(Action action);

    /// <summary>True only for the extended display, which can cede its monitor to WPS's own window for
    /// Office documents. The Preview surface can't embed another application's window.</summary>
    bool CanHostExternalDocumentWindow { get; }

    /// <summary>Screen rectangle an external document window should cover (the bound monitor).</summary>
    Rectangle ExternalDocumentBounds { get; }

    /// <summary>Hide so the external document window is visible (Program: hide the overlay).</summary>
    void YieldToExternalWindow();

    /// <summary>Undo <see cref="YieldToExternalWindow"/>.</summary>
    void ReclaimFromExternalWindow();
}
