using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace EveryStage.Rendering;

/// <summary>
/// Owns the single D3D11 device shared between the MF decoder (hardware DXVA output textures)
/// and the DXGI swap-chain presenter, plus the IMFDXGIDeviceManager that binds them together.
///
/// This shared-device-manager step is the crux of the zero-copy claim in PLANNING.md §4.2/§4.4:
/// if the decoder and the renderer don't share one device (or don't go through
/// IMFDXGIDeviceManager), Media Foundation falls back to CPU-side copies internally even though
/// the sample "looks" like a texture from the caller's side.
/// </summary>
public sealed class D3D11Device : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext ImmediateContext { get; }
    public IDXGIFactory2 DxgiFactory { get; }
    public IMFDXGIDeviceManager DeviceManager { get; }
    public uint DeviceManagerResetToken { get; }

    public D3D11Device()
    {
        // BgraSupport: required by some MF video-processor / debug overlay paths.
        // VideoSupport: required so the driver exposes DXVA video decode/processing on this device.
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
#if DEBUG
        flags |= DeviceCreationFlags.Debug;
#endif
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            flags,
            new[]
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
            },
            out var device,
            out var context).CheckError();

        Device = device!;
        ImmediateContext = context!;

        // Bug fixed here: this used to be one chained expression —
        // Device.QueryInterface<IDXGIDevice>().GetParent<IDXGIAdapter>().GetParent<IDXGIFactory2>() —
        // which silently leaked the two intermediate COM objects (the IDXGIDevice and IDXGIAdapter
        // instances QueryInterface/GetParent each hand back with their own AddRef) since only the
        // final IDXGIFactory2 was ever kept around to be Dispose()d later. Every other COM call
        // chain of more than one step elsewhere in this codebase (e.g. AudioTakeoverService's
        // enumerator/device pair) already wraps each intermediate step in its own `using` — this
        // constructor's one-liner was the one place that didn't. Matters more here than a one-time
        // leak would: this class is constructed fresh per cast (Caster.Casting.LiveCastSession.Start()
        // makes a new D3D11Device every time casting starts), so every cast start/stop cycle used to
        // leak one more IDXGIDevice + IDXGIAdapter reference that would never be released until
        // process exit.
        //
        // A second, separate bug fixed here (found later, self-review — not the same one as above):
        // that earlier fix only addressed leaking the two INTERMEDIATE COM objects within this one
        // chained expression; it never addressed this constructor's own version of the exact
        // "step one succeeds and gets kept, step two throws, nothing disposes step one" shape this
        // session has since found and fixed in six separate MFStartup-based constructors elsewhere
        // (see this project's READMEs) — an irony worth calling out explicitly, since this is the
        // one class every one of those six depends on for the GPU device they in turn try to keep
        // clean on their own partial-construction failures. Device/ImmediateContext are already
        // assigned to properties by the time this comment's code runs (D3D11CreateDevice above
        // already succeeded); if QueryInterface<ID3D11Multithread>/the DxgiFactory chain/
        // MFCreateDXGIDeviceManager/ResetDevice throws anywhere below, this constructor never
        // finishes, so no D3D11Device instance ever exists for a caller to later Dispose() and
        // release whatever of Device/ImmediateContext/DxgiFactory/the not-yet-assigned local
        // `manager` already succeeded — every one of them would otherwise leak a real GPU/COM
        // resource for the rest of the process's life.
        IMFDXGIDeviceManager? manager = null;
        try
        {
            // Video decode + present happen from different threads (MF work-queue thread vs. the
            // window's render/present loop) so the device's multithread protection must be on.
            using (var multithread = Device.QueryInterface<ID3D11Multithread>())
            {
                multithread.SetMultithreadProtected(true);
            }

            using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
            DxgiFactory = adapter.GetParent<IDXGIFactory2>();

            MediaFactory.MFCreateDXGIDeviceManager(out var resetToken, out manager).CheckError();
            manager!.ResetDevice(Device, resetToken).CheckError();

            DeviceManager = manager;
            DeviceManagerResetToken = resetToken;
        }
        catch
        {
            // Cleanup order deliberately mirrors Dispose()'s own order below, and each step is
            // independently null-safe: DxgiFactory/manager may never have been assigned depending on
            // exactly where above the throw happened, but Device/ImmediateContext are unconditionally
            // already live COM objects by this point (see the comment above), so those two are always
            // disposed here, no null-check needed — same reasoning AacAudioDecoder's own constructor
            // fix (this project's README) already applies to its single `_decoder` field.
            manager?.Dispose();
            DxgiFactory?.Dispose();
            ImmediateContext.Dispose();
            Device.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        DeviceManager.Dispose();
        DxgiFactory.Dispose();
        ImmediateContext.Dispose();
        Device.Dispose();
    }
}
