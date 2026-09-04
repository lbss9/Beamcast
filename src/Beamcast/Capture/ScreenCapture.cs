using System.Buffers;
using System.Diagnostics;
using Beamcast.Codec;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Beamcast.Capture;

#pragma warning disable CA1416 // Newer members are guarded with ApiInformation at runtime.

/// <summary>
/// Wraps a Windows.Graphics.Capture session for one monitor or window and turns the GPU frames
/// into tightly packed BGRA byte arrays at (at most) the requested frame rate. Frames are handed
/// to <see cref="FrameArrived"/> on the capture thread; the buffer comes from
/// <see cref="ArrayPool{T}.Shared"/> and must be returned by the consumer.
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly object _sync = new();

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _poolSize;
    private long _lastFrameTicks;
    private long _minIntervalTicks;
    private bool _disposed;

    public ScreenCapture()
    {
        (_device, _winrtDevice) = CaptureInterop.CreateDevice();
        _context = _device.ImmediateContext;
    }

    public static bool IsSupported => SafeTry.Run(GraphicsCaptureSession.IsSupported);

    public event Action<RawFrame>? FrameArrived;

    public event Action<Exception>? Faulted;

    /// <summary>Upper bound on delivered frames per second. Capture itself runs at display rate.</summary>
    public int MaxFps
    {
        set => Interlocked.Exchange(ref _minIntervalTicks, value <= 0 ? 0 : Stopwatch.Frequency / value);
    }

    public bool ShowCursor { get; set; } = true;

    public void Start(CaptureSource source, int maxFps)
    {
        lock (_sync)
        {
            StopCore();
            MaxFps = maxFps;

            _item = CaptureInterop.CreateItem(source);
            _poolSize = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _poolSize
            );
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(_item);
            _item.Closed += OnItemClosed;

            if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName!, nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
                SafeTry.Run(() => _session.IsCursorCaptureEnabled = ShowCursor);
            if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName!, nameof(GraphicsCaptureSession.IsBorderRequired)))
                SafeTry.Run(() => _session.IsBorderRequired = false);

            _session.StartCapture();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopCore();
        }
    }

    private void StopCore()
    {
        if (_item is not null)
            _item.Closed -= OnItemClosed;
        if (_pool is not null)
            _pool.FrameArrived -= OnFrameArrived;

        SafeTry.Run(() => _session?.Dispose());
        SafeTry.Run(() => _pool?.Dispose());
        _session = null;
        _pool = null;
        _item = null;
        _staging?.Dispose();
        _staging = null;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        Stop();
        Faulted?.Invoke(new InvalidOperationException("The shared window was closed."));
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null)
                return;

            var contentSize = frame.ContentSize;
            var now = Stopwatch.GetTimestamp();
            var interval = Interlocked.Read(ref _minIntervalTicks);
            var due = interval == 0 || now - _lastFrameTicks >= interval;

            if (due && contentSize.Width > 0 && contentSize.Height > 0)
            {
                _lastFrameTicks = now;
                var raw = ReadPixels(frame.Surface, contentSize);
                if (raw is not null)
                    FrameArrived?.Invoke(raw);
            }

            if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
            {
                _poolSize = contentSize;
                lock (_sync)
                {
                    _pool?.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
                }
            }
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private unsafe RawFrame? ReadPixels(IDirect3DSurface surface, SizeInt32 contentSize)
    {
        using var texture = CaptureInterop.GetTexture(surface);
        var description = texture.Description;
        var width = Math.Min(contentSize.Width, (int)description.Width);
        var height = Math.Min(contentSize.Height, (int)description.Height);
        if (width <= 0 || height <= 0)
            return null;

        if (_staging is null || _staging.Description.Width != description.Width || _staging.Description.Height != description.Height)
        {
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(
                new Texture2DDescription
                {
                    Width = description.Width,
                    Height = description.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                }
            );
        }

        _context.CopyResource(_staging, texture);
        var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = width * 4;
            var buffer = ArrayPool<byte>.Shared.Rent(rowBytes * height);
            fixed (byte* dst = buffer)
            {
                var src = (byte*)mapped.DataPointer;
                for (var y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(src + y * mapped.RowPitch, dst + y * rowBytes, rowBytes, rowBytes);
                }
            }
            return new RawFrame(buffer, width, height, Environment.TickCount64);
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _context.Dispose();
        _device.Dispose();
        SafeTry.Run(() => _winrtDevice.Dispose());
    }
}
