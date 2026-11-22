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
        public string ClassName; public string Text; public long Style; public bool TextQueried; public bool Visible, Enabled;
    }
    delegate bool EnumCallback(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder value, int capacity);
    [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW", SetLastError=true)]
    static extern IntPtr SendButtonMessageTimeout(IntPtr hwnd, uint message, UIntPtr wparam, IntPtr lparam, uint flags, uint timeout, out UIntPtr result);
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
    public static void ValidateNormalButton(int pid, long dialog, long button, string expectedBody)
    {
        var parent = new IntPtr(dialog); var selected = new IntPtr(button);
        var title = new StringBuilder(512); GetWindowText(parent, title, title.Capacity);
        if (title.ToString() != "开始使用" || !IsWindowVisible(parent) || !IsWindowEnabled(parent))
            throw new InvalidOperationException("Active guide identity/readiness changed");
        var children = Read(pid, dialog); int eligible = 0; bool selectedEligible = false; bool body = false;
        foreach (var child in children)
        {
            if (child.ClassName == "Static" && child.Text != null && child.Text.Replace("\r\n", "\n") == expectedBody) body = true;
            long buttonType = child.Style & 0xf;
            if (child.ClassName == "Button" && child.Visible && child.Enabled && (buttonType == 0 || buttonType == 1) && (child.Text == "OK" || child.Text == "确定"))
            { eligible++; if (child.Hwnd == button) selectedEligible = true; }
        }
        if (!body || eligible != 1 || !selectedEligible || !IsChild(parent, selected))
            throw new InvalidOperationException("Exact source body and unique owned normal OK button required");
    }
    public static void ClickNormalButton(int pid, long dialog, long button, string expectedBody)
    {
        ValidateNormalButton(pid, dialog, button, expectedBody);
        if (GetForegroundWindow() != new IntPtr(dialog)) throw new InvalidOperationException("Exact guide must be active before BM_CLICK");
        UIntPtr result;
        // Standard user button activation only, not dialog close or an application shutdown message.
        if (SendButtonMessageTimeout(new IntPtr(button), 0x00f5, UIntPtr.Zero, IntPtr.Zero, 3, 2000, out result) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Normal OK BM_CLICK did not complete");
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
                children.Add(new Child {Hwnd=child.ToInt64(), ProcessId=childPid, ControlId=GetDlgCtrlID(child), ClassName=name, Text=text, Style=style, TextQueried=textChild, Visible=IsWindowVisible(child), Enabled=IsWindowEnabled(child)});
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
