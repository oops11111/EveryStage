using System.Drawing;
using System.Runtime.InteropServices;

namespace EveryStage.Terminal.ContentEngine;

/// <summary>Uses Windows' registered thumbnail providers (including video codecs) without
/// loading a second media stack into the UI process. The returned bitmap owns its own GDI handle.</summary>
public static class ShellThumbnailReader
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindingContext,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(Size size, SIIGBF flags, out IntPtr bitmap);
    }

    [Flags]
    private enum SIIGBF : uint
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
        CropToSquare = 0x20,
        Wide = 0x40,
        Icon = 0x100,
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);

    public static Bitmap? Read(string path, Size target)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        Guid iid = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
        IShellItemImageFactory? item = null;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            item.GetImage(target, SIIGBF.ThumbnailOnly | SIIGBF.BiggerSizeOk, out IntPtr hbitmap);
            if (hbitmap == IntPtr.Zero) return null;
            try
            {
                using var source = Image.FromHbitmap(hbitmap);
                return new Bitmap(source);
            }
            finally { DeleteObject(hbitmap); }
        }
        catch (COMException) { return null; }
        catch (ExternalException) { return null; }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
        }
    }
}
