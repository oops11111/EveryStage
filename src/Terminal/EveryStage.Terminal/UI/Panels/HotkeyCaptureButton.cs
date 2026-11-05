using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI.Panels;

/// <summary>
/// 设置 · 热键: shows a key (「Ctrl+Enter」); click it, then press the new key (按下录制). While recording,
/// every key — including Esc, Enter, arrows and the 急停 key — goes here instead of to the window's own
/// shortcuts. Click again or leave the button to cancel.
/// </summary>
internal sealed class HotkeyCaptureButton : Button
{
    private Keys _keys;
    private bool _recording;

    /// <summary>A key combination was pressed while recording (not yet validated or saved).</summary>
    public event Action<Keys>? KeyCaptured;

    public HotkeyCaptureButton(Keys keys)
    {
        _keys = keys;
        ModernUi.StyleButton(this);
        Font = new Font(ModernUi.UiFont, 9.5F, FontStyle.Bold);
        Size = new Size(150, 30);
        Margin = Padding.Empty;
        UpdateText();
    }

    public Keys Keys
    {
        get => _keys;
        set { _keys = value; UpdateText(); }
    }

    public bool IsRecording => _recording;

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        SetRecording(!_recording);
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        SetRecording(false);
    }

    private void SetRecording(bool recording)
    {
        _recording = recording;
        EmergencyStopHotkey.Recording = recording;
        UpdateText();
    }

    private void UpdateText() => Text = _recording ? "请按下新按键…" : Hotkeys.Format(_keys);

    protected override bool IsInputKey(Keys keyData) => _recording || base.IsInputKey(keyData);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_recording) return base.ProcessCmdKey(ref msg, keyData);
        var key = keyData & Keys.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu) return true; // wait for the real key
        SetRecording(false);
        KeyCaptured?.Invoke(keyData);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _recording) EmergencyStopHotkey.Recording = false;
        base.Dispose(disposing);
    }
}
