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

    /// <summary>A cover image (aspect-fitted in the upper area) or a document glyph when there is none,
    /// with a title and a caption line underneath — the Preview card for content that can only really
    /// open on the extended display (Office documents in WPS).</summary>
    public static Bitmap CreateCoverCard(Size size, Image? cover, string title, string caption)
    {
        int width = Math.Max(1, size.Width);
        int height = Math.Max(1, size.Height);
        var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var background = new SolidBrush(Color.FromArgb(255, 24, 24, 28)))
            g.FillRectangle(background, 0, 0, width, height);

        int textBand = Math.Max(48, height / 4);
        var coverArea = new Rectangle(width / 12, height / 12, width - width / 6, Math.Max(1, height - textBand - height / 12 - 8));
        if (cover != null && cover.Width > 0 && cover.Height > 0)
        {
            float scale = Math.Min(coverArea.Width / (float)cover.Width, coverArea.Height / (float)cover.Height);
            int w = Math.Max(1, (int)(cover.Width * scale)), h = Math.Max(1, (int)(cover.Height * scale));
            g.DrawImage(cover, coverArea.X + (coverArea.Width - w) / 2, coverArea.Y + (coverArea.Height - h) / 2, w, h);
        }
        else
        {
            int glyph = Math.Min(coverArea.Width, coverArea.Height) / 2;
            var page = new Rectangle(coverArea.X + (coverArea.Width - glyph * 3 / 4) / 2, coverArea.Y + (coverArea.Height - glyph) / 2, glyph * 3 / 4, glyph);
            using var pageBrush = new SolidBrush(Color.FromArgb(255, 60, 66, 80));
            g.FillRectangle(pageBrush, page);
        }

        using var titleFont = new Font("Segoe UI Semibold", Math.Max(10f, height / 22f));
        using var captionFont = new Font("Segoe UI", Math.Max(8.5f, height / 30f));
        using var titleBrush = new SolidBrush(Color.FromArgb(255, 225, 228, 235));
        using var captionBrush = new SolidBrush(Color.FromArgb(255, 150, 160, 178));
        using var centered = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        float titleY = height - textBand + 4;
        g.DrawString(title, titleFont, titleBrush, new RectangleF(8, titleY, width - 16, titleFont.GetHeight(g) + 4), centered);
        g.DrawString(caption, captionFont, captionBrush, new RectangleF(8, titleY + titleFont.GetHeight(g) + 6, width - 16, captionFont.GetHeight(g) + 4), centered);
        return bitmap;
    }
}
