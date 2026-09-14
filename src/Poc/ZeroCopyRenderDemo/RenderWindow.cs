using System.Windows.Forms;

namespace EveryStage.Poc.ZeroCopyRenderDemo;

/// <summary>
/// Plain output window. Real Terminal uses a borderless topmost overlay (see PLANNING.md §5);
/// this demo keeps a normal resizable window so 1080p/4K swap-chain resize paths can be exercised.
/// </summary>
public sealed class RenderWindow : Form
{
    public event Action<int, int>? SizeChangedPx;

    public RenderWindow(string title, int width, int height)
    {
        Text = title;
        ClientSize = new Size(width, height);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        DoubleBuffered = false; // we own presentation via the DXGI swap chain, not GDI.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        if (ClientSize.Width > 0 && ClientSize.Height > 0)
            SizeChangedPx?.Invoke(ClientSize.Width, ClientSize.Height);
    }

    // Prevent WinForms from erasing the background every frame (avoids flicker fighting the swap chain).
    protected override void OnPaintBackground(PaintEventArgs e) { }
}
