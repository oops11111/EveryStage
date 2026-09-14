using EveryStage.Rendering;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace EveryStage.Caster.Capture;

/// <summary>
/// Screen capture via the Desktop Duplication API (PLANNING.md §4.2 "屏幕捕获：Desktop Duplication
/// API (DDA)"). This is new, uncompiled DirectX interop — same risk category as the Phase 0 demo's
/// D3D11/Media Foundation code and written under the same constraint (this sandbox has no Windows
/// SDK/GPU to compile or run against). IDXGIOutputDuplication / OutputDuplicateFrameInformation
/// member names here are a best-effort guess at Vortice.DXGI's surface (it generally mirrors
/// SharpDX.DXGI naming) rather than something verified against the installed package — see this
/// project's README "已知风险" for the specific points to check first on a real build.
///
/// Known DDA behavior this class is built around (documented native Desktop Duplication semantics,
/// not guesses):
/// - <see cref="AcquireNextFrame"/> times out with nothing new when the screen genuinely hasn't
///   changed — that's DDA's normal "no frame yet" signal, not an error to handle specially.
/// - The texture DDA hands back is only valid until <c>ReleaseFrame()</c> is called, so this copies
///   it into a texture the caller owns before releasing — handing out a texture that becomes
///   invalid out from under the caller on the next Acquire would be a much worse bug to leave in
///   uncompiled, unverified code than the extra copy costs in performance.
/// - An acquire can succeed with only the cursor having moved and no new pixel data — see
///   <see cref="CapturedFrame.HasNewImage"/>.
/// - DXGI_ERROR_ACCESS_LOST is a real, expected failure mode (see <see cref="ScreenCaptureLostException"/>)
///   that permanently invalidates this instance; there is no in-place recovery from it.
/// </summary>
public sealed class ScreenCaptureSource : IDisposable
{
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);

    private readonly D3D11Device _gpu;
    private readonly IDXGIOutputDuplication _duplication;

    public int Width { get; }
    public int Height { get; }

    /// <param name="outputIndex">Which monitor to capture, in DXGI adapter-output enumeration
    /// order. PLANNING.md §12 doesn't specify multi-monitor Caster behavior beyond "全屏捕获" —
    /// this defaults to output 0 (typically the primary display); picking a specific monitor to
    /// cast is a UI concern for later, not something this class decides.</param>
    public ScreenCaptureSource(D3D11Device gpu, int outputIndex = 0)
    {
        _gpu = gpu;

        using var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
        using var output = adapter.GetOutput(outputIndex);
        using var output1 = output.QueryInterface<IDXGIOutput1>();

        _duplication = output1.DuplicateOutput(gpu.Device);

        var desc = _duplication.Description;
        Width = (int)desc.ModeDescription.Width;
        Height = (int)desc.ModeDescription.Height;
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the next frame. Returns null on a normal
    /// timeout (nothing changed) — the common case at low desktop activity, not an error.
    /// </summary>
    /// <exception cref="ScreenCaptureLostException">The duplication interface is permanently gone;
    /// recreate this <see cref="ScreenCaptureSource"/> entirely rather than retrying the call.</exception>
    public CapturedFrame? AcquireNextFrame(int timeoutMs = 500)
    {
        var callResult = _duplication.AcquireNextFrame((uint)timeoutMs, out var frameInfo, out var desktopResource);
        if (callResult == ResultCode.WaitTimeout) return null;

        try
        {
            callResult.CheckError();
        }
        catch (Exception ex) when (ex.HResult == DXGI_ERROR_ACCESS_LOST)
        {
            throw new ScreenCaptureLostException(ex);
        }

        // Only reached on a genuinely successful acquire — DDA's contract is that ReleaseFrame()
        // corresponds 1:1 with a successful AcquireNextFrame, so it belongs in this block, not
        // wrapping the CheckError() call above (a failed acquire has nothing to release).
        try
        {
            using var sourceTexture = desktopResource.QueryInterface<ID3D11Texture2D>();

            var textureDesc = sourceTexture.Description;
            textureDesc.Usage = ResourceUsage.Default;
            textureDesc.BindFlags = BindFlags.ShaderResource;
            textureDesc.CPUAccessFlags = CpuAccessFlags.None;
            textureDesc.MiscFlags = ResourceOptionFlags.None;

            var copy = _gpu.Device.CreateTexture2D(textureDesc);
            _gpu.ImmediateContext.CopyResource(copy, sourceTexture);

            return new CapturedFrame(copy, Width, Height, frameInfo.LastPresentTime != 0);
        }
        finally
        {
            desktopResource.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    public void Dispose() => _duplication.Dispose();
}
