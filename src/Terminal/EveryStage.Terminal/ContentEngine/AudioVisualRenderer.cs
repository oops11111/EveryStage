using System.Drawing;
using System.Drawing.Drawing2D;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>
/// Generates the two purely-local placeholder visuals <c>PlaybackEngine</c> shows on the extended
/// display while standalone audio (<c>MediaFile.Kind == Audio</c>) plays —
/// <c>MediaFile.BackgroundAudioVisual</c>'s <c>DefaultBackgroundImage</c> and <c>Waveform</c> cases
/// (<c>Black</c> needs no generated frame at all: <see cref="ContentSurface"/> already clears to
/// black when <see cref="ContentSurface.SetFrame"/> is called with null, see that class's
/// <c>OnPaint</c>).
///
/// Neither of these is a "real" designed visual: this repo has no bundled image assets at all (no
/// Resources/Assets folder exists anywhere in it), so <c>DefaultBackgroundImage</c> is a
/// programmatically drawn placeholder rather than a loaded default image, and <c>Waveform</c> is a
/// single-bar peak level meter rather than a true scrolling waveform — both are honest about being
/// minimal placeholders rather than pretending to be finished product visuals; see this project's
/// README "已知风险" for what a real implementation of either would need.
/// </summary>
internal static class AudioVisualRenderer
{
    public static Bitmap CreateDefaultBackgroundFrame(Size size, string sourcePath)
    {
        int width = Math.Max(1, size.Width);
        int height = Math.Max(1, size.Height);
        var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        using var background = new SolidBrush(Color.FromArgb(255, 24, 24, 28));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.FillRectangle(background, 0, 0, width, height);

        string fileName = Path.GetFileName(sourcePath);
        using var font = new Font("Segoe UI", Math.Max(12f, height / 20f));
        using var textBrush = new SolidBrush(Color.FromArgb(255, 210, 210, 215));
        var textSize = g.MeasureString(fileName, font);
        g.DrawString(fileName, font, textBrush,
            (width - textSize.Width) / 2f, (height - textSize.Height) / 2f);

        return bitmap;
    }

    /// <param name="level">Normalized [0, 1] peak amplitude — see
    /// <c>AudioContentController.LevelChanged</c>.</param>
    public static Bitmap CreateWaveformFrame(Size size, float level)
    {
        int width = Math.Max(1, size.Width);
        int height = Math.Max(1, size.Height);
        var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        using var background = new SolidBrush(Color.Black);
        using var barBrush = new SolidBrush(Color.FromArgb(255, 90, 200, 255));
        g.FillRectangle(background, 0, 0, width, height);

        float clamped = Math.Clamp(level, 0f, 1f);
        int barHeight = (int)(height * 0.6f * clamped);
        int barWidth = Math.Max(1, width / 8);
        int x = (width - barWidth) / 2;
        int y = Math.Max(0, height - barHeight - height / 8);
        g.FillRectangle(barBrush, x, y, barWidth, barHeight);

        return bitmap;
    }
}
