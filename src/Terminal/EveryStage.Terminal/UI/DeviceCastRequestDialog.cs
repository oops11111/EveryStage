namespace EveryStage.Terminal.UI;

public sealed class DeviceCastRequestDialog : Form
{
    private readonly Button _acceptButton;
    private readonly System.Windows.Forms.Timer _timer;
    private int _remainingSeconds = 60;

    public bool Accepted { get; private set; }

    public DeviceCastRequestDialog(string newDeviceName, string currentDeviceName, bool currentOnProgram)
    {
        Text = "设备投屏请求";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 220);
        TopMost = true;

        var title = new Label
        {
            Text = "新的设备请求投屏",
            Bounds = new Rectangle(18, 16, 380, 28),
            ForeColor = ModernUi.Text,
            Font = new Font("Segoe UI Semibold", 13F),
        };
        var body = new Label
        {
            Text = $"{newDeviceName} 请求投屏。\n\n" +
                   $"当前 {currentDeviceName} 已连接（{(currentOnProgram ? "正在大屏输出" : "在设备投屏窗口预览")}）。\n" +
                   $"接受后将断开 {currentDeviceName}；如果它正在大屏上，会先恢复到投屏前的内容。新的设备会进入设备投屏窗口，按“投屏”后才上大屏。",
            Bounds = new Rectangle(18, 52, 384, 96),
            ForeColor = ModernUi.Text,
            Font = new Font("Segoe UI", 9.5F),
        };

        _acceptButton = new Button { Bounds = new Rectangle(214, 172, 92, 30) };
        var declineButton = new Button { Text = "拒绝", Bounds = new Rectangle(314, 172, 88, 30), DialogResult = DialogResult.Cancel };
        ModernUi.StyleButton(_acceptButton, primary: true);
        ModernUi.StyleButton(declineButton);
        AcceptButton = _acceptButton;
        CancelButton = declineButton;

        _acceptButton.Click += (_, _) => { Accepted = true; DialogResult = DialogResult.OK; Close(); };
        Controls.AddRange(new Control[] { title, body, _acceptButton, declineButton });
        ModernUi.StyleDialog(this);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            _remainingSeconds--;
            RefreshAcceptText();
            if (_remainingSeconds <= 0)
            {
                Accepted = false;
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };
        RefreshAcceptText();
        Shown += (_, _) => _timer.Start();
        FormClosed += (_, _) => _timer.Stop();
    }

    private void RefreshAcceptText() => _acceptButton.Text = $"接受 ({_remainingSeconds})";

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
