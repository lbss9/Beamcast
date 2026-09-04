using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Beamcast.Capture;

/// <summary>
/// The handful of COM calls Windows.Graphics.Capture needs from a desktop app: building a WinRT
/// Direct3D device, creating capture items from HWND/HMONITOR and reaching the DXGI texture behind
/// a captured surface. Interop interfaces are called through their raw vtables so no COM wrapper
/// generation is required.
/// </summary>
internal static unsafe class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private const string CaptureItemClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    public static (ID3D11Device Device, IDirect3DDevice WinRtDevice) CreateDevice()
    {
        var flags = DeviceCreationFlags.BgraSupport;
        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            flags,
            null,
            out ID3D11Device? device
        );
        if (result.Failure || device is null)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, null, out device).CheckError();
        }

        using var dxgi = device!.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
        try
        {
            var winrt = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            return (device, winrt);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    public static GraphicsCaptureItem CreateItem(CaptureSource source)
    {
        var factory = GetCaptureItemInterop();
        try
        {
            var vtable = *(IntPtr**)factory;
            // IGraphicsCaptureItemInterop: 0-2 IUnknown, 3 CreateForWindow, 4 CreateForMonitor.
            var slot = source.Kind == CaptureSourceKind.Window ? 3 : 4;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtable[slot];

            var iid = GraphicsCaptureItemIid;
            IntPtr item;
            Marshal.ThrowExceptionForHR(create(factory, source.Handle, &iid, &item));
            try
            {
                return GraphicsCaptureItem.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static IntPtr GetCaptureItemInterop()
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(CaptureItemClassName, CaptureItemClassName.Length, out var hstring));
        try
        {
            var iid = GraphicsCaptureItemInteropIid;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iid, out var factory));
            return factory;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>Returns the D3D11 texture behind a captured surface. Caller disposes.</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var unknown = MarshalInspectable<object>.FromManaged(surface);
        try
        {
            var accessIid = DxgiInterfaceAccessIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref accessIid, out var access));
            try
            {
                var vtable = *(IntPtr**)access;
                // IDirect3DDxgiInterfaceAccess: 0-2 IUnknown, 3 GetInterface(REFIID, void**).
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtable[3];
                var textureIid = Texture2DIid;
                IntPtr texture;
                Marshal.ThrowExceptionForHR(getInterface(access, &textureIid, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
