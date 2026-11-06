using System.Runtime.InteropServices;

namespace EveryStage.Terminal.UI;

/// <summary>
/// The 急停 key (F12 by default, 设置 · 热键), working while another program is in front.
/// <list type="number">
/// <item>RegisterHotKey on a message-only window (MOD_NOREPEAT) — the preferred route.</item>
/// <item>When Windows refuses it — F12 is reserved for debuggers and is refused on many machines, or another
/// program owns the key — a low-level keyboard hook that looks at nothing but that one key: no key is
/// recorded or logged, every key (that one included) is passed on unchanged, and the callback only posts
/// the stop to the UI thread.</item>
/// <item>Inside EveryStage the key also stops output through a message filter, including in this
/// application's own modal dialogs (and when neither global route is available).</item>
/// </list>
/// Windows does not deliver keys typed into a program running as administrator to a non-elevated hook, so
/// with an elevated program in front only route 1 works.
/// </summary>
internal sealed class EmergencyStopHotkey : NativeWindow, IMessageFilter, IDisposable
{
    private const int HotkeyId = 0x4553;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    private readonly Action _stop;
    private readonly Action<string> _failure;
    private readonly HookProc _hookProc; // kept alive: Windows calls it through a function pointer
    private readonly SynchronizationContext? _ui;
    private Keys _key = Keys.F12;
    private bool _enabled;
    private bool _registered;
    private IntPtr _hook;
    private bool _keyDown;
    private bool _disposed;

    /// <summary>RegisterHotKey succeeded.</summary>
    public bool Registered => _registered;

    /// <summary>The keyboard-hook fallback is active (registration was refused).</summary>
    public bool HookActive => _hook != IntPtr.Zero;

    /// <summary>The key works globally, by either route.</summary>
    public bool Global => _registered || HookActive;

    public Keys Key => _key;

    /// <summary>True while 设置 · 热键 is recording a key: every key then goes to the recorder, and the
    /// global routes are released so even the 急停 key itself can be recorded.</summary>
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

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode, ScanCode, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    public EmergencyStopHotkey(Action stop, Action<string> failure)
    {
        _stop = stop;
        _failure = failure;
        _hookProc = OnKeyboardHook;
        _ui = SynchronizationContext.Current;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3), Caption = "EveryStage Emergency Stop" });
        Application.AddMessageFilter(this);
        RecordingChanged += OnRecordingChanged;
    }

    private void OnRecordingChanged()
    {
        if (_disposed) return;
        if (Recording) ReleaseGlobal();
        else if (_enabled && !Global && _key != Keys.None)
        {
            _enabled = false; // force Configure to set up again
            Configure(true, _key);
        }
    }

    public void Configure(bool enabled) => Configure(enabled, _key);

    /// <summary>Makes <paramref name="key"/> global when <paramref name="enabled"/>; inside EveryStage the
    /// key stops output either way.</summary>
    public void Configure(bool enabled, Keys key)
    {
        if (_disposed) return;
        if (enabled == _enabled && key == _key && (Global || !enabled)) return;
        ReleaseGlobal();
        _enabled = enabled;
        _key = key;
        if (!enabled || key == Keys.None) return;
        uint modifiers = 0x4000; // MOD_NOREPEAT
        if (key.HasFlag(Keys.Alt)) modifiers |= 0x0001;
        if (key.HasFlag(Keys.Control)) modifiers |= 0x0002;
        if (key.HasFlag(Keys.Shift)) modifiers |= 0x0004;
        _registered = RegisterHotKey(Handle, HotkeyId, modifiers, (uint)(key & Keys.KeyCode));
        if (_registered) return;
        int error = Marshal.GetLastWin32Error();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            _failure($"全局急停 {Data.Hotkeys.Format(key)} 无法在全局生效（注册错误 {error}，键盘监听错误 {Marshal.GetLastWin32Error()}）。" +
                     "在 EveryStage 窗口内按此键仍可急停；也可在「设置 · 热键」换一个键。");
    }

    private void ReleaseGlobal()
    {
        if (_registered) UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _keyDown = false;
    }

    /// <summary>The low-level hook: matches only the 急停 key (with its modifiers), on the key-down edge,
    /// posts the stop and always passes the key on.</summary>
    private IntPtr OnKeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !Recording && _key != Keys.None)
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((Keys)data.VkCode == (_key & Keys.KeyCode))
            {
                int message = wParam.ToInt32();
                bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                if (down && !_keyDown && ModifiersHeld() == (_key & Keys.Modifiers))
                {
                    if (_ui != null) _ui.Post(_ => _stop(), null); else _stop();
                }
                _keyDown = down;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static Keys ModifiersHeld()
    {
        static bool Held(Keys key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;
        var modifiers = Keys.None;
        if (Held(Keys.ControlKey)) modifiers |= Keys.Control;
        if (Held(Keys.ShiftKey)) modifiers |= Keys.Shift;
        if (Held(Keys.Menu)) modifiers |= Keys.Alt;
        return modifiers;
    }

    public bool PreFilterMessage(ref Message m)
    {
        // Inside this application. A registered hotkey arrives as WM_HOTKEY instead; with the hook active,
        // the hook has already seen this key press.
        if ((m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN) && _key != Keys.None && !Recording && !HookActive
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
