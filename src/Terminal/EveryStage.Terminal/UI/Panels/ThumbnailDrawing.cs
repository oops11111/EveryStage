using System.Drawing.Drawing2D;

namespace EveryStage.Terminal.UI.Panels;

internal static class ThumbnailDrawing
{
    // Keep the whole image visible, centered inside its available frame.
    internal static void DrawContained(Graphics graphics, Image image, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        float scale = Math.Min((float)bounds.Width / image.Width, (float)bounds.Height / image.Height);
        float width = image.Width * scale, height = image.Height * scale;
        var previous = graphics.InterpolationMode;
        try
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, bounds.Left + (bounds.Width - width) / 2,
                bounds.Top + (bounds.Height - height) / 2, width, height);
        }
        finally { graphics.InterpolationMode = previous; }
    }
}
