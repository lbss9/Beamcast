using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Beamcast.Codec;
using Beamcast.Codec.Gpu;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using static Beamcast.Capture.NativeMethods;

namespace Beamcast.Capture;

#pragma warning disable CA1416 // Newer members are guarded with ApiInformation at runtime.

/// <summary>A captured frame that stays on the GPU. Valid only for the duration of the callback.</summary>
public readonly record struct GpuFrame(ID3D11Texture2D Texture, int Width, int Height, long TimestampMs);

/// <summary>
/// Captures one monitor or window. Each frame is copied once on the GPU into a texture we own and
/// handed to <see cref="TextureArrived"/> on the capture thread. CPU pixels are only produced on
/// request through <see cref="ReadPixels"/> (VP8 fallback).
///
/// Monitors go through DXGI Desktop Duplication: it never draws Windows' capture border, works
/// on every Windows 10/11 build and delivers at display rate. The cursor is not part of those
/// frames, so it is drawn on the GPU texture with GDI (DrawIconEx) when wanted. Windows, and any
/// monitor whose duplication cannot be created (rotated output, other adapter, protected session),
/// use Windows.Graphics.Capture, asking for borderless capture where the OS allows it.
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private static readonly TimeSpan IdleRepeatInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RecreateBudget = TimeSpan.FromSeconds(20);

    private readonly GpuDevice _gpu;
    private readonly object _sync = new();

    // Windows.Graphics.Capture path
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;

    // Desktop Duplication path
    private IDXGIOutput1? _output;
    private IDXGIOutputDuplication? _duplication;
    private Thread? _duplicationThread;
    private CancellationTokenSource? _duplicationCts;
    private RawRect _outputRect;
    private long _lastDeliveredTicks;

    private ID3D11Texture2D? _frameTexture;
    private ID3D11Texture2D? _staging;
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

    /// <summary>"duplication" or "wgc" while capturing; empty when stopped.</summary>
    public string Method { get; private set; } = string.Empty;

    public void Start(CaptureSource source, int maxFps)
    {
        lock (_sync)
        {
            StopCore();
            MaxFps = maxFps;

            if (source.Kind == CaptureSourceKind.Monitor && TryStartDuplication(source))
            {
                Method = "duplication";
                Diag.Log($"capture: monitor {source.Subtitle} via desktop duplication ({_outputRect.Right - _outputRect.Left}x{_outputRect.Bottom - _outputRect.Top})");
                return;
            }

            StartGraphicsCapture(source);
            Method = "wgc";
        }
    }

    // ----- Desktop Duplication -----

    private bool TryStartDuplication(CaptureSource source)
    {
        try
        {
            using var dxgiDevice = _gpu.Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            IDXGIOutput1? found = null;
            for (uint i = 0; adapter.EnumOutputs(i, out IDXGIOutput? output).Success && output is not null; i++)
            {
                using (output)
                {
                    var description = output.Description;
                    if (description.Monitor != source.Handle)
                        continue;
                    if (description.Rotation != ModeRotation.Identity && description.Rotation != ModeRotation.Unspecified)
                    {
                        Diag.Log("capture: rotated output, falling back to Windows.Graphics.Capture");
                        return false;
                    }
                    _outputRect = description.DesktopCoordinates;
                    found = output.QueryInterface<IDXGIOutput1>();
                    break;
                }
            }
            if (found is null)
            {
                Diag.Log("capture: monitor not on the encoder adapter, falling back to Windows.Graphics.Capture");
                return false;
            }

            _output = found;
            _duplication = _output.DuplicateOutput(_gpu.Device);
            _lastDeliveredTicks = 0;
            _duplicationCts = new CancellationTokenSource();
            var token = _duplicationCts.Token;
            _duplicationThread = new Thread(() => DuplicationLoop(token))
            {
                Name = "Beamcast capture",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _duplicationThread.Start();
            return true;
        }
        catch (Exception ex)
        {
            Diag.Log("capture: desktop duplication unavailable: " + ex.Message);
            SafeTry.Run(() => _duplication?.Dispose());
            SafeTry.Run(() => _output?.Dispose());
            _duplication = null;
            _output = null;
            return false;
        }
    }

    private void DuplicationLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var duplication = _duplication;
                if (duplication is null)
                {
                    if (!RecreateDuplication(ct))
                        return;
                    continue;
                }

                var result = duplication.AcquireNextFrame(100, out var info, out IDXGIResource? resource);
                if (result.Failure)
                {
                    if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
                    {
                        RepeatLastFrameIfIdle();
                        continue;
                    }
                    if (result.Code == Vortice.DXGI.ResultCode.AccessLost.Code || result.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code)
                    {
                        Diag.Log("capture: duplication access lost, recreating");
                        DisposeDuplication();
                        continue;
                    }
                    result.CheckError();
                }

                try
                {
                    if (resource is not null && IsDue(Stopwatch.GetTimestamp()))
                        DeliverDuplicatedFrame(resource);
                }
                finally
                {
                    resource?.Dispose();
                    SafeTry.Run(() => duplication.ReleaseFrame());
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Diag.Log("capture: duplication loop failed: " + ex.Message);
                Faulted?.Invoke(ex);
            }
        }
    }

    private bool RecreateDuplication(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested && Stopwatch.GetElapsedTime(started) < RecreateBudget)
        {
            try
            {
                var output = _output ?? throw new InvalidOperationException("no output");
                _duplication = output.DuplicateOutput(_gpu.Device);
                return true;
            }
            catch (Exception)
            {
                // Typical while the secure desktop (UAC, lock screen) is up; keep trying for a while.
                Thread.Sleep(250);
            }
        }
        if (!ct.IsCancellationRequested)
            Faulted?.Invoke(new InvalidOperationException("The display could not be captured any more."));
        return false;
    }

    private void DisposeDuplication()
    {
        var duplication = _duplication;
        _duplication = null;
        SafeTry.Run(() => duplication?.Dispose());
    }

    private void DeliverDuplicatedFrame(IDXGIResource resource)
    {
        var handler = TextureArrived;
        if (handler is null)
            return;

        lock (_gpu.ContextLock)
        {
            using var source = resource.QueryInterface<ID3D11Texture2D>();
            var description = source.Description;
            var width = (int)description.Width & ~1;
            var height = (int)description.Height & ~1;
            if (width <= 0 || height <= 0)
                return;

            EnsureFrameTexture(width, height, gdiCompatible: true);
            var box = new Box(0, 0, 0, width, height, 1);
            _gpu.Context.CopySubresourceRegion(_frameTexture, 0, 0, 0, 0, source, 0, box);
            if (ShowCursor)
                DrawCursor(_frameTexture!);
            _lastDeliveredTicks = Stopwatch.GetTimestamp();
            handler(new GpuFrame(_frameTexture!, width, height, Environment.TickCount64));
        }
    }

    /// <summary>
    /// Duplication only produces frames when something changed. Viewers who join a still desktop
    /// need a keyframe, and the encoder needs an input for that, so the last frame is re-offered
    /// at a slow rate while nothing moves.
    /// </summary>
    private void RepeatLastFrameIfIdle()
    {
        var handler = TextureArrived;
        if (handler is null || _lastDeliveredTicks == 0 || Stopwatch.GetElapsedTime(_lastDeliveredTicks) < IdleRepeatInterval)
            return;
        lock (_gpu.ContextLock)
        {
            var texture = _frameTexture;
            if (texture is null)
                return;
            _lastDeliveredTicks = Stopwatch.GetTimestamp();
            _lastFrameTicks = _lastDeliveredTicks;
            handler(new GpuFrame(texture, (int)texture.Description.Width, (int)texture.Description.Height, Environment.TickCount64));
        }
    }

    /// <summary>Draws the current cursor on the frame with GDI, straight onto the GPU texture.</summary>
    private void DrawCursor(ID3D11Texture2D texture)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0 || info.Cursor == IntPtr.Zero)
            return;
        if (!GetIconInfo(info.Cursor, out var icon))
            return;
        try
        {
            var x = info.ScreenPos.X - _outputRect.Left - icon.XHotspot;
            var y = info.ScreenPos.Y - _outputRect.Top - icon.YHotspot;
            var width = (int)texture.Description.Width;
            var height = (int)texture.Description.Height;
            if (x < -64 || y < -64 || x > width || y > height)
                return;

            using var surface = texture.QueryInterface<IDXGISurface1>();
            var hdc = surface.GetDC(false);
            try
            {
                DrawIconEx(hdc, x, y, info.Cursor, 0, 0, 0, IntPtr.Zero, DiNormal);
            }
            finally
            {
                surface.ReleaseDC(null);
            }
        }
        catch (Exception ex)
        {
            // Never let the cursor break the picture; just skip it this frame.
            Diag.Log("capture: cursor draw failed: " + ex.Message);
        }
        finally
        {
            if (icon.ColorBitmap != IntPtr.Zero)
                DeleteObject(icon.ColorBitmap);
            if (icon.MaskBitmap != IntPtr.Zero)
                DeleteObject(icon.MaskBitmap);
        }
    }

    // ----- Windows.Graphics.Capture -----

    private void StartGraphicsCapture(CaptureSource source)
    {
        // Windows only honours IsBorderRequired = false after borderless access was granted to
        // this process; the request starts at launch, so this normally returns at once.
        CaptureAccess.EnsureBorderless();

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
            Diag.Log($"capture: {source.Kind} via Windows.Graphics.Capture, border {(CaptureAccess.BorderlessGranted ? "hidden" : "kept by Windows (no borderless access)")}");
        }
        // Newer Windows 11 builds throttle capture to ~60 Hz unless told otherwise, which on a
        // 75/120/144 Hz display means every other frame. 4 ms lets the display rate through.
        if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName!, "MinUpdateInterval"))
            SafeTry.Run(() => _session.MinUpdateInterval = TimeSpan.FromMilliseconds(4));

        _session.StartCapture();
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
        Method = string.Empty;

        var cts = _duplicationCts;
        _duplicationCts = null;
        cts?.Cancel();
        _duplicationThread?.Join(TimeSpan.FromSeconds(2));
        _duplicationThread = null;
        cts?.Dispose();
        DisposeDuplication();
        SafeTry.Run(() => _output?.Dispose());
        _output = null;

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
        _lastDeliveredTicks = 0;
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

            EnsureFrameTexture(width, height, gdiCompatible: false);
            var box = new Box(0, 0, 0, width, height, 1);
            _gpu.Context.CopySubresourceRegion(_frameTexture, 0, 0, 0, 0, source, 0, box);
            handler(new GpuFrame(_frameTexture!, width, height, Environment.TickCount64));
        }
    }

    private void EnsureFrameTexture(int width, int height, bool gdiCompatible)
    {
        if (_frameTexture is not null && _frameTexture.Description.Width == width && _frameTexture.Description.Height == height)
            return;
        _frameTexture?.Dispose();
        _frameTexture = _gpu.CreateTexture(
            Format.B8G8R8A8_UNorm,
            width,
            height,
            BindFlags.ShaderResource | BindFlags.RenderTarget,
            gdiCompatible ? ResourceOptionFlags.GdiCompatible : ResourceOptionFlags.None
        );
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
