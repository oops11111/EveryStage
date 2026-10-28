using System.Drawing;
using System.Windows.Forms;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// Content that draws itself into a <see cref="ContentSurface"/> instead of handing it one bitmap — used
/// by the continuous PDF view (only the visible pages, from a page cache) and the presentation presenter
/// view. The surface forwards input to it; everything except <see cref="Paint"/> is optional.
/// </summary>
public interface IContentPainter
{
    void Paint(Graphics graphics, Rectangle bounds);

    /// <summary>Called when the painter is shown on <paramref name="host"/> (and <see cref="Detach"/> when
    /// it is replaced), so it can invalidate and read the host's size.</summary>
    void Attach(Control host) { }
    void Detach() { }

    void OnMouseWheel(MouseEventArgs e) { }
    void OnMouseDown(MouseEventArgs e) { }
    void OnMouseMove(MouseEventArgs e) { }
    void OnMouseUp(MouseEventArgs e) { }
    /// <summary>Returns true when the key was handled.</summary>
    bool OnKeyDown(Keys key) => false;
    void OnResize() { }
}
