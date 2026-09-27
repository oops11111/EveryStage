using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace EveryStage.Terminal.UI;

/// <summary>
/// Branded idle splash for the local-preview surface (<see cref="SharedPreviewDock"/>), drawn when
/// there is no live content thumbnail to show. Mirrors the design prototype's preview hero
/// (design/terminal_ui_prototype.html: <c>.screen .frame</c>) so the running app matches the
/// reference mockup's "本地预览 / 播控窗口" instead of a flat dark rectangle.
///
/// Produced as a 16:9 <see cref="Bitmap"/> and handed to the existing <see cref="PictureBox"/> whose
/// <see cref="PictureBoxSizeMode.Zoom"/> already letterboxes it against a black background — the same
/// black-bars-around-a-centered-frame the prototype draws — so no new custom-painted control is
/// introduced here (this repo deliberately avoids UI whose layout can't be verified without a real
/// Windows build; see <c>FloatingPreviewWindow._volumeDownButton</c>'s doc comment). All colours are
/// sampled from the prototype's pixel-locked CSS variables.
/// </summary>
internal static class PreviewPlaceholder
{
    private static readonly Color SkyTop = Color.FromArgb(0xD6, 0xE5, 0xF2);
    private static readonly Color SkyMid = Color.FromArgb(0xBC, 0xD3, 0xE6);
    private static readonly Color SkyBot = Color.FromArgb(0xA4, 0xBF, 0xD8);
    private static readonly Color BackTop = Color.FromArgb(0xEF, 0xF5, 0xFB);
    private static readonly Color BackMid = Color.FromArgb(0xC4, 0xD3, 0xE2);
    private static readonly Color BackBot = Color.FromArgb(0x93, 0xAA, 0xBF);
    private static readonly Color FrontTop = Color.FromArgb(153, 0x52, 0x6C, 0x8C);
    private static readonly Color FrontBot = Color.FromArgb(224, 0x30, 0x4D, 0x6C);
    private static readonly Color BrandNavy = Color.FromArgb(0x1A, 0x3A, 0x5C);
    private static readonly Color TitleNavy = Color.FromArgb(0x12, 0x32, 0x4F);
    private static readonly Color SubNavy = Color.FromArgb(0x34, 0x5D, 0x80);

    public static Bitmap Create(int w, int h)
    {
        var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        Draw(g, new Rectangle(0, 0, bmp.Width, bmp.Height));
        return bmp;
    }

    public static void Draw(Graphics g, Rectangle r)
    {
        // Sky (vertical 3-stop gradient).
        using (var sky = new LinearGradientBrush(r, SkyTop, SkyBot, LinearGradientMode.Vertical))
        {
            sky.InterpolationColors = new ColorBlend(3)
            {
                Colors = new[] { SkyTop, SkyMid, SkyBot },
                Positions = new[] { 0f, 0.48f, 1f },
            };
            g.FillRectangle(sky, r);
        }

        // Warm sunset glow, bottom-right.
        float gr = r.Width * 0.85f;
        var glowRect = new RectangleF(r.Right - gr, r.Bottom - gr * 0.72f, gr * 1.35f, gr * 1.45f);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(glowRect);
            using var pgb = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(135, 255, 201, 150),
                SurroundColors = new[] { Color.FromArgb(0, 255, 176, 122) },
                CenterPoint = new PointF(r.Right - r.Width * 0.10f, r.Bottom - r.Height * 0.08f),
            };
            var saved = g.Clip;
            g.SetClip(r);
            g.FillPath(pgb, path);
            g.Clip = saved;
        }

        // Mountain ranges — y fractions mirror the prototype clip-paths, measured within each band.
        DrawRange(g, r, 0.58f,
            new[] { (0f, 0.56f), (0.12f, 0.27f), (0.24f, 0.50f), (0.38f, 0.13f), (0.50f, 0.45f),
                    (0.60f, 0.31f), (0.68f, 0.53f), (0.80f, 0.68f), (1f, 0.84f) },
            BackTop, BackMid, BackBot);
        DrawRange(g, r, 0.42f,
            new[] { (0f, 0.72f), (0.16f, 0.47f), (0.30f, 0.68f), (0.46f, 0.41f), (0.58f, 0.63f),
                    (0.70f, 0.71f), (0.84f, 0.85f), (1f, 0.93f) },
            FrontTop, FrontTop, FrontBot);

        // Text.
        const string family = "Microsoft YaHei";
        using var esFont = new Font(family, r.Height * 0.072f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var titleFont = new Font(family, r.Height * 0.102f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subFont = new Font(family, r.Height * 0.046f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var esBrush = new SolidBrush(BrandNavy);
        using var titleBrush = new SolidBrush(TitleNavy);
        using var subBrush = new SolidBrush(SubNavy);

        g.DrawString("EveryStage", esFont, esBrush, r.Left + r.Width * 0.085f, r.Top + r.Height * 0.10f);

        float tx = r.Left + r.Width * 0.545f;
        float lh = titleFont.GetHeight(g);
        float blockH = lh * 2 + r.Height * 0.03f + subFont.GetHeight(g);
        float ty = r.Top + (r.Height - blockH) / 2f;
        g.DrawString("让每个阶段", titleFont, titleBrush, tx, ty);
        g.DrawString("都精彩呈现", titleFont, titleBrush, tx, ty + lh);
        g.DrawString("专业 · 高效 · 连接无限可能", subFont, subBrush, tx + 2, ty + lh * 2 + r.Height * 0.03f);
    }

    private static void DrawRange(Graphics g, Rectangle r, float bandFrac, (float x, float y)[] pts,
        Color top, Color mid, Color bot)
    {
        float bandTop = r.Bottom - r.Height * bandFrac;
        float bandH = r.Height * bandFrac;
        var poly = new List<PointF>();
        foreach (var (xf, yf) in pts)
            poly.Add(new PointF(r.Left + r.Width * xf, bandTop + bandH * yf));
        poly.Add(new PointF(r.Right, r.Bottom));
        poly.Add(new PointF(r.Left, r.Bottom));

        using var path = new GraphicsPath();
        path.AddPolygon(poly.ToArray());
        var bandRect = new RectangleF(r.Left, bandTop, Math.Max(1, r.Width), Math.Max(1, bandH));
        using var brush = new LinearGradientBrush(bandRect, top, bot, LinearGradientMode.Vertical);
        brush.InterpolationColors = new ColorBlend(3)
        {
            Colors = new[] { top, mid, bot },
            Positions = new[] { 0f, 0.42f, 1f },
        };
        g.FillPath(brush, path);
    }
}
