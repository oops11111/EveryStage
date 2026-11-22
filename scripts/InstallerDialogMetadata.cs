using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class InstallerDialogMetadata
{
    public sealed class Child
    {
        public long Hwnd; public uint ProcessId; public int ControlId;
        public string ClassName; public string Text; public long Style; public bool TextQueried;
    }
    delegate bool EnumCallback(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder value, int capacity);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, UIntPtr capacity, StringBuilder value, uint flags, uint timeout, out UIntPtr result);
    static string ClassName(IntPtr hwnd)
    {
        var value = new StringBuilder(256);
        if (GetClassName(hwnd, value, value.Capacity) == 0) throw new InvalidOperationException("Dialog class query failed");
        return value.ToString();
    }
    public static Child[] Read(int pid, long dialog)
    {
        IntPtr parent = new IntPtr(dialog); uint owner;
        GetWindowThreadProcessId(parent, out owner);
        if (owner != (uint)pid || ClassName(parent) != "#32770") throw new InvalidOperationException("Exact owned native message dialog required");
        var children = new List<Child>(); Exception failure = null;
        EnumCallback callback = delegate(IntPtr child, IntPtr unused)
        {
            try
            {
                if (children.Count >= 128) throw new InvalidOperationException("Dialog child count bound exceeded");
                uint childPid; GetWindowThreadProcessId(child, out childPid);
                if (childPid != (uint)pid || !IsChild(parent, child)) throw new InvalidOperationException("Dialog child ownership mismatch");
                string name = ClassName(child); string text = null; long style = GetWindowLongPtr(child, -16).ToInt64();
                long staticType = style & 0x1f;
                bool textChild = name == "Button" || (name == "Static" && staticType != 3 && staticType != 14 && staticType != 15);
                // WM_GETTEXT is a bounded read-only query, only to Static/Button children of this matched dialog.
                if (textChild)
                {
                    var value = new StringBuilder(8192); UIntPtr length;
                    if (SendMessageTimeout(child, 0x000D, (UIntPtr)value.Capacity, value, 3, 1000, out length) == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned dialog text query failed");
                    if (length.ToUInt64() >= 8191) throw new InvalidOperationException("Dialog text exceeds bound");
                    text = value.ToString();
                }
                children.Add(new Child {Hwnd=child.ToInt64(), ProcessId=childPid, ControlId=GetDlgCtrlID(child), ClassName=name, Text=text, Style=style, TextQueried=textChild});
                return true;
            }
            catch (Exception error) {failure=error; return false;}
        };
        EnumChildWindows(parent, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        if (failure != null) throw failure;
        return children.ToArray();
    }
}
