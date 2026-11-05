using System.Runtime.InteropServices;

namespace EveryStage.Terminal.UI;

/// <summary>
/// The 急停 key (F12 by default, 设置 · 热键). A message-only window owns the global registration
/// (RegisterHotKey, MOD_NOREPEAT; no keyboard hook, no key logging), so it works while another program
/// is in front and while the owner is disabled by a dialog. The same key also stops output inside
/// EveryStage through a message filter — including in this application's own modal dialogs, and as the
/// fallback when Windows refuses the global registration (another program owns the key).
/// </summary>
internal sealed class EmergencyStopHotkey : NativeWindow, IMessageFilter, IDisposable
{
    private const int HotkeyId = 0x4553;
    private readonly Action _stop;
    private readonly Action<string> _failure;
    private Keys _key = Keys.F12;
    private bool _enabled;
    private bool _registered;
    private bool _disposed;
    public bool Registered => _registered;

    /// <summary>True while 设置 · 热键 is recording a key: every key then goes to the recorder, and the
    /// global registration is released so even the 急停 key itself can be recorded.</summary>
    internal static bool Recording
    {
        get => _recording;
        set
        {
            if (_recording == value) return;
            _recording = value;
            RecordingChanged?.Invoke();
        }
    }
    private static bool _recording;
    private static event Action? RecordingChanged;
    public Keys Key => _key;

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
        RecordingChanged += OnRecordingChanged;
    }

    private void OnRecordingChanged()
    {
        if (_disposed) return;
        if (Recording)
        {
            if (_registered) UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }
        else if (_enabled && !_registered && _key != Keys.None)
        {
            _enabled = false; // force Configure to register again
            Configure(true, _key);
        }
    }

    public void Configure(bool enabled) => Configure(enabled, _key);

    /// <summary>Registers <paramref name="key"/> globally when <paramref name="enabled"/>; inside
    /// EveryStage the key stops output either way.</summary>
    public void Configure(bool enabled, Keys key)
    {
        if (_disposed) return;
        if (enabled == _enabled && key == _key && (_registered || !enabled)) return;
        if (_registered) UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
        _enabled = enabled;
        _key = key;
        if (!enabled || key == Keys.None) return;
        uint modifiers = 0x4000; // MOD_NOREPEAT
        if (key.HasFlag(Keys.Alt)) modifiers |= 0x0001;
        if (key.HasFlag(Keys.Control)) modifiers |= 0x0002;
        if (key.HasFlag(Keys.Shift)) modifiers |= 0x0004;
        _registered = RegisterHotKey(Handle, HotkeyId, modifiers, (uint)(key & Keys.KeyCode));
        if (!_registered)
            _failure($"全局急停 {Data.Hotkeys.Format(key)} 注册失败（错误 {Marshal.GetLastWin32Error()}），可能已被其他程序占用。" +
                     "在 EveryStage 窗口内按此键仍可急停；也可在「设置 · 热键」换一个键。");
    }

    public bool PreFilterMessage(ref Message m)
    {
        // WM_KEYDOWN / WM_SYSKEYDOWN inside this application (a registered hotkey arrives as WM_HOTKEY instead).
        if ((m.Msg == 0x100 || m.Msg == 0x104) && _key != Keys.None && !Recording
            && (Keys)m.WParam.ToInt32() == (_key & Keys.KeyCode) && Control.ModifierKeys == (_key & Keys.Modifiers))
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
        RecordingChanged -= OnRecordingChanged;
        _disposed = true;
        Application.RemoveMessageFilter(this);
        DestroyHandle();
    }
}
