using System.Drawing.Drawing2D;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI.Panels;

public sealed partial class FilesPanel
{
    private const int CardGap = 12;
    private const int NameRowHeight = 20;
    private const int MetaRowHeight = 16;

    // P-06: the card fonts are created once for the app's lifetime instead of per card per
    // paint (DrawMediaCard runs for every visible card on every grid repaint). Deliberately
    // never disposed — they live as long as the process, like ActivityRowView's static fonts.
    private static readonly Font CardBadgeFont = new(ModernUi.UiFont, 8F, FontStyle.Bold);
    private static readonly Font CardNameFont = new(ModernUi.UiFont, 9.5F);
    private static readonly Font CardMetaFont = new(ModernUi.UiFont, 8F);

    private readonly record struct CardGeometry(Rectangle Card, Rectangle Thumb, Rectangle Name, Rectangle Meta, Rectangle More);

    /// <summary>Card geometry shared by painting (<see cref="DrawMediaCard"/>), the ⋯ hit test and
    /// tile sizing, so they cannot drift apart.</summary>
    private static CardGeometry CardLayout(Rectangle tile)
    {
        var card = tile; // the grid keeps the gaps between cards
        var thumb = new Rectangle(card.Left, card.Top, Math.Max(1, card.Width), Math.Max(1, card.Width * 10 / 16));
        // Small cards (小 is 85 px) get tighter padding and a narrower ⋯ so the name keeps some room.
        int pad = card.Width < 120 ? 5 : 9, moreWidth = card.Width < 120 ? 16 : 24;
        var more = new Rectangle(card.Right - moreWidth - pad / 2, thumb.Bottom + 6, moreWidth, NameRowHeight);
        var name = Rectangle.FromLTRB(card.Left + pad, more.Top, more.Left - 2, more.Bottom);
        var meta = new Rectangle(card.Left + pad, name.Bottom, Math.Max(1, card.Width - 2 * pad), MetaRowHeight);
        return new CardGeometry(card, thumb, name, meta, more);
    }

    private static int TileHeightFor(int tileWidth) =>
        CardLayout(new Rectangle(0, 0, tileWidth, 10_000)).Meta.Bottom + 8;

    private static bool SameFile(MediaFile a, MediaFile b) =>
        ReferenceEquals(a, b) || string.Equals(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase);

    private void DrawMediaCard(Graphics g, MediaGrid.Item item, Rectangle bounds)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var layout = CardLayout(bounds);
        bool selected = item.Selected;
        var media = item.File;
        bool inPreview = media != null && _playback is { State: not PlaybackChannelState.Idle, CurrentFile: { } current } && SameFile(media, current);
        bool onProgram = media != null && ProgramFile?.Invoke() is { } programFile && SameFile(media, programFile);

        // Card: surface fill; the border says where the file is — 大屏 (danger), 预览 (accent),
        // selected (accent, thinner), otherwise a faint line.
        var cardRect = layout.Card;
        cardRect.Width -= 1;
        cardRect.Height -= 1;
        bool flashing = string.Equals(media?.SourcePath, _flashPath, StringComparison.OrdinalIgnoreCase) && _flashPath != null;
        Color borderColor = onProgram || flashing ? ModernUi.Palette.ProgramBorder : inPreview ? ModernUi.Palette.Preview : selected ? ModernUi.Accent : ModernUi.Palette.IsHighContrast ? ModernUi.Border : Blend(ModernUi.Surface, ModernUi.Border, 0.45);
        float borderWidth = flashing ? 3F : onProgram || inPreview ? 2F : selected ? 1.6F : 1F;
        using (var path = RoundedCard(cardRect, 9))
        {
            using (var fill = new SolidBrush(selected && !ModernUi.Palette.IsHighContrast ? Blend(ModernUi.Surface, ModernUi.Accent, 0.10) : ModernUi.Surface))
                g.FillPath(fill, path);

            var state = g.Save();
            g.SetClip(path);
            var imageRect = layout.Thumb;
            using (var imgBg = new SolidBrush(ModernUi.Palette.IsHighContrast ? SystemColors.Window : Color.FromArgb(20, 37, 58)))
                g.FillRectangle(imgBg, imageRect);
            if (_thumbnailByFileId.TryGetValue(item.File.Id.ToString(), out var image))
            {
                ThumbnailDrawing.DrawContained(g, image, imageRect);
            }
            g.Restore(state);

            using var border = new Pen(borderColor, borderWidth);
            if (ModernUi.Palette.IsHighContrast && inPreview && !onProgram && !flashing) border.DashStyle = DashStyle.Dash;
            g.DrawPath(border, path);
        }

        var thumb = layout.Thumb;
        // Video: a translucent dark play disc in the middle of the thumbnail.
        if (media is { Kind: MediaKind.Video })
        {
            int d = Math.Min(30, thumb.Height - 8);
            var playCircle = new Rectangle(thumb.Left + (thumb.Width - d) / 2, thumb.Top + (thumb.Height - d) / 2, d, d);
            using var playBrush = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
            g.FillEllipse(playBrush, playCircle);
            using var triBrush = new SolidBrush(Color.White);
            float cx = playCircle.Left + d / 2f + 1, cy = playCircle.Top + d / 2f, s = d * 0.2f;
            g.FillPolygon(triBrush, new[] { new PointF(cx - s, cy - s * 1.2f), new PointF(cx - s, cy + s * 1.2f), new PointF(cx + s * 1.3f, cy) });
        }

        var badgeFont = CardBadgeFont;
        // Type chip (JPG / MP4 / PPT / PDF / DOC …) bottom-left; 大屏 / 预览 tag top-left.
        if (media != null)
        {
            var (badgeText, badgeColor) = FileTypeBadge.For(media);
            if (ModernUi.Palette.IsHighContrast) badgeColor = SystemColors.WindowText;
            DrawChip(g, badgeText, badgeFont, new Point(thumb.Left + S(6), thumb.Bottom - S(6) - ChipHeight(badgeFont)), badgeColor, ModernUi.Palette.IsHighContrast ? SystemColors.Window : Color.White);
        }
        if (onProgram) DrawChip(g, "大屏", badgeFont, new Point(thumb.Left + S(6), thumb.Top + S(6)), ModernUi.Palette.DangerFill, ModernUi.Palette.DangerFillForeground);
        else if (inPreview) DrawChip(g, "预览", badgeFont, new Point(thumb.Left + S(6), thumb.Top + S(6)), ModernUi.Palette.Preview, ModernUi.Palette.PreviewForeground);
        if (item.Unavailable)
        {
            const string unavailable = "不可用";
            int width = TextRenderer.MeasureText(unavailable, badgeFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(10);
            DrawChip(g, unavailable, badgeFont, new Point(thumb.Right - width - S(6), thumb.Top + S(6)),
                ModernUi.Palette.IsHighContrast ? SystemColors.Highlight : ModernUi.Palette.DangerFill,
                ModernUi.Palette.IsHighContrast ? SystemColors.HighlightText : ModernUi.Palette.DangerFillForeground);
        }

        if (!item.Unavailable && CardPlaybackProgress(media, onProgram ? ProgramPlayback?.Invoke() : inPreview ? _playback : null) is { } progress)
        {
            Color color = ModernUi.Palette.IsHighContrast ? SystemColors.Highlight : onProgram ? ModernUi.Palette.DangerFill : ModernUi.Palette.Preview;
            Color ink = ModernUi.Palette.IsHighContrast ? SystemColors.HighlightText : onProgram ? ModernUi.Palette.DangerFillForeground : ModernUi.Palette.PreviewForeground;
            if (progress.PageLabel is { } page)
            {
                int width = TextRenderer.MeasureText(page, badgeFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(10);
                DrawChip(g, page, badgeFont, new Point(thumb.Right - width - S(6), thumb.Bottom - S(6) - ChipHeight(badgeFont)), color, ink);
            }
            if (progress.Fraction is { } fraction)
            {
                var track = new Rectangle(thumb.Left + 1, thumb.Bottom - 4, Math.Max(1, thumb.Width - 2), 3);
                using (var background = new SolidBrush(ModernUi.Palette.IsHighContrast ? SystemColors.Window : Color.FromArgb(160, 0, 0, 0))) g.FillRectangle(background, track);
                using var foreground = new SolidBrush(color);
                g.FillRectangle(foreground, track.Left, track.Top, (int)Math.Round(track.Width * fraction), track.Height);
            }
        }

        // Name (with ⋯ on the same row) and the meta line (kind · size).
        string name = Path.GetFileName(item.File.SourcePath);
        TextRenderer.DrawText(g, name, CardNameFont, layout.Name, ModernUi.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        if (item.Meta.Length > 0)
            TextRenderer.DrawText(g, item.Meta, CardMetaFont, layout.Meta,
                item.Unavailable && !ModernUi.Palette.IsHighContrast ? ModernUi.Palette.Danger : ModernUi.Muted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);

        using (var dots = new SolidBrush(ModernUi.Muted))
        {
            int dy = layout.More.Top + layout.More.Height / 2;
            for (int i = -1; i <= 1; i++)
                g.FillEllipse(dots, layout.More.Left + layout.More.Width / 2 + i * 5 - 1.5f, dy - 1.5f, 3.2f, 3.2f);
        }
    }

    /// <summary>Badge height from the font, not a fixed 17 px: the font scales with DPI, so a fixed
    /// height cut the badge text off at 125–150 %.</summary>
    private int S(int value) => LogicalToDeviceUnits(value);

    private static int ChipHeight(Font font) => Math.Max(17, TextRenderer.MeasureText("大屏", font, Size.Empty, TextFormatFlags.NoPadding).Height + 5);

    private static void DrawChip(Graphics g, string text, Font font, Point topLeft, Color fill, Color fore)
    {
        var size = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding);
        int height = ChipHeight(font);
        var chip = new Rectangle(topLeft.X, topLeft.Y, size.Width + height * 10 / 17, height);
        using (var path = RoundedCard(chip, Math.Max(4, height / 4)))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        TextRenderer.DrawText(g, text, font, chip, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
    internal readonly record struct CardProgress(double? Fraction, string? PageLabel);

    internal static CardProgress? CardPlaybackProgress(MediaFile? file, PlaybackEngine? engine)
    {
        if (file == null || engine is not { State: PlaybackChannelState.Playing or PlaybackChannelState.Paused, CurrentFile: { } current }
            || !SameFile(file, current)) return null;
        if (engine.DocumentPageInfo is { } page && page.CurrentPage >= 1 && page.CurrentPage <= page.PageCount)
            return new CardProgress(null, $"{page.CurrentPage}/{page.PageCount}");
        if (current.Kind is MediaKind.Video or MediaKind.Audio && engine.Duration is { Ticks: > 0 } duration)
            return new CardProgress(Math.Clamp((double)engine.Position.Ticks / duration.Ticks, 0, 1), null);
        return null;
    }

    /// <summary>The card's muted second line: 「图片 · 4.2 MB」.</summary>
    private static string CardMeta(MediaFile file)
    {
        try
        {
            var info = new FileInfo(file.SourcePath);
            return info.Exists ? $"{KindLabel(file.Kind)} · {FormatBytes(info.Length)}" : $"{KindLabel(file.Kind)} · 文件不可用";
        }
        catch (Exception) { return KindLabel(file.Kind); }
    }

    private static string CardToolTip(MediaFile file)
    {
        string name = Path.GetFileName(file.SourcePath);
        string tip;
        try
        {
            var info = new FileInfo(file.SourcePath);
            tip = info.Exists ? $"{name}\n{info.LastWriteTime:yyyy/MM/dd}    {FormatBytes(info.Length)}" : $"{name}\n文件不可用";
        }
        catch (Exception) { tip = $"{name}\n{KindLabel(file.Kind)}"; }
        // 可能需要转码只放进 ToolTip，不占卡片的元信息行——那一行已经满了，播放前的提醒靠悬停和导入汇总即可。
        return file.NeedsTranscode ? $"{tip}\n可能需要转码：{TranscodeReason(file)}" : tip;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount),
        (int)(from.B + (to.B - from.B) * amount));

    private static System.Drawing.Drawing2D.GraphicsPath RoundedCard(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.0} MB",
        >= 1024L => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B",
    };

    private static string KindLabel(MediaKind kind) => kind switch
    {
        MediaKind.Image => "图片",
        MediaKind.Video => "视频",
        MediaKind.Document => "文档",
        MediaKind.Audio => "音频",
        _ => "文件",
    };

    /// <summary>A clean 240×150 gradient tile for non-image kinds (and the missing-file case). The
    /// colored type badge is drawn separately over the tile by <see cref="DrawMediaCard"/>; only the
    /// missing-file case passes a caption so the operator can see why a tile is blank.</summary>
    private static Bitmap BuildKindTile(string? caption, Color top, Color bottom)
    {
        var bitmap = new Bitmap(240, 150);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 240, 150), top, bottom, 60F))
            graphics.FillRectangle(bg, 0, 0, 240, 150);
        if (caption != null)
        {
            using var captionBrush = new SolidBrush(Color.FromArgb(210, 225, 240));
            using var captionFont = new Font(ModernUi.UiFont, 11F);
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(caption, captionFont, captionBrush, new RectangleF(0, 0, 240, 150), centered);
        }
        return bitmap;
    }
}
