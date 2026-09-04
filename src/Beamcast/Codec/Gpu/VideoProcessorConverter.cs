using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice;
using Vortice.Mathematics;

namespace Beamcast.Codec.Gpu;

/// <summary>
/// GPU colour conversion and scaling through the D3D11 video processor (the same fixed-function
/// block video players use). Handles BGRA→NV12 for the encoder and NV12→BGRA for display,
/// including letterboxing into a destination rectangle. Not thread-safe: callers hold
/// <see cref="GpuDevice.ContextLock"/>.
/// </summary>
public sealed class VideoProcessorConverter : IDisposable
{
    private readonly GpuDevice _gpu;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private (int InW, int InH, int OutW, int OutH) _shape;
    private readonly Dictionary<(IntPtr, uint), ID3D11VideoProcessorInputView> _inputViews = new();
    private readonly Dictionary<IntPtr, ID3D11VideoProcessorOutputView> _outputViews = new();
    private bool _inputIsYuv;
    private bool _outputIsYuv;

    public VideoProcessorConverter(GpuDevice gpu)
    {
        _gpu = gpu;
    }

    /// <summary>
    /// Converts <paramref name="source"/> into <paramref name="destination"/>. The whole source is
    /// scaled into <paramref name="destRect"/> (or the whole destination when null).
    /// </summary>
    public void Convert(
        ID3D11Texture2D source,
        uint sourceSubresource,
        int sourceWidth,
        int sourceHeight,
        bool sourceIsYuv,
        ID3D11Texture2D destination,
        int destWidth,
        int destHeight,
        bool destIsYuv,
        RectI? destRect = null
    )
    {
        EnsureProcessor(sourceWidth, sourceHeight, destWidth, destHeight, sourceIsYuv, destIsYuv);

        var input = GetInputView(source, sourceSubresource);
        var output = GetOutputView(destination);
        var context = _gpu.VideoContext;

        context.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new RawRect(0, 0, sourceWidth, sourceHeight));
        var target = destRect ?? new RectI(0, 0, destWidth, destHeight);
        context.VideoProcessorSetStreamDestRect(_processor!, 0, true, new RawRect(target.X, target.Y, target.X + target.Width, target.Y + target.Height));
        context.VideoProcessorSetOutputTargetRect(_processor!, true, new RawRect(0, 0, destWidth, destHeight));

        var streams = new[]
        {
            new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                InputSurface = input,
            },
        };
        context.VideoProcessorBlt(_processor!, output, 0, streams).CheckError();
    }

    private void EnsureProcessor(int inW, int inH, int outW, int outH, bool inYuv, bool outYuv)
    {
        var shape = (inW, inH, outW, outH);
        if (_processor is not null && shape == _shape && inYuv == _inputIsYuv && outYuv == _outputIsYuv)
            return;

        ReleaseViews();
        _processor?.Dispose();
        _enumerator?.Dispose();

        var description = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1),
            InputWidth = (uint)inW,
            InputHeight = (uint)inH,
            OutputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)outW,
            OutputHeight = (uint)outH,
            Usage = VideoUsage.OptimalSpeed,
        };
        _enumerator = _gpu.VideoDevice.CreateVideoProcessorEnumerator(description);
        _processor = _gpu.VideoDevice.CreateVideoProcessor(_enumerator, 0);
        _shape = shape;
        _inputIsYuv = inYuv;
        _outputIsYuv = outYuv;

        var context = _gpu.VideoContext;
        context.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        context.VideoProcessorSetStreamColorSpace(_processor, 0, ColorSpace(inYuv));
        context.VideoProcessorSetOutputColorSpace(_processor, ColorSpace(outYuv));
        context.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        context.VideoProcessorSetOutputBackgroundColor(_processor, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
    }

    /// <summary>BT.709 limited range for YUV, full range for RGB: what every H.264 decoder expects.</summary>
    private static VideoProcessorColorSpace ColorSpace(bool yuv) =>
        new()
        {
            Usage = 0,
            RGB_Range = 0,
            YCbCr_Matrix = 1,
            YCbCr_xvYCC = 0,
            Nominal_Range = yuv ? 1u : 2u,
        };

    private ID3D11VideoProcessorInputView GetInputView(ID3D11Texture2D texture, uint subresource)
    {
        var key = (texture.NativePointer, subresource);
        if (_inputViews.TryGetValue(key, out var view))
            return view;

        var description = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = subresource },
        };
        view = _gpu.VideoDevice.CreateVideoProcessorInputView(texture, _enumerator!, description);
        if (_inputViews.Count > 64)
            ReleaseInputViews();
        _inputViews[key] = view;
        return view;
    }

    private ID3D11VideoProcessorOutputView GetOutputView(ID3D11Texture2D texture)
    {
        if (_outputViews.TryGetValue(texture.NativePointer, out var view))
            return view;

        var description = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        };
        view = _gpu.VideoDevice.CreateVideoProcessorOutputView(texture, _enumerator!, description);
        if (_outputViews.Count > 16)
            ReleaseOutputViews();
        _outputViews[texture.NativePointer] = view;
        return view;
    }

    /// <summary>Drops cached views for a texture that is about to be destroyed (e.g. swap chain resize).</summary>
    public void Forget(ID3D11Texture2D texture)
    {
        if (_outputViews.Remove(texture.NativePointer, out var output))
            output.Dispose();
        foreach (var key in _inputViews.Keys.Where(k => k.Item1 == texture.NativePointer).ToList())
        {
            _inputViews[key].Dispose();
            _inputViews.Remove(key);
        }
    }

    private void ReleaseInputViews()
    {
        foreach (var view in _inputViews.Values)
            view.Dispose();
        _inputViews.Clear();
    }

    private void ReleaseOutputViews()
    {
        foreach (var view in _outputViews.Values)
            view.Dispose();
        _outputViews.Clear();
    }

    private void ReleaseViews()
    {
        ReleaseInputViews();
        ReleaseOutputViews();
    }

    public void Dispose()
    {
        ReleaseViews();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _processor = null;
        _enumerator = null;
    }
}
