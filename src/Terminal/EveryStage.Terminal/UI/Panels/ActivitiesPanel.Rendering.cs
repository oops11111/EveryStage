using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI.Panels;

public sealed partial class ActivitiesPanel
{
    private void DrawActivityNode(object? sender, DrawTreeNodeEventArgs e)
    {
        e.DrawDefault = false;
        if (e.Node is null) return;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var row = e.Bounds;
        row = new Rectangle(8, row.Top + 5, Math.Max(40, _tree.ClientSize.Width - 24), row.Height - 10);

        if (e.Node.Tag is Activity activity)
        {
            bool active = e.Node.IsSelected || activity.Files.Any(file => ReferenceEquals(file, _playback?.CurrentFile));
            using var path = RoundedRect(row, 12);
            using var fill = new SolidBrush(active ? ModernUi.SurfaceRaised : ModernUi.Surface);
            using var border = new Pen(active ? ModernUi.Accent : ModernUi.Border, active ? 1.4F : 1F);
            g.FillPath(fill, path);
            g.DrawPath(border, path);

            int numberSize = 22;
            var number = new Rectangle(row.Left + 14, row.Top + 12, numberSize, numberSize);
            using var numberFill = new SolidBrush(active ? ModernUi.Accent : ModernUi.SurfaceRaised);
            g.FillEllipse(numberFill, number);
            // Disposed rather than left to the finalizer: this is a TreeView owner-draw callback, so
            // it runs once per node per repaint. An undisposed Font here leaks a GDI handle every
            // time the tree is hovered, scrolled or reactivated, and Font objects are far too small
            // to create the memory pressure that would trigger a collection - the per-process GDI
            // handle limit is reached long before the finalizer ever runs. On an unattended Terminal
            // that is a slow death rather than a crash anyone sees happen.
            using var numberFont = new Font(ModernUi.UiFontSemibold, 9F);
            TextRenderer.DrawText(g, (e.Node.Index + 1).ToString(), numberFont, number,
                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            using var titleFont = new Font(ModernUi.UiFontSemibold, 10F);
            TextRenderer.DrawText(g, activity.Name, titleFont,
                new Rectangle(number.Right + 12, row.Top + 8, row.Width - 190, 22), ModernUi.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string mode = activity.DefaultPlayMode == PlayMode.SequentialAuto ? "顺序自动" : "手动点选";
            TextRenderer.DrawText(g, $"{activity.Files.Count} 个文件 · {mode}", Font,
                new Rectangle(number.Right + 12, row.Top + 30, row.Width - 190, 18), ModernUi.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (activity.Files.Any(file => ReferenceEquals(file, _playback?.CurrentFile)))
                TextRenderer.DrawText(g, "● 正在播放", Font, new Rectangle(row.Right - 110, row.Top + 14, 96, 22),
                    ModernUi.Success, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, e.Node.IsExpanded ? "⌃" : "⌄", Font,
                new Rectangle(row.Right - 34, row.Top + 12, 24, 24), ModernUi.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        if (e.Node.Tag is MediaFile file)
        {
            bool selected = e.Node.IsSelected || _multiSelectedFileNodes.Contains(e.Node);
            var chip = new Rectangle(row.Left + 28, row.Top + 10, Math.Min(360, row.Width - 56), 42);
            using var chipPath = RoundedRect(chip, 8);
            using var chipFill = new SolidBrush(selected ? ModernUi.Surface : ModernUi.SurfaceRaised);
            using var chipBorder = new Pen(selected ? ModernUi.Accent : ModernUi.Border, selected ? 1.2F : 1F);
            g.FillPath(chipFill, chipPath);
            g.DrawPath(chipBorder, chipPath);

            string badge = file.Kind switch
            {
                MediaKind.Video => "V",
                MediaKind.Audio => "A",
                MediaKind.Image => "I",
                MediaKind.Document => Path.GetExtension(file.SourcePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" : "W",
                _ => "•",
            };
            var badgeRect = new Rectangle(chip.Left + 8, chip.Top + 9, 24, 24);
            using var badgeFill = new SolidBrush(file.Kind switch
            {
                MediaKind.Video => Color.FromArgb(124, 58, 237),
                MediaKind.Audio => Color.FromArgb(20, 160, 130),
                MediaKind.Document => Color.FromArgb(41, 105, 214),
                _ => ModernUi.Accent,
            });
            g.FillEllipse(badgeFill, badgeRect);
            using var badgeFont = new Font(ModernUi.UiFontSemibold, badge.Length > 1 ? 6.5F : 9F);
            TextRenderer.DrawText(g, badge, badgeFont, badgeRect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Path.GetFileName(file.SourcePath), Font,
                new Rectangle(badgeRect.Right + 8, chip.Top + 5, chip.Width - 44, 20), ModernUi.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string meta = file.IsBackgroundAudio ? "背景音频 · 循环" : file.Kind switch
            {
                MediaKind.Image or MediaKind.Document => file.StayDuration.HasValue ? $"停留 {file.StayDuration.Value.TotalSeconds:0}s" : "手动翻页",
                _ => file.OnCompletion == CompletionAction.Loop ? "循环" : "播放",
            };
            // Same per-repaint GDI handle leak as numberFont above.
            using var metaFont = new Font(ModernUi.UiFont, 8F);
            TextRenderer.DrawText(g, meta, metaFont,
                new Rectangle(badgeRect.Right + 8, chip.Top + 23, chip.Width - 44, 14), ModernUi.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
