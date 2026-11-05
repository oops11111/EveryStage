namespace EveryStage.Terminal.Data;

/// <summary>The operator shortcuts that can be changed in 设置 · 热键 (the minimum show set). PDF page keys
/// and the audio player's own keys stay fixed and are not listed here; Space is per page (流程 = 下一项)
/// and is only explained, not editable.</summary>
public enum HotkeyAction
{
    /// <summary>急停 from any application (RegisterHotKey); default F12.</summary>
    EmergencyStopGlobal,
    /// <summary>急停 while EveryStage has focus (after the 歌单 drawer is closed); default Esc.</summary>
    EmergencyStopLocal,
    /// <summary>投到大屏 (Take): Preview → big screen; default Ctrl+Enter.</summary>
    Take,
    /// <summary>暂停 / 继续大屏; default P.</summary>
    PauseProgram,
    /// <summary>流程 上一项 (within the current section); default Ctrl+←.</summary>
    PreviousItem,
    /// <summary>流程 下一项 (within the current section); default Ctrl+→.</summary>
    NextItem,
}

/// <summary>Default key codes (System.Windows.Forms.Keys values, kept as numbers so the settings model
/// has no WinForms dependency): F12, Esc, Ctrl+Enter, P, Ctrl+←, Ctrl+→.</summary>
public static class HotkeyDefaults
{
    private const int Control = 0x20000;

    public static readonly IReadOnlyDictionary<HotkeyAction, int> Codes = new Dictionary<HotkeyAction, int>
    {
        [HotkeyAction.EmergencyStopGlobal] = 123,     // F12
        [HotkeyAction.EmergencyStopLocal] = 27,       // Esc
        [HotkeyAction.Take] = Control | 13,           // Ctrl+Enter
        [HotkeyAction.PauseProgram] = 80,             // P
        [HotkeyAction.PreviousItem] = Control | 37,   // Ctrl+←
        [HotkeyAction.NextItem] = Control | 39,       // Ctrl+→
    };
}
