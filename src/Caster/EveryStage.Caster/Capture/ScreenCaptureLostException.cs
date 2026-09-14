namespace EveryStage.Caster.Capture;

/// <summary>
/// Thrown when the Desktop Duplication API reports DXGI_ERROR_ACCESS_LOST — the duplication
/// interface has become permanently unusable (a display mode change, a Remote Desktop
/// connect/disconnect, a GPU driver reset, or a secure desktop like a UAC prompt taking over) and
/// must be recreated from scratch (a fresh <see cref="ScreenCaptureSource"/> against the same
/// output). This is a known, expected DDA failure mode — not a bug in this code — and there is no
/// in-place recovery for it.
/// </summary>
public sealed class ScreenCaptureLostException : Exception
{
    public ScreenCaptureLostException(Exception inner)
        : base("Desktop Duplication access was lost (DXGI_ERROR_ACCESS_LOST) — the capture source must be recreated.", inner)
    {
    }
}
