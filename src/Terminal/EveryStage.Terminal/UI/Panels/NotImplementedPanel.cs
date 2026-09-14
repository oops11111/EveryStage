namespace EveryStage.Terminal.UI.Panels;

/// <summary>Honest placeholder for a §8.2 panel this pass didn't build (活动/设置) — shows what's
/// missing instead of a blank rectangle that looks broken rather than "not built yet".</summary>
public sealed class NotImplementedPanel : UserControl
{
    public NotImplementedPanel(string panelName, string reason)
    {
        Dock = DockStyle.Fill;
        Controls.Add(new Label
        {
            Text = $"{panelName}面板尚未实现。\n\n{reason}",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.DimGray,
        });
    }
}
