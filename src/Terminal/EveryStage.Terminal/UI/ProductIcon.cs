namespace EveryStage.Terminal.UI;

internal static class ProductIcon
{
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "Assets", "EveryStage.ico");

    public static Icon LoadIcon() => File.Exists(FilePath)
        ? new Icon(FilePath)
        : (Icon)SystemIcons.Application.Clone();

    public static Bitmap LoadBitmap()
    {
        using var icon = LoadIcon();
        return icon.ToBitmap();
    }
}
