namespace EveryStage.SharedUi;

/// <summary>
/// Text-font selection shared by Terminal's and Caster's <c>ModernUi</c> (F-27: this logic used to
/// exist as two byte-identical private copies, one in each app — exactly the kind of copy that
/// drifts). The palettes and styling of the two apps still differ and stay in their own ModernUi.
/// </summary>
public static class UiFonts
{
    /// <summary>The UI text font: Windows 11's Segoe UI Variable (Text optical size) when it is installed
    /// AND Windows links it to Microsoft YaHei UI for Chinese (HKLM FontLink\SystemLink); otherwise Segoe
    /// UI. The link check matters: the UI is mostly Chinese, Segoe UI Variable has no CJK glyphs, and a
    /// machine without that link (seen in the wild) renders the Chinese with a worse fallback or as boxes.</summary>
    public static readonly string Text = PickTextFont("Segoe UI Variable Text", "Segoe UI");

    /// <summary>Semibold counterpart of <see cref="Text"/> (headings, labels).</summary>
    public static readonly string TextSemibold = PickTextFont("Segoe UI Variable Text Semibold", "Segoe UI Semibold");

    public static string PickTextFont(string windows11Family, string fallback)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return fallback;
        try
        {
            using var link = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\FontLink\SystemLink");
            if (link?.GetValue(windows11Family) is not string[] fonts
                || !fonts.Any(f => f.Contains("YaHei", StringComparison.OrdinalIgnoreCase)))
                return fallback;
            using var installed = new System.Drawing.Text.InstalledFontCollection();
            return installed.Families.Any(f => string.Equals(f.Name, windows11Family, StringComparison.OrdinalIgnoreCase))
                ? windows11Family : fallback;
        }
        catch (Exception) { return fallback; }
    }
}
