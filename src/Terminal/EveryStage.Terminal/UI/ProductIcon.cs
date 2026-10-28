namespace EveryStage.Terminal.UI;

internal static class ProductIcon
{
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "Assets", "EveryStage.ico");

    public static Icon LoadIcon() => File.Exists(FilePath)
        ? new Icon(FilePath)
        : (Icon)SystemIcons.Application.Clone();

    /// <summary>Picks the .ico frame drawn for <paramref name="size"/> (e.g. the tray's small icon)
    /// instead of letting Windows shrink the default 32 px frame.</summary>
    public static Icon LoadIcon(Size size) => File.Exists(FilePath)
        ? new Icon(FilePath, size)
        : (Icon)SystemIcons.Application.Clone();

    public static Bitmap LoadBitmap()
    {
        using var icon = LoadIcon();
        return icon.ToBitmap();
    }
}
