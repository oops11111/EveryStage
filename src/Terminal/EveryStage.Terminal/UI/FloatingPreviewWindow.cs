using EveryStage.Terminal.Data;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;

namespace EveryStage.Terminal.UI;

/// <summary>
/// The floating preview window (PLANNING.md §8.3): "仅在扩展屏有输出时出现，无输出时不存在" — shown
/// while <see cref="OutputStateMachine"/> is Active, hidden the rest of the time. Content: LIVE
/// badge, thumbnail preview, filename/kind, and four buttons (上一项/暂停/下一项/切断).
///
/// Kept as one persistent <see cref="Form"/> instance rather than recreated per show — that's what
/// gives "支持拖动记忆位置" (dragged position remembered) for free: <see cref="Control.Location"/>
/// naturally survives a Hide()/Show() cycle without any bookkeeping of our own.
///
/// Refreshes its thumbnail/labels on a short polling timer rather than purely off
/// <see cref="PlaybackEngine.FileStarted"/>: that event fires synchronously the moment a file is
/// accepted, but for image/PDF the actual decoded frame lands slightly later (LoadAsync is
/// fire-and-forget from PlaybackEngine's point of view) — polling sidesteps that race entirely
/// instead of chasing a second "frame ready" event.
/// </summary>
public sealed class FloatingPreviewWindow : Form
{
    private readonly PlaybackEngine _playback;
    private readonly OutputStateMachine _stateMachine;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly Label _liveBadge;
    private readonly PictureBox _thumbnail;
    private readonly Label _fileLabel;
    private readonly Button _previousButton;
    private readonly Button _pauseButton;
    private readonly Button _nextButton;
    private readonly Button _disconnectButton;
    private readonly Button _pinButton;

    public FloatingPreviewWindow(PlaybackEngine playback, OutputStateMachine stateMachine)
    {
        _playback = playback;
        _stateMachine = stateMachine;

        Text = "EveryStage";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40); // only used the very first time this instance is shown.

        _liveBadge = new Label
        {
            Text = "● LIVE",
            ForeColor = Color.Red,
            AutoSize = true,
            Location = new Point(8, 6),
        };

        _thumbnail = new PictureBox
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
            Bounds = new Rectangle(8, 28, 204, 114),
        };

        _fileLabel = new Label
        {
            AutoEllipsis = true,
            Bounds = new Rectangle(8, 146, 204, 32),
        };

        _previousButton = new Button { Text = "◀ 上一项", Bounds = new Rectangle(8, 182, 60, 24) };
        _pauseButton = new Button { Text = "暂停", Bounds = new Rectangle(72, 182, 44, 24) };
        _nextButton = new Button { Text = "下一项 ▶", Bounds = new Rectangle(120, 182, 60, 24) };
        _disconnectButton = new Button { Text = "断", ForeColor = Color.DarkRed, Bounds = new Rectangle(184, 182, 28, 24) };
        _pinButton = new Button { Text = "📌", Bounds = new Rectangle(184, 6, 24, 20) };

        ClientSize = new Size(220, 214);

        Controls.AddRange(new Control[]
        {
            _liveBadge, _thumbnail, _fileLabel,
            _previousButton, _pauseButton, _nextButton, _disconnectButton, _pinButton,
        });

        _previousButton.Click += (_, _) => _playback.PreviousManual();
        _nextButton.Click += (_, _) => _playback.NextManual();
        _disconnectButton.Click += (_, _) => _stateMachine.Disconnect();
        _pauseButton.Click += (_, _) =>
        {
            if (_playback.IsPaused) _playback.Resume(); else _playback.Pause();
            RefreshFromEngine();
        };
        _pinButton.Click += (_, _) =>
        {
            TopMost = !TopMost;
            _pinButton.BackColor = TopMost ? SystemColors.Highlight : SystemColors.Control;
        };

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _refreshTimer.Tick += (_, _) => RefreshFromEngine();

        // "意外关闭后从主面板召回" (PLANNING.md §8.3): the Phase 4 main panel that would offer a
        // recall button doesn't exist yet, so the closest honest behavior today is "the window
        // never actually closes on its own X button, only hides" — clicking X just re-hides it,
        // same as normal auto-hide, rather than disposing an instance nothing could bring back.
        FormClosing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>"投" — PLANNING.md §8.3's "仅在扩展屏有输出时出现".</summary>
    public void ShowForActiveOutput()
    {
        RefreshFromEngine();
        Show();
        _refreshTimer.Start();
    }

    /// <summary>"断" — "无输出时不存在".</summary>
    public void HideForIdleOutput()
    {
        _refreshTimer.Stop();
        Hide();
    }

    private void RefreshFromEngine()
    {
        MediaFile? file = _playback.CurrentFile;
        if (file == null)
        {
            _fileLabel.Text = "(无内容 / 音频类文件预览尚未支持)";
            _thumbnail.Image = null;
            _pauseButton.Enabled = false;
            return;
        }

        _fileLabel.Text = $"{Path.GetFileName(file.SourcePath)}\n[{file.Kind}]";
        _thumbnail.Image = _playback.CurrentThumbnail; // null for video — see PlaybackEngine.CurrentThumbnail.

        // Pause is only meaningful for image/PDF today (see PlaybackEngine.Pause's doc comment).
        _pauseButton.Enabled = file.Kind is MediaKind.Image or MediaKind.Document;
        _pauseButton.Text = _playback.IsPaused ? "继续" : "暂停";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refreshTimer.Dispose();
        base.Dispose(disposing);
    }
}
