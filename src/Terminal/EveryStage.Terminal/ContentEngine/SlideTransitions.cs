using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>What a slide transition looks like and how long it takes.</summary>
public readonly record struct SlideTransitionSpec(SlideTransitionEffect Effect, SlideTransitionDirection Direction, int Milliseconds)
{
    public static SlideTransitionSpec From(AppSettings settings) =>
        new(settings.SlideTransition, settings.SlideTransitionDirection, Math.Clamp(settings.SlideTransitionMilliseconds, 100, 3000));
}

/// <summary>
/// Draws one frame of a PPT page transition from the previous page (<c>from</c>) to the next (<c>to</c>):
/// 渐隐渐显 (a soft-edged fade that travels in the chosen direction), 翻页效果 (the old page is pushed off
/// like a sheet being turned, with a curled edge and a shadow on the page beneath) and 雨点浸润 (the next
/// page soaks in as growing drops, densest where the transition starts, behind a fully soaked band).
/// GDI+ only; both bitmaps are the destination size.
/// </summary>
public static class SlideTransitions
{
    private const int FadeStrips = 64;
    private const float FadeSoftness = 0.35f;

    /// <summary>Ease in-out: the motion starts and ends gently.</summary>
    public static float Ease(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t < 0.5f ? 2 * t * t : 1 - (float)Math.Pow(-2 * t + 2, 2) / 2;
    }

    public static void Draw(Graphics g, Rectangle r, Bitmap from, Bitmap to, float t, SlideTransitionEffect effect, SlideTransitionDirection direction)
    {
        t = Math.Clamp(t, 0f, 1f);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        switch (effect)
        {
            case SlideTransitionEffect.PageTurn: DrawPageTurn(g, r, from, to, t, direction); break;
            case SlideTransitionEffect.RainSoak: DrawRain(g, r, from, to, t, direction); break;
            default: DrawFade(g, r, from, to, t, direction); break;
        }
    }

    /// <summary>The point at <paramref name="along"/> (0 = where the transition starts) and
    /// <paramref name="across"/>, both 0..1, inside <paramref name="r"/>.</summary>
    private static PointF Map(Rectangle r, SlideTransitionDirection d, float along, float across) => d switch
    {
        SlideTransitionDirection.TopToBottom => new(r.Left + across * r.Width, r.Top + along * r.Height),
        SlideTransitionDirection.BottomToTop => new(r.Left + across * r.Width, r.Bottom - along * r.Height),
        SlideTransitionDirection.LeftToRight => new(r.Left + along * r.Width, r.Top + across * r.Height),
        _ => new(r.Right - along * r.Width, r.Top + across * r.Height),
    };

    private static bool Vertical(SlideTransitionDirection d) => d is SlideTransitionDirection.TopToBottom or SlideTransitionDirection.BottomToTop;

    /// <summary>The band of <paramref name="r"/> between <paramref name="a0"/> and <paramref name="a1"/> along the direction.</summary>
    private static RectangleF Band(Rectangle r, SlideTransitionDirection d, float a0, float a1)
    {
        var p0 = Map(r, d, a0, 0);
        var p1 = Map(r, d, a1, 1);
        return RectangleF.FromLTRB(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y));
    }

    private static void DrawFade(Graphics g, Rectangle r, Bitmap from, Bitmap to, float t, SlideTransitionDirection d)
    {
        g.DrawImage(from, r);
        if (t <= 0) return;
        if (t >= 1) { g.DrawImage(to, r); return; }
        using var attributes = new ImageAttributes();
        var matrix = new ColorMatrix();
        float edge = t * (1 + FadeSoftness);
        for (int i = 0; i < FadeStrips; i++)
        {
            float a0 = (float)i / FadeStrips, a1 = (float)(i + 1) / FadeStrips;
            float alpha = Math.Clamp((edge - (a0 + a1) / 2) / FadeSoftness, 0f, 1f);
            if (alpha <= 0) break; // later strips are further from the start: all transparent
            var f = Band(r, d, a0, a1);
            // Round the edges (not position and size): neighbouring strips then meet without a seam.
            var band = Rectangle.FromLTRB((int)Math.Round(f.Left), (int)Math.Round(f.Top), (int)Math.Round(f.Right), (int)Math.Round(f.Bottom));
            if (band.Width <= 0 || band.Height <= 0) continue;
            var source = new Rectangle(band.X - r.X, band.Y - r.Y, band.Width, band.Height);
            if (alpha >= 1) { g.DrawImage(to, band, source, GraphicsUnit.Pixel); continue; }
            matrix.Matrix33 = alpha;
            attributes.SetColorMatrix(matrix);
            g.DrawImage(to, band, source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
    }

    private static void DrawPageTurn(Graphics g, Rectangle r, Bitmap from, Bitmap to, float t, SlideTransitionDirection d)
    {
        g.DrawImage(to, r);
        if (t >= 1) return;
        bool vertical = Vertical(d);
        int extent = vertical ? r.Height : r.Width;
        int shift = (int)(t * extent);
        int sign = d is SlideTransitionDirection.TopToBottom or SlideTransitionDirection.LeftToRight ? 1 : -1;
        var page = vertical ? new Rectangle(r.X, r.Y + sign * shift, r.Width, r.Height) : new Rectangle(r.X + sign * shift, r.Y, r.Width, r.Height);

        // Shadow the old page casts on the new one, just behind its trailing edge.
        int shadowWidth = Math.Max(4, extent / 22);
        Rectangle shadow = sign > 0
            ? (vertical ? new Rectangle(r.X, page.Top - shadowWidth, r.Width, shadowWidth) : new Rectangle(page.Left - shadowWidth, r.Y, shadowWidth, r.Height))
            : (vertical ? new Rectangle(r.X, page.Bottom, r.Width, shadowWidth) : new Rectangle(page.Right, r.Y, shadowWidth, r.Height));
        var state = g.Save();
        g.SetClip(r);
        if (shadow.Width > 0 && shadow.Height > 0)
        {
            var dark = Color.FromArgb(150, 0, 0, 0);
            var clear = Color.FromArgb(0, 0, 0, 0);
            float angle = vertical ? (sign > 0 ? 270f : 90f) : (sign > 0 ? 180f : 0f);
            using var shade = new LinearGradientBrush(Rectangle.Inflate(shadow, 1, 1), dark, clear, angle);
            g.FillRectangle(shade, shadow);
        }
        g.DrawImage(from, page);

        // The curled edge: the back of the sheet, widest halfway through the turn.
        int curl = (int)(extent * 0.10f * (float)Math.Sin(Math.PI * t));
        if (curl > 1)
        {
            Rectangle fold = sign > 0
                ? (vertical ? new Rectangle(r.X, page.Top, r.Width, curl) : new Rectangle(page.Left, r.Y, curl, r.Height))
                : (vertical ? new Rectangle(r.X, page.Bottom - curl, r.Width, curl) : new Rectangle(page.Right - curl, r.Y, curl, r.Height));
            float angle = vertical ? (sign > 0 ? 90f : 270f) : (sign > 0 ? 0f : 180f);
            using var paper = new LinearGradientBrush(Rectangle.Inflate(fold, 1, 1), Color.FromArgb(236, 214, 210, 200), Color.FromArgb(250, 252, 250, 244), angle);
            g.FillRectangle(paper, fold);
            using var crease = new Pen(Color.FromArgb(90, 0, 0, 0), 1.5f);
            if (vertical) g.DrawLine(crease, fold.Left, sign > 0 ? fold.Bottom : fold.Top, fold.Right, sign > 0 ? fold.Bottom : fold.Top);
            else g.DrawLine(crease, sign > 0 ? fold.Right : fold.Left, fold.Top, sign > 0 ? fold.Right : fold.Left, fold.Bottom);
        }
        g.Restore(state);
    }

    // Drop field shared by every frame (so the drops grow in place rather than flicker).
    private static readonly (float Along, float Across, float Size)[] Drops = MakeDrops();

    private static (float, float, float)[] MakeDrops()
    {
        var random = new Random(20261029);
        var drops = new (float, float, float)[220];
        for (int i = 0; i < drops.Length; i++)
            drops[i] = ((float)random.NextDouble(), (float)random.NextDouble(), 0.55f + (float)random.NextDouble() * 0.6f);
        return drops;
    }

    private static void DrawRain(Graphics g, Rectangle r, Bitmap from, Bitmap to, float t, SlideTransitionDirection d)
    {
        g.DrawImage(from, r);
        if (t <= 0) return;
        if (t >= 1) { g.DrawImage(to, r); return; }
        float maxRadius = (float)Math.Sqrt((double)r.Width * r.Height) * 0.075f;
        using var path = new GraphicsPath(FillMode.Winding);
        // Fully soaked band behind the drops.
        float band = t * 1.25f - 0.25f;
        if (band > 0) path.AddRectangle(Band(r, d, 0, Math.Min(1, band)));
        // Drops: each starts once the front reaches it and grows to full size.
        float front = t * 1.35f;
        foreach (var (along, across, size) in Drops)
        {
            float grow = Math.Clamp((front - along) / 0.35f, 0f, 1f);
            if (grow <= 0) continue;
            float radius = maxRadius * size * (0.25f + 0.75f * grow) * (0.6f + 0.6f * t);
            var center = Map(r, d, along, across);
            path.AddEllipse(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        }
        var state = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        g.DrawImage(to, r);
        g.Restore(state);
    }
}
