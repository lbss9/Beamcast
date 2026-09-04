using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace Beamcast.Render;

/// <summary>ISwapChainPanelNative (WinUI 3 flavour) called through its raw vtable.</summary>
internal static unsafe class SwapChainPanelInterop
{
    private static readonly Guid SwapChainPanelNativeIid = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    /// <summary>Attaches (or, with IntPtr.Zero, detaches) a DXGI swap chain. UI thread only.</summary>
    public static void SetSwapChain(SwapChainPanel panel, IntPtr swapChain)
    {
        var unknown = MarshalInspectable<object>.FromManaged(panel);
        try
        {
            var iid = SwapChainPanelNativeIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref iid, out var native));
            try
            {
                var vtable = *(IntPtr**)native;
                // ISwapChainPanelNative: 0-2 IUnknown, 3 SetSwapChain(IDXGISwapChain*).
                var set = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtable[3];
                Marshal.ThrowExceptionForHR(set(native, swapChain));
            }
            finally
            {
                Marshal.Release(native);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
