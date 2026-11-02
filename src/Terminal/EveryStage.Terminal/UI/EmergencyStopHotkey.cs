using System.Runtime.InteropServices;

namespace EveryStage.Terminal.UI;

/// <summary>A message-only window keeps the global shortcut working while the owner is disabled by a dialog.</summary>
internal sealed class EmergencyStopHotkey : NativeWindow, IMessageFilter, IDisposable
{
    private const int HotkeyId = 0x4553;
    private readonly Action _stop;
    private readonly Action<string> _failure;
    private bool _registered;
    private bool _disposed;
    public bool Registered => _registered;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    public EmergencyStopHotkey(Action stop, Action<string> failure)
    {
        _stop = stop;
        _failure = failure;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3), Caption = "EveryStage Emergency Stop" });
        Application.AddMessageFilter(this);
    }

    public void Configure(bool enabled)
    {
        if (_disposed || enabled == _registered) return;
        if (!enabled)
        {
            UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
            return;
        }
        // MOD_SHIFT | MOD_NOREPEAT; no keyboard hook and no key logging.
        _registered = RegisterHotKey(Handle, HotkeyId, 0x0004 | 0x4000, (uint)Keys.Escape);
        if (!_registered)
            _failure($"全局急停 Shift+Esc 注册失败（错误 {Marshal.GetLastWin32Error()}），可能已被其他程序占用。应用内快捷键仍可使用。关闭占用后请关闭再开启此设置重试。");
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg == 0x100 && m.WParam.ToInt32() == (int)Keys.Escape && Control.ModifierKeys == Keys.Shift)
        {
            _stop();
            return true;
        }
        return false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312 && m.WParam.ToInt32() == HotkeyId)
        {
            if (_registered) _stop();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Configure(false);
        _disposed = true;
        Application.RemoveMessageFilter(this);
        DestroyHandle();
    }
}
