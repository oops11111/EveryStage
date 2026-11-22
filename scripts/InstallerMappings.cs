using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
public static class InstallerMappings
{
    [StructLayout(LayoutKind.Sequential)] struct Info { public IntPtr Base, Allocation; public uint InitialProtect; public ushort Partition; public UIntPtr Size; public uint State, Protect, Type; }
    [StructLayout(LayoutKind.Sequential)] struct SystemInfo { public ushort Arch, Reserved; public uint Page; public IntPtr Min, Max, Mask; public uint Count, Type, Granularity; public ushort Level, Revision; }
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern void GetNativeSystemInfo(out SystemInfo info);
    [DllImport("kernel32.dll", SetLastError=true)] static extern UIntPtr VirtualQueryEx(IntPtr handle, IntPtr address, out Info info, UIntPtr size);
    [DllImport("psapi.dll", CharSet=CharSet.Unicode)] static extern uint GetMappedFileNameW(IntPtr handle, IntPtr address, StringBuilder name, uint size);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern uint QueryDosDevice(string drive, StringBuilder target, int size);
    public static string[] Read(int pid)
    {
        // Query mapping metadata only; never read memory contents or attach a debugger.
        IntPtr handle = OpenProcess(0x0400, false, pid);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Mapping query access denied");
        try
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SystemInfo system; GetNativeSystemInfo(out system);
            ulong address = 0, maximum = (ulong)system.Max.ToInt64(); int queries = 0;
            while (address <= maximum)
            {
                if (++queries > 200000) throw new InvalidOperationException("Mapping query limit");
                Info info;
                if (VirtualQueryEx(handle, new IntPtr((long)address), out info, (UIntPtr)Marshal.SizeOf(typeof(Info))) == UIntPtr.Zero)
                    throw new InvalidOperationException("Incomplete mapping query: " + Marshal.GetLastWin32Error());
                if (info.State == 0x1000 && (info.Type == 0x1000000 || info.Type == 0x40000))
                {
                    var name = new StringBuilder(32768);
                    if (GetMappedFileNameW(handle, info.Base, name, (uint)name.Capacity) > 0)
                    {
                        string leaf = Path.GetFileName(name.ToString());
                        if (leaf.Equals("FluidMotion.L1.dll", StringComparison.OrdinalIgnoreCase) || leaf.Equals("fluidmotion.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            bool resolved = false;
                            foreach (string root in Directory.GetLogicalDrives())
                            {
                                string drive = root.Substring(0, 2); var target = new StringBuilder(1024);
                                if (QueryDosDevice(drive, target, target.Capacity) > 0 && name.ToString().StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase))
                                { paths.Add(drive + name.ToString().Substring(target.Length)); resolved = true; break; }
                            }
                            if (!resolved) throw new InvalidOperationException("Unresolved mapped path");
                        }
                    }
                }
                ulong next = unchecked((ulong)info.Base.ToInt64()) + info.Size.ToUInt64();
                if (next <= address) throw new InvalidOperationException("Mapping overflow");
                address = next;
            }
            var result = new string[paths.Count]; paths.CopyTo(result); return result;
        }
        finally { CloseHandle(handle); }
    }
}
