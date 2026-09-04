using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Beamcast.Codec.Gpu;

/// <summary>
/// One Direct3D 11 device shared by everything in a pipeline (capture, colour conversion,
/// encoder or decoder, presenter), so textures never leave the GPU. The immediate context is
/// used from several threads, so every use goes through <see cref="ContextLock"/>.
/// </summary>
public sealed class GpuDevice : IDisposable
{
    private static int _mfStarted;

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public GpuDevice()
    {
        EnsureMediaFoundation();

        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out ID3D11Device? device);
        if (result.Failure || device is null)
            D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, levels, out device).CheckError();

        Device = device!;
        Context = Device.ImmediateContext;
        Device.QueryInterfaceOrNull<ID3D11Multithread>()?.SetMultithreadProtected(true);

        using (var dxgi = Device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
            try
            {
                WinRtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }

        DeviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        DeviceManager.ResetDevice(Device);
        VideoDevice = Device.QueryInterface<ID3D11VideoDevice>();
        VideoContext = Context.QueryInterface<ID3D11VideoContext>();
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID3D11VideoDevice VideoDevice { get; }
    public ID3D11VideoContext VideoContext { get; }
    public IDirect3DDevice WinRtDevice { get; }
    public IMFDXGIDeviceManager DeviceManager { get; }

    /// <summary>Guards the immediate context (and video context) across threads.</summary>
    public object ContextLock { get; } = new();

    public static void EnsureMediaFoundation()
    {
        if (Interlocked.Exchange(ref _mfStarted, 1) == 0)
            MediaFactory.MFStartup(true).CheckError();
    }

    public ID3D11Texture2D CreateTexture(Format format, int width, int height, BindFlags bind, ResourceOptionFlags misc = ResourceOptionFlags.None)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = bind,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = misc,
        };
        return Device.CreateTexture2D(description);
    }

    public void Dispose()
    {
        SafeTry.Run(() => DeviceManager.Dispose());
        SafeTry.Run(() => VideoContext.Dispose());
        SafeTry.Run(() => VideoDevice.Dispose());
        SafeTry.Run(() => WinRtDevice.Dispose());
        SafeTry.Run(() => Context.Dispose());
        SafeTry.Run(() => Device.Dispose());
    }
}
