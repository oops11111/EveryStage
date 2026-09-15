using EveryStage.Rendering;

namespace EveryStage.Terminal.Display;

/// <summary>
/// The single D3D11 device + swap chain bound to <see cref="OverlayWindow.VideoHost"/>'s HWND, kept
/// alive for the Terminal's whole lifetime and shared between whichever of the two things that can
/// put video on that HWND is currently active: local file playback
/// (<c>ContentEngine.VideoContentController</c>) or a live device cast
/// (<c>Receiving.CastReceiver</c>).
///
/// This exists to fix a real bug found during self-review: before this class, each of those two
/// consumers created its own independent <see cref="D3D11Device"/>/<see cref="SwapChainPresenter"/>
/// bound to the very same HWND. Two swap chains for one window is already dubious, but the sharper
/// problem is concurrent presentation — if a local video's decode/present thread were still running
/// when a device cast started (or the other way around), both would call
/// <c>SwapChainPresenter.PresentFrame</c> on their own separate presenter instances against the same
/// window with no coordination at all. Sharing one presenter here doesn't by itself make that safe
/// either — <see cref="SwapChainPresenter"/> isn't documented as safe for concurrent callers, its
/// `_processor`/`_enumerator`/`_outputView` fields get mutated by `EnsureProcessor` — so the actual
/// mutual-exclusion fix is still on the callers: <c>PlaybackEngine.StopForDeviceCast</c> and
/// <c>TerminalApplicationContext</c>'s handling of <c>PlaybackEngine.LocalPlaybackStarting</c> make
/// sure only one of the two is ever actively decoding/presenting at a time. This class only removes
/// the "two swap chains, two D3D devices, one HWND" half of the original problem; see this project's
/// README for the parts that remain caller responsibility rather than something this class enforces
/// on its own.
/// </summary>
public sealed class VideoSurface : IDisposable
{
    public D3D11Device Gpu { get; }
    public SwapChainPresenter Presenter { get; }

    public VideoSurface(IntPtr hostHandle, int width, int height)
    {
        Gpu = new D3D11Device();
        Presenter = new SwapChainPresenter(Gpu, hostHandle, width, height);
    }

    public void Resize(int width, int height) => Presenter.Resize(width, height);

    public void Dispose()
    {
        Presenter.Dispose();
        Gpu.Dispose();
    }
}
