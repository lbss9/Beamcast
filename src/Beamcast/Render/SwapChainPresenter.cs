using System.Numerics;
using Beamcast.Codec.Gpu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Beamcast.Render;

/// <summary>
/// Paints textures straight into a <see cref="SwapChainPanel"/> through a DXGI composition swap
/// chain. Frames are letterboxed by the video processor, so an NV12 decoder output or a BGRA
/// capture texture reaches the screen with one GPU blit and no CPU copy.
/// </summary>
public sealed class SwapChainPresenter : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly VideoProcessorConverter _converter;
    private readonly object _sync = new();
    private SwapChainPanel? _panel;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _uploadTexture;
    private int _width;
    private int _height;
    private int _pendingWidth;
    private int _pendingHeight;
    private float _scaleX = 1;
    private float _scaleY = 1;
    private long _lastPresentTicks;
    private bool _disposed;

    /// <summary>With no frame this recently, a resize repaints black right away instead of waiting for the next frame.</summary>
    private const int IdleAfterMs = 300;

    public SwapChainPresenter(GpuDevice gpu)
    {
        _gpu = gpu;
        _converter = new VideoProcessorConverter(gpu);
    }

    public bool IsAttached => _swapChain is not null;

    /// <summary>Must be called on the UI thread that owns the panel.</summary>
    public void Attach(SwapChainPanel panel)
    {
        Detach();
        _panel = panel;
        _scaleX = (float)Math.Max(0.5, panel.CompositionScaleX);
        _scaleY = (float)Math.Max(0.5, panel.CompositionScaleY);
        var width = Math.Max(8, (int)Math.Round(panel.ActualWidth * _scaleX));
        var height = Math.Max(8, (int)Math.Round(panel.ActualHeight * _scaleY));

        lock (_sync)
        {
            CreateSwapChain(width, height);
        }

        SwapChainPanelInterop.SetSwapChain(panel, _swapChain!.NativePointer);
        ApplyScaleTransform();

        panel.SizeChanged += OnPanelSizeChanged;
        panel.CompositionScaleChanged += OnScaleChanged;
    }

    public void Detach()
    {
        if (_panel is null)
            return;
        _panel.SizeChanged -= OnPanelSizeChanged;
        _panel.CompositionScaleChanged -= OnScaleChanged;
        var panel = _panel;
        _panel = null;
        SafeTry.Run(() => SwapChainPanelInterop.SetSwapChain(panel, IntPtr.Zero));
        lock (_sync)
        {
            _swapChain?.Dispose();
            _swapChain = null;
        }
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Interlocked.Exchange(ref _pendingWidth, Math.Max(8, (int)Math.Round(e.NewSize.Width * _scaleX)));
        Interlocked.Exchange(ref _pendingHeight, Math.Max(8, (int)Math.Round(e.NewSize.Height * _scaleY)));
        RepaintIfIdle();
    }

    private void OnScaleChanged(SwapChainPanel sender, object args)
    {
        _scaleX = (float)Math.Max(0.5, sender.CompositionScaleX);
        _scaleY = (float)Math.Max(0.5, sender.CompositionScaleY);
        ApplyScaleTransform();
        Interlocked.Exchange(ref _pendingWidth, Math.Max(8, (int)Math.Round(sender.ActualWidth * _scaleX)));
        Interlocked.Exchange(ref _pendingHeight, Math.Max(8, (int)Math.Round(sender.ActualHeight * _scaleY)));
        RepaintIfIdle();
    }

    /// <summary>
    /// The swap chain only takes a new size on the next Present, so with nothing playing (or a
    /// paused stream) a resized panel kept showing the old surface at its old size in the corner.
    /// A live stream repaints itself within a frame; otherwise paint black at the new size now.
    /// </summary>
    private void RepaintIfIdle()
    {
        if (Environment.TickCount64 - Volatile.Read(ref _lastPresentTicks) > IdleAfterMs)
            Clear();
    }

    private void ApplyScaleTransform()
    {
        lock (_sync)
        {
            using var swapChain2 = _swapChain?.QueryInterfaceOrNull<IDXGISwapChain2>();
            if (swapChain2 is not null)
                swapChain2.MatrixTransform = new Matrix3x2(1 / _scaleX, 0, 0, 1 / _scaleY, 0, 0);
        }
    }

    private void CreateSwapChain(int width, int height)
    {
        _width = width;
        _height = height;
        using var dxgiDevice = _gpu.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };
        _swapChain = factory.CreateSwapChainForComposition(_gpu.Device, description, null);
    }

    private void ResizeIfNeeded()
    {
        var width = Interlocked.Exchange(ref _pendingWidth, 0);
        var height = Interlocked.Exchange(ref _pendingHeight, 0);
        if (width <= 0 || height <= 0 || _swapChain is null)
            return;
        if (width == _width && height == _height)
            return;

        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        _width = width;
        _height = height;
    }

    /// <summary>Presents a texture (NV12 from the decoder, or BGRA from capture). Any thread.</summary>
    public void Present(ID3D11Texture2D texture, uint subresource, int width, int height, bool isYuv)
    {
        if (_disposed)
            return;

        lock (_sync)
        {
            if (_swapChain is null)
                return;

            lock (_gpu.ContextLock)
            {
                ResizeIfNeeded();
                using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                var target = Letterbox(width, height, _width, _height);
                _converter.Convert(texture, subresource, width, height, isYuv, backBuffer, _width, _height, false, target);
                _converter.Forget(backBuffer);
                _swapChain.Present(0, PresentFlags.None);
                Volatile.Write(ref _lastPresentTicks, Environment.TickCount64);
            }
        }
    }

    /// <summary>Uploads packed BGRA pixels and presents them (used by the CPU VP8 path).</summary>
    public void PresentPixels(byte[] bgra, int width, int height)
    {
        if (_disposed || width <= 0 || height <= 0)
            return;

        lock (_sync)
        {
            if (_swapChain is null)
                return;

            lock (_gpu.ContextLock)
            {
                if (_uploadTexture is null || _uploadTexture.Description.Width != width || _uploadTexture.Description.Height != height)
                {
                    if (_uploadTexture is not null)
                        _converter.Forget(_uploadTexture);
                    _uploadTexture?.Dispose();
                    _uploadTexture = _gpu.CreateTexture(Format.B8G8R8A8_UNorm, width, height, BindFlags.ShaderResource | BindFlags.RenderTarget);
                }
                _gpu.Context.UpdateSubresource(bgra, _uploadTexture, 0, (uint)(width * 4), 0);
            }
        }
        Present(_uploadTexture!, 0, width, height, false);
    }

    /// <summary>Paints black.</summary>
    public void Clear()
    {
        if (_disposed)
            return;
        lock (_sync)
        {
            if (_swapChain is null)
                return;
            lock (_gpu.ContextLock)
            {
                ResizeIfNeeded();
                using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                using var view = _gpu.Device.CreateRenderTargetView(backBuffer);
                _gpu.Context.ClearRenderTargetView(view, new Color4(0, 0, 0, 1));
                _swapChain.Present(0, PresentFlags.None);
            }
        }
    }

    private static RectI Letterbox(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return new RectI(0, 0, targetWidth, targetHeight);
        var scale = Math.Min(targetWidth / (double)sourceWidth, targetHeight / (double)sourceHeight);
        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return new RectI((targetWidth - width) / 2, (targetHeight - height) / 2, width, height);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Detach();
        lock (_sync)
        {
            _converter.Dispose();
            _uploadTexture?.Dispose();
            _uploadTexture = null;
        }
    }
}
