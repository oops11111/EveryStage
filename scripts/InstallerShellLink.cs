using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
public static class InstallerShellLink
{
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
    }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }
    public static string ReadRawPath(string shortcut)
    {
        string full = Path.GetFullPath(shortcut);
        if (!File.Exists(full)) throw new FileNotFoundException("Shortcut does not exist", full);
        object instance = new ShellLink();
        try
        {
            // STGM_READ only. Do not Resolve, Save, modify the shortcut, or execute its target.
            ((IPersistFile)instance).Load(full, 0);
            var result = new StringBuilder(260);
            int status = ((IShellLinkW)instance).GetPath(result, result.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
            Marshal.ThrowExceptionForHR(status);
            if (status != 0 || result.Length == 0) throw new InvalidDataException("IShellLinkW returned no raw filesystem target");
            return result.ToString();
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }
}
