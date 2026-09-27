using System.Drawing;
using System.Windows.Forms;
using EveryStage.Terminal.ContentEngine;

namespace EveryStage.Terminal.Display;

/// <summary>
/// The Preview channel's output: an in-app monitor inside "本地预览 / 播控窗口". Same two layers as
/// <see cref="OverlayWindow"/> (a video host HWND for the swap chain, and a <see cref="ContentSurface"/>
/// for everything drawn with GDI+), so one <see cref="Playback.PlaybackEngine"/> implementation drives
/// both channels.
///
/// Content is laid out in a centred viewport of <see cref="ViewportAspect"/> (4:3 by default, per the
/// play-control spec); inside it, images/pages are aspect-fitted by <see cref="ContentSurface"/> and video
/// by the swap chain's <see cref="EveryStage.Rendering.VideoScaleMode"/>, so nothing is ever stretched.
/// Around the viewport the control shows its own (theme) background.
///
/// An optional overlay card (the error card) can be placed over the preview area with
/// <see cref="SetOverlayCard"/>.
/// </summary>
public sealed class PreviewSurface : Control, IPlaybackOutput
{
    private double _viewportAspect = 4.0 / 3.0;
    private Control? _overlayCard;

    public Control VideoHost { get; }
    public ContentSurface ContentSurface { get; }

    /// <summary>Raised when the video host's pixel size changes, so the owner can resize the swap chain.</summary>
    public event Action<int, int>? VideoHostResized;

    /// <summary>Width / height of the content viewport.</summary>
    public double ViewportAspect
    {
        get => _viewportAspect;
        set { _viewportAspect = value > 0 ? value : 4.0 / 3.0; PerformLayout(); Invalidate(); }
    }

    /// <summary>Viewport rectangle in client coordinates (for tests and overlays).</summary>
    public Rectangle Viewport { get; private set; }

    public PreviewSurface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(4, 16, 31);
        VideoHost = new Control { BackColor = Color.Black, Visible = false };
        ContentSurface = new ContentSurface { Dock = DockStyle.None };
        Controls.Add(ContentSurface);
        Controls.Add(VideoHost);
        VideoHost.SizeChanged += (_, _) => VideoHostResized?.Invoke(Math.Max(1, VideoHost.ClientSize.Width), Math.Max(1, VideoHost.ClientSize.Height));
        // Controllers post back from worker threads; the handle must exist before the first Post.
        _ = Handle;
        _ = VideoHost.Handle;
    }

    public void ShowImageSurface()
    {
        VideoHost.Visible = false;
        ContentSurface.Visible = true;
        ContentSurface.BringToFront();
        _overlayCard?.BringToFront();
    }

    public void ShowVideoSurface()
    {
        ContentSurface.Visible = false;
        VideoHost.Visible = true;
        VideoHost.BringToFront();
        _overlayCard?.BringToFront();
    }

    /// <summary>Places <paramref name="card"/> over the whole preview area (null removes it). It covers the
    /// full control rather than just the viewport: in a short dock row the 4:3 viewport can be too small
    /// for an error card with its action buttons. The previous card is removed but not disposed.</summary>
    public void SetOverlayCard(Control? card)
    {
        if (ReferenceEquals(card, _overlayCard)) return;
        if (_overlayCard != null) Controls.Remove(_overlayCard);
        _overlayCard = card;
        if (card != null)
        {
            Controls.Add(card);
            card.Bounds = ClientRectangle;
            card.BringToFront();
        }
    }

    public void Post(Action action)
    {
        // Always deferred, like OverlayWindow: callers (controller worker threads) expect async delivery.
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(action);
    }

    public bool CanHostExternalDocumentWindow => false;
    public Rectangle ExternalDocumentBounds => Rectangle.Empty;
    public void YieldToExternalWindow() { }
    public void ReclaimFromExternalWindow() { }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var client = ClientRectangle;
        if (client.Width <= 0 || client.Height <= 0) return;
        int width = client.Width, height = (int)Math.Round(width / _viewportAspect);
        if (height > client.Height)
        {
            height = client.Height;
            width = (int)Math.Round(height * _viewportAspect);
        }
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        Viewport = new Rectangle((client.Width - width) / 2, (client.Height - height) / 2, width, height);
        ContentSurface.Bounds = Viewport;
        VideoHost.Bounds = Viewport;
        if (_overlayCard != null) _overlayCard.Bounds = ClientRectangle;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var background = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(background, ClientRectangle);
    }
}
