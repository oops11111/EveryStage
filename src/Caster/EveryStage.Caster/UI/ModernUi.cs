using System.Drawing.Drawing2D;

namespace EveryStage.Caster.UI;

internal static class ModernUi
{
    public static readonly Color Background = Color.FromArgb(10, 20, 34);
    public static readonly Color Surface = Color.FromArgb(18, 37, 59);
    public static readonly Color SurfaceRaised = Color.FromArgb(25, 48, 76);
    public static readonly Color Border = Color.FromArgb(48, 75, 106);
    public static readonly Color Text = Color.FromArgb(239, 245, 255);
    public static readonly Color Muted = Color.FromArgb(155, 174, 201);
    public static readonly Color Accent = Color.FromArgb(66, 139, 255);
    public static readonly Color Danger = Color.FromArgb(239, 63, 68);
    public static readonly Color Success = Color.FromArgb(42, 210, 128);
    public static readonly Color Warning = Color.FromArgb(255, 181, 71);

    public static void StyleTree(Control root)
    {
        root.Font = new Font("Segoe UI", 10F);
        Apply(root, isRoot: true);
    }

    private static void Apply(Control root, bool isRoot = false)
    {
        if (isRoot) root.BackColor = Background;
        else if (root is GlassPanel) root.BackColor = Color.Transparent;
        else if (root is Panel) root.BackColor = Background;
        if (root is not Label || root.ForeColor == SystemColors.ControlText) root.ForeColor = Text;
        foreach (Control child in root.Controls)
        {
            if (child is not Label || child.ForeColor == SystemColors.ControlText) child.ForeColor = Text;
            child.BackColor = child switch
            {
                Button => Surface,
                ListBox or ComboBox => SurfaceRaised,
                Panel => Background,
                _ => Background,
            };
            if (child is Button button)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Border;
                button.Cursor = Cursors.Hand;
            }
            Apply(child);
        }
    }

    public static void Primary(Button button, bool danger = false)
    {
        button.BackColor = danger ? Danger : Accent;
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI Semibold", 11F);
    }
}

public class GradientForm : Form
{
    public GradientForm()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var background = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(7, 17, 31), Color.FromArgb(18, 39, 65), 35F);
        e.Graphics.FillRectangle(background, ClientRectangle);
        using var glow = new SolidBrush(Color.FromArgb(24, 65, 137, 232));
        e.Graphics.FillEllipse(glow, ClientSize.Width / 3, -ClientSize.Height / 3,
            ClientSize.Width, ClientSize.Height);
    }
}

internal class GlassPanel : Panel
{
    public int CornerRadius { get; set; } = 18;
    public Color GlassTint { get; set; } = Color.FromArgb(195, 14, 32, 53);

    public GlassPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(ClientRectangle, CornerRadius);
        using var fill = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(Math.Min(255, GlassTint.A + 25), GlassTint), GlassTint, 112F);
        e.Graphics.FillPath(fill, path);
        using var border = new Pen(Color.FromArgb(75, 166, 207, 255));
        e.Graphics.DrawPath(border, path);
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        bounds.Width = Math.Max(1, bounds.Width - 1);
        bounds.Height = Math.Max(1, bounds.Height - 1);
        int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
