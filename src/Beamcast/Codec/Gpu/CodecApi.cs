using System.Runtime.InteropServices;

namespace Beamcast.Codec.Gpu;

/// <summary>
/// Minimal ICodecAPI binding (Vortice does not ship one). Only the vtable slots up to
/// <see cref="SetValue"/> are declared; the interface is never implemented on the managed side.
/// </summary>
[ComImport]
[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig]
    int IsSupported(ref Guid api);

    [PreserveSig]
    int IsModifiable(ref Guid api);

    [PreserveSig]
    int GetParameterRange(ref Guid api, out PropVariant min, out PropVariant max, out PropVariant delta);

    [PreserveSig]
    int GetParameterValues(ref Guid api, out IntPtr values, out uint count);

    [PreserveSig]
    int GetDefaultValue(ref Guid api, out PropVariant value);

    [PreserveSig]
    int GetValue(ref Guid api, out PropVariant value);

    [PreserveSig]
    int SetValue(ref Guid api, ref PropVariant value);
}

/// <summary>PROPVARIANT, limited to the scalar kinds the codec properties use.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort Type;
    private ushort _reserved1;
    private ushort _reserved2;
    private ushort _reserved3;
    public long Value;

    private const ushort VtBool = 11;
    private const ushort VtUInt32 = 19;
    private const ushort VtUInt64 = 21;

    public static PropVariant FromBool(bool value) => new() { Type = VtBool, Value = value ? unchecked((short)-1) : (short)0 };

    public static PropVariant FromUInt32(uint value) => new() { Type = VtUInt32, Value = value };

    public static PropVariant FromUInt64(ulong value) => new() { Type = VtUInt64, Value = unchecked((long)value) };
}

/// <summary>Well-known CODECAPI_* GUIDs used by the hardware encoders and the Microsoft decoders.</summary>
internal static class CodecApiGuids
{
    public static readonly Guid AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid AVEncCommonLowLatency = new("9d3ecd55-89e8-490a-970a-0c9548d5a56e");
    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AVEncCommonMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid AVEncCommonBufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid AVEncVideoForceKeyFrame = new("2c0fbc66-bbc1-497a-b4d9-1c7b3daa3c48");
    public static readonly Guid AVEncVideoMaxNumRefFrame = new("964829ed-94f9-43b4-b74d-ef40944b69a0");
    public static readonly Guid AVEncH264CABACEnable = new("ee6cad62-d305-4248-a50e-e1b255f7caf8");
    public static readonly Guid AVEncNumWorkerThreads = new("b0c8bf60-16f7-4951-a30b-1db1609293d6");
    public static readonly Guid AVDecVideoAccelerationH264 = new("f7db8a2f-4f48-4ee8-ae31-8b6ebe558ae2");

    /// <summary>eAVEncCommonRateControlMode_CBR</summary>
    public const uint RateControlCbr = 0;
}

/// <summary>Helper that applies codec properties and swallows "not supported" from a given vendor.</summary>
internal sealed class CodecApiSetter
{
    private readonly ICodecAPI? _api;

    public CodecApiSetter(IntPtr transformUnknown)
    {
        var iid = typeof(ICodecAPI).GUID;
        if (Marshal.QueryInterface(transformUnknown, ref iid, out var ptr) == 0 && ptr != IntPtr.Zero)
        {
            try
            {
                _api = (ICodecAPI)Marshal.GetObjectForIUnknown(ptr);
            }
            finally
            {
                Marshal.Release(ptr);
            }
        }
    }

    public bool IsAvailable => _api is not null;

    /// <summary>HRESULT of the last SetValue per property, for diagnostics.</summary>
    public Dictionary<Guid, int> Results { get; } = new();

    public bool Set(Guid api, PropVariant value)
    {
        if (_api is null)
            return false;
        try
        {
            var hr = _api.SetValue(ref api, ref value);
            Results[api] = hr;
            return hr == 0;
        }
        catch (Exception ex)
        {
            Results[api] = ex.HResult;
            return false;
        }
    }

    public bool SetBool(Guid api, bool value) => Set(api, PropVariant.FromBool(value));

    public bool SetUInt32(Guid api, uint value) => Set(api, PropVariant.FromUInt32(value));
}
