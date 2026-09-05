using System.Buffers;
using System.Diagnostics;
using Beamcast.Codec;
using Beamcast.Codec.Gpu;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Beamcast.Capture;

#pragma warning disable CA1416 // Newer members are guarded with ApiInformation at runtime.

/// <summary>A captured frame that stays on the GPU. Valid only for the duration of the callback.</summary>
public readonly record struct GpuFrame(ID3D11Texture2D Texture, int Width, int Height, long TimestampMs);

/// <summary>
/// Windows.Graphics.Capture session for one monitor or window. Each frame is copied once on the
/// GPU into a texture we own and handed to <see cref="TextureArrived"/> on the capture thread.
/// CPU pixels are only produced on request through <see cref="ReadPixels"/> (VP8 fallback).
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly object _sync = new();

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _frameTexture;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _poolSize;
    private long _lastFrameTicks;
    private long _minIntervalTicks;
    private bool _disposed;

    public ScreenCapture(GpuDevice gpu)
    {
        _gpu = gpu;
    }

    public static bool IsSupported => SafeTry.Run(GraphicsCaptureSession.IsSupported);

    public event Action<GpuFrame>? TextureArrived;

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

            // Windows only honours IsBorderRequired = false after borderless access was granted
            // to this process; the request starts at launch, so this normally returns at once.
            SafeTry.Run(() => CaptureAccess.EnsureBorderlessAsync().Wait(TimeSpan.FromMilliseconds(800)));

            _item = CaptureInterop.CreateItem(source);
            _poolSize = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _gpu.WinRtDevice,
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
            {
                SafeTry.Run(() => _session.IsBorderRequired = false);
                Diag.Log($"capture: border {(CaptureAccess.BorderlessGranted ? "hidden" : "kept by Windows (no borderless access)")}");
            }
            // Newer Windows 11 builds throttle capture to ~60 Hz unless told otherwise, which on a
            // 75/120/144 Hz display means every other frame. 4 ms lets the display rate through.
            if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName!, "MinUpdateInterval"))
                SafeTry.Run(() => _session.MinUpdateInterval = TimeSpan.FromMilliseconds(4));

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
        lock (_gpu.ContextLock)
        {
            _frameTexture?.Dispose();
            _frameTexture = null;
            _staging?.Dispose();
            _staging = null;
        }
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
            if (IsDue(Stopwatch.GetTimestamp()) && contentSize.Width > 0 && contentSize.Height > 0)
                DeliverFrame(frame, contentSize);

            if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
            {
                _poolSize = contentSize;
                lock (_sync)
                {
                    _pool?.Recreate(_gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
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

    /// <summary>
    /// Frame pacing against the target rate. A plain "at least 1/fps since the last frame" rule
    /// would accept every other frame on a 75 Hz display when asked for 60, landing at 37.5.
    /// Instead the next deadline advances by exactly one interval, with half an interval of
    /// tolerance, so the accepted rate averages out to the target from any refresh rate above it.
    /// </summary>
    private bool IsDue(long now)
    {
        var interval = Interlocked.Read(ref _minIntervalTicks);
        if (interval == 0)
            return true;

        if (_lastFrameTicks == 0 || now - _lastFrameTicks > interval * 2)
        {
            _lastFrameTicks = now;
            return true;
        }

        var deadline = _lastFrameTicks + interval;
        if (now < deadline - interval / 2)
            return false;

        _lastFrameTicks = deadline;
        return true;
    }

    private void DeliverFrame(Direct3D11CaptureFrame frame, SizeInt32 contentSize)
    {
        var handler = TextureArrived;
        if (handler is null)
            return;

        lock (_gpu.ContextLock)
        {
            using var source = CaptureInterop.GetTexture(frame.Surface);
            var description = source.Description;
            var width = Math.Min(contentSize.Width, (int)description.Width) & ~1;
            var height = Math.Min(contentSize.Height, (int)description.Height) & ~1;
            if (width <= 0 || height <= 0)
                return;

            if (_frameTexture is null || _frameTexture.Description.Width != width || _frameTexture.Description.Height != height)
            {
                _frameTexture?.Dispose();
                _frameTexture = _gpu.CreateTexture(Format.B8G8R8A8_UNorm, width, height, BindFlags.ShaderResource | BindFlags.RenderTarget);
            }

            var box = new Box(0, 0, 0, width, height, 1);
            _gpu.Context.CopySubresourceRegion(_frameTexture, 0, 0, 0, 0, source, 0, box);
            handler(new GpuFrame(_frameTexture, width, height, Environment.TickCount64));
        }
    }

    /// <summary>
    /// Reads a GPU frame back into a pooled BGRA buffer (caller returns it to <see cref="ArrayPool{T}.Shared"/>).
    /// Only the VP8 fallback uses this; it costs a GPU→CPU copy of the whole frame.
    /// Must be called while holding <see cref="GpuDevice.ContextLock"/> (i.e. from the callback).
    /// </summary>
    public unsafe RawFrame ReadPixels(GpuFrame frame)
    {
        if (_staging is null || _staging.Description.Width != frame.Width || _staging.Description.Height != frame.Height)
        {
            _staging?.Dispose();
            _staging = _gpu.Device.CreateTexture2D(
                new Texture2DDescription
                {
                    Width = (uint)frame.Width,
                    Height = (uint)frame.Height,
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

        var context = _gpu.Context;
        context.CopyResource(_staging, frame.Texture);
        var mapped = context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = frame.Width * 4;
            var buffer = ArrayPool<byte>.Shared.Rent(rowBytes * frame.Height);
            fixed (byte* dst = buffer)
            {
                var src = (byte*)mapped.DataPointer;
                for (var y = 0; y < frame.Height; y++)
                    Buffer.MemoryCopy(src + y * mapped.RowPitch, dst + y * rowBytes, rowBytes, rowBytes);
            }
            return new RawFrame(buffer, frame.Width, frame.Height, frame.TimestampMs);
        }
        finally
        {
            context.Unmap(_staging, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}
