namespace EveryStage.Terminal.Data;

public static class Hotkeys
{
    public static readonly IReadOnlyDictionary<HotkeyAction, Keys> Defaults =
        HotkeyDefaults.Codes.ToDictionary(pair => pair.Key, pair => (Keys)pair.Value);

    /// <summary>The key assigned to <paramref name="action"/> (the default when unset).</summary>
    public static Keys HotkeyFor(this AppSettings settings, HotkeyAction action) => (Keys)settings.HotkeyCode(action);

    public static string Label(HotkeyAction action) => action switch
    {
        HotkeyAction.EmergencyStopGlobal => "急停（全局，其他程序在前台也有效）",
        HotkeyAction.EmergencyStopLocal => "急停（本程序内）",
        HotkeyAction.Take => "投到大屏（Take）",
        HotkeyAction.PauseProgram => "播放 / 暂停",
        HotkeyAction.PreviousItem => "上一项（当前环节内）",
        HotkeyAction.NextItem => "下一项（当前环节内）",
        _ => action.ToString(),
    };

    /// <summary>「Ctrl+Enter」, 「F12」, 「Esc」 …</summary>
    public static string Format(Keys keys)
    {
        if (keys == Keys.None) return "未设置";
        var parts = new List<string>();
        if (keys.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keys.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (keys.HasFlag(Keys.Alt)) parts.Add("Alt");
        var key = keys & Keys.KeyCode;
        parts.Add(key switch
        {
            Keys.Escape => "Esc",
            Keys.Enter => "Enter",
            Keys.Left => "←",
            Keys.Right => "→",
            Keys.Up => "↑",
            Keys.Down => "↓",
            Keys.Space => "Space",
            Keys.PageUp => "PgUp",
            Keys.PageDown => "PgDn",
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }

    /// <summary>Keys that cannot be assigned: modifiers alone, and keys Windows or text editing need.</summary>
    public static bool IsAssignable(Keys keys)
    {
        var key = keys & Keys.KeyCode;
        return key is not (Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
            or Keys.Tab or Keys.Back or Keys.Delete or Keys.Apps);
    }

    /// <summary>The action already using <paramref name="keys"/> (all hotkeys share one scope), if any.</summary>
    public static HotkeyAction? ConflictWith(IReadOnlyDictionary<HotkeyAction, Keys> map, HotkeyAction action, Keys keys)
    {
        foreach (var (other, assigned) in map)
            if (other != action && assigned == keys && keys != Keys.None) return other;
        return null;
    }
}
