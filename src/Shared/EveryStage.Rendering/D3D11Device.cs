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

        // Video decode + present happen from different threads (MF work-queue thread vs. the
        // window's render/present loop) so the device's multithread protection must be on.
        using (var multithread = Device.QueryInterface<ID3D11Multithread>())
        {
            multithread.SetMultithreadProtected(true);
        }

        DxgiFactory = Device.QueryInterface<IDXGIDevice>().GetParent<IDXGIAdapter>().GetParent<IDXGIFactory2>();

        MediaFactory.MFCreateDXGIDeviceManager(out var resetToken, out var manager).CheckError();
        manager.ResetDevice(Device, resetToken).CheckError();

        DeviceManager = manager;
        DeviceManagerResetToken = resetToken;
    }

    public void Dispose()
    {
        DeviceManager.Dispose();
        DxgiFactory.Dispose();
        ImmediateContext.Dispose();
        Device.Dispose();
    }
}
