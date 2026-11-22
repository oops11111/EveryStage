using EveryStage.Discovery;
using EveryStage.Terminal.Devices;

namespace EveryStage.Terminal.UI;

/// <summary>
/// PLANNING.md §7's "首次需接收端确认（弹窗/PIN码）". The current pairing v2 flow
/// uses a six-digit comparison code derived by the discovery handshake; this dialog is the
/// operator-side accept/decline step after the code has been verified visually.
/// </summary>
public sealed class PairingConfirmationDialog : Form
{
    private readonly CheckBox _allowCastCheckbox;
    private readonly System.Windows.Forms.Timer? _countdown;
    private readonly DateTimeOffset? _expiresAt;

    public bool Accepted { get; private set; }

    /// <summary>True when the dialog closed itself at the request's expiry, without the
    /// operator answering — the caller reports a timeout rather than a silent decline.</summary>
    public bool Expired { get; private set; }
    public bool AllowCast => _allowCastCheckbox.Checked;
    // 监看 and 信任设备 are not offered (product decision: 只留「允许投放」; nothing implements them, and
    // trust never puts a device straight on the big screen). New pairings store the safe values.
    public bool AllowMonitor => false;
    public TrustMode TrustMode => TrustMode.RequireManualConfirmation;

    /// <param name="expiresAt">When the request stops being answerable (see
    /// <see cref="DiscoveryService.PairingDeadline"/>): the dialog counts down to it and closes
    /// itself at expiry. Null keeps the old layout with no countdown.</param>
    public PairingConfirmationDialog(PairingRequest request, DateTimeOffset? expiresAt = null)
    {
        _expiresAt = expiresAt;
        Text = "投屏设备配对请求";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(380, 256);
        TopMost = true;
        // Its own task-bar button: with the Terminal in the tray (or the Caster on the same PC in front),
        // an owned dialog without one was easy to miss, and an unanswered request expires after 2 minutes.
        ShowInTaskbar = true;
        Shown += (_, _) => { Activate(); BringToFront(); };

        string code = request.VerificationCode.Length == 6
            ? request.VerificationCode.Insert(3, " ")
            : request.VerificationCode;
        var codeLabel = new Label
        {
            Text = code,
            Bounds = new Rectangle(12, 18, 356, 52),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(ModernUi.UiFontSemibold, 28F),
            ForeColor = ModernUi.Accent,
        };
        ModernUi.SetForegroundRole(codeLabel, UiForegroundRole.Accent);
        var codeHint = new Label
        {
            Text = "请核对投屏机上的数字一致后再接受",
            Bounds = new Rectangle(12, 68, 356, 24),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = ModernUi.Text,
        };
        var infoLabel = new Label
        {
            Text = $"设备 \"{request.DeviceName}\" ({request.RemoteAddress}) 请求配对。\n\n" +
                   $"设备ID: {request.DeviceId}",
            Bounds = new Rectangle(12, 100, 356, 58),
        };

        _allowCastCheckbox = new CheckBox
        {
            // OP-11: checked by default — accepting the request is the operator agreeing this
            // device may cast; leaving it unchecked would pair a device that cannot cast and
            // say nothing about why its casts are refused.
            Checked = true,
            Text = "允许被投放（该设备可以向本终端机投屏）",
            Bounds = new Rectangle(12, 190, 356, 24),
        };
        var countdownLabel = new Label
        {
            Text = "",
            Bounds = new Rectangle(12, 166, 356, 20),
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = expiresAt.HasValue,
        };
        ModernUi.SetForegroundRole(countdownLabel, UiForegroundRole.Warning);
        var acceptButton = new Button { Text = "接受", Bounds = new Rectangle(180, 222, 88, 28), DialogResult = DialogResult.OK };
        var declineButton = new Button { Text = "拒绝", Bounds = new Rectangle(280, 222, 88, 28), DialogResult = DialogResult.Cancel };

        AcceptButton = acceptButton;
        CancelButton = declineButton;

        Controls.AddRange(new Control[]
        {
            codeLabel, codeHint, infoLabel, countdownLabel, _allowCastCheckbox, acceptButton, declineButton,
        });
        ModernUi.StyleDialog(this);

        FormClosed += (_, _) => Accepted = DialogResult == DialogResult.OK;
        Disposed += (_, _) => _countdown?.Dispose();

        if (expiresAt is { } deadline)
        {
            UpdateCountdown(countdownLabel, deadline);
            _countdown = new System.Windows.Forms.Timer { Interval = 1000 };
            _countdown.Tick += (_, _) =>
            {
                // The operator has already answered: the form is closing, leave the result alone.
                if (DialogResult != DialogResult.None) { _countdown.Stop(); return; }
                if (DateTimeOffset.Now >= deadline)
                {
                    _countdown.Stop();
                    Expired = true;
                    DialogResult = DialogResult.Cancel;
                    Close();
                    return;
                }
                UpdateCountdown(countdownLabel, deadline);
            };
            _countdown.Start();
        }
    }

    private static void UpdateCountdown(Label label, DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.Now;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        label.Text = $"还剩 {(int)remaining.TotalMinutes}:{remaining.Seconds:D2}，超时未确认将自动关闭";
    }
}
