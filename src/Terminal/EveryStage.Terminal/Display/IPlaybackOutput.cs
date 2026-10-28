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

    /// <summary>Screen rectangle of the extended display this output covers (Program), or null for an
    /// in-app surface (Preview). A native slide show runs at this size, behind the output window, and is
    /// captured onto it — no other application's window is ever shown on the extended display.</summary>
    Rectangle? OutputScreenBounds { get; }
}
