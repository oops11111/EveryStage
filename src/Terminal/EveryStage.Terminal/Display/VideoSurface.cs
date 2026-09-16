using EveryStage.Rendering;
using Vortice.Direct3D11;

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
///
/// A second, independent concurrency gap found later (also self-review, not a hypothetical): a
/// display-configuration change can call <see cref="Resize"/> (<c>Program.cs</c>'s
/// <c>HandleDisplaySettingsChanged</c>, on the UI thread) at any time, including while whichever of
/// <c>VideoContentController</c>/<c>CastReceiver</c> is currently active is mid-<c>PresentFrame</c>
/// on its own background thread — and <see cref="SwapChainPresenter.Resize"/> disposes and recreates
/// the exact same swap chain/back buffer/video processor fields <see cref="SwapChainPresenter.PresentFrame"/>
/// reads. <see cref="_lock"/> now serializes <see cref="Resize"/> against <see cref="PresentFrame"/>
/// here, on the actually-shared <see cref="Presenter"/>, rather than in either consumer separately —
/// a lock inside just one consumer (this class used to have one, removed when this one was added)
/// can never cover a call arriving through the other consumer or through <see cref="Resize"/> itself.
/// Both <c>VideoContentController</c> and <c>CastReceiver</c> now call <see cref="PresentFrame"/>
/// here instead of touching <see cref="Presenter"/> directly.
/// </summary>
public sealed class VideoSurface : IDisposable
{
    private readonly object _lock = new();

    public D3D11Device Gpu { get; }
    public SwapChainPresenter Presenter { get; }

    public VideoSurface(IntPtr hostHandle, int width, int height)
    {
        Gpu = new D3D11Device();
        Presenter = new SwapChainPresenter(Gpu, hostHandle, width, height);
    }

    public void Resize(int width, int height)
    {
        lock (_lock) Presenter.Resize(width, height);
    }

    /// <summary>Thread-safe wrapper <see cref="ContentEngine.VideoContentController"/> and
    /// <see cref="Receiving.CastReceiver"/> both call instead of touching <see cref="Presenter"/>
    /// directly — see this class's own doc comment for why calling
    /// <see cref="SwapChainPresenter.PresentFrame"/> without going through this method (or
    /// concurrently with <see cref="Resize"/>) is unsafe.</summary>
    public void PresentFrame(ID3D11Texture2D decodedTexture, int arraySlice, int frameWidth, int frameHeight, bool vsync)
    {
        lock (_lock) Presenter.PresentFrame(decodedTexture, arraySlice, frameWidth, frameHeight, vsync);
    }

    public void Dispose()
    {
        Presenter.Dispose();
        Gpu.Dispose();
    }
}
