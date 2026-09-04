using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Beamcast.Codec.Gpu;

/// <summary>A decoded picture living in a texture owned by the decoder. Dispose to give it back.</summary>
public sealed class DecodedTexture : IDisposable
{
    private readonly IMFSample _sample;
    private readonly IMFMediaBuffer _buffer;

    internal DecodedTexture(IMFSample sample, IMFMediaBuffer buffer, ID3D11Texture2D texture, uint subresource, int width, int height)
    {
        _sample = sample;
        _buffer = buffer;
        Texture = texture;
        Subresource = subresource;
        Width = width;
        Height = height;
    }

    public ID3D11Texture2D Texture { get; }
    public uint Subresource { get; }
    public int Width { get; }
    public int Height { get; }

    public void Dispose()
    {
        Texture.Dispose();
        _buffer.Dispose();
        _sample.Dispose();
    }
}

/// <summary>
/// H.264/HEVC decoder through the Microsoft Media Foundation decoder with DXVA: frames come out as
/// NV12 textures on the pipeline's device, ready to be blitted to the swap chain without touching
/// system memory. Synchronous MFT: one call in, zero or more pictures out.
/// </summary>
public sealed class MfVideoDecoder : IDisposable
{
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private static readonly Guid Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid MfLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private readonly GpuDevice _gpu;
    private readonly IMFTransform _transform;
    private readonly CodecApiSetter _codecApi;
    private int _width;
    private int _height;
    private bool _disposed;

    public MfVideoDecoder(GpuDevice gpu, VideoCodec codec, int width, int height)
    {
        _gpu = gpu;
        Codec = codec;
        _width = Math.Max(2, width);
        _height = Math.Max(2, height);

        _transform = MfCodecs.CreateDecoder(codec, out var name)
            ?? throw new NotSupportedException("No decoder for " + codec);
        Name = name;

        var attributes = _transform.Attributes;
        if (attributes is not null)
            SafeTry.Run(() => attributes.Set(MfLowLatency, 1u));

        _codecApi = new CodecApiSetter(_transform.NativePointer);
        _codecApi.SetUInt32(CodecApiGuids.AVLowLatencyMode, 1);
        _codecApi.SetBool(CodecApiGuids.AVLowLatencyMode, true);
        if (codec == VideoCodec.H264)
            _codecApi.SetUInt32(CodecApiGuids.AVDecVideoAccelerationH264, 1);

        // The device manager must be attached before the types are negotiated.
        _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)_gpu.DeviceManager.NativePointer);

        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, MfCodecs.Subtype(codec));
            input.Set(MediaTypeAttributeKeys.FrameSize, Pack(_width, _height));
            input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.MixedInterlaceOrProgressive);
            _transform.SetInputType(0, input, 0);
        }

        SelectNv12Output();
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public VideoCodec Codec { get; }
    public string Name { get; }

    private void SelectNv12Output()
    {
        for (var i = 0; ; i++)
        {
            IMFMediaType candidate;
            try
            {
                candidate = _transform.GetOutputAvailableType(0, i);
            }
            catch (Exception)
            {
                throw new NotSupportedException("The decoder offers no NV12 output.");
            }

            using (candidate)
            {
                if (candidate.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12)
                    continue;
                _transform.SetOutputType(0, candidate, 0);
                var size = candidate.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                _width = (int)(size >> 32);
                _height = (int)(size & 0xFFFFFFFF);
                return;
            }
        }
    }

    /// <summary>
    /// Feeds one access unit (Annex B, one frame) and returns the newest decoded picture, or null
    /// when the decoder needs more data. Older pictures produced in the same call are discarded.
    /// </summary>
    public DecodedTexture? Decode(ReadOnlySpan<byte> accessUnit, long timestampMs)
    {
        if (_disposed || accessUnit.IsEmpty)
            return null;

        using (var buffer = MediaFactory.MFCreateMemoryBuffer(accessUnit.Length))
        using (var sample = MediaFactory.MFCreateSample())
        {
            WriteBuffer(buffer, accessUnit);
            sample.AddBuffer(buffer);
            sample.SampleTime = timestampMs * 10_000;
            _transform.ProcessInput(0, sample, 0);
        }

        DecodedTexture? newest = null;
        while (true)
        {
            var output = new OutputDataBuffer { StreamID = 0 };
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();

            if (result.Code == NeedMoreInput)
                break;
            if (result.Code == StreamChange)
            {
                output.Sample?.Dispose();
                SelectNv12Output();
                continue;
            }
            if (result.Failure)
            {
                output.Sample?.Dispose();
                throw new InvalidOperationException($"Decoder failed: 0x{result.Code:X8}");
            }

            var sample = output.Sample;
            if (sample is null)
                continue;

            var texture = Unwrap(sample);
            if (texture is null)
                continue;
            newest?.Dispose();
            newest = texture;
        }

        return newest;
    }

    private DecodedTexture? Unwrap(IMFSample sample)
    {
        var buffer = sample.GetBufferByIndex(0);
        using var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();
        if (dxgi is null)
        {
            buffer.Dispose();
            sample.Dispose();
            return null;
        }

        var pointer = dxgi.GetResource(Texture2DIid);
        var texture = new ID3D11Texture2D(pointer);
        return new DecodedTexture(sample, buffer, texture, dxgi.SubresourceIndex, _width, _height);
    }

    private static unsafe void WriteBuffer(IMFMediaBuffer buffer, ReadOnlySpan<byte> data)
    {
        buffer.Lock(out var pointer, out _, out _);
        try
        {
            data.CopyTo(new Span<byte>((void*)pointer, data.Length));
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = data.Length;
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        SafeTry.Run(() => _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero));
        SafeTry.Run(() => _transform.Dispose());
    }
}
