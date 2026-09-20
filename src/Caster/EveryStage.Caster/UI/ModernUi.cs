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
        if (isRoot || root is Panel) root.BackColor = Background;
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
