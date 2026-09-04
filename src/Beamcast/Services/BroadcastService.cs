using System.Buffers;
using System.Diagnostics;
using Beamcast.Capture;
using Beamcast.Codec;
using Beamcast.Codec.Gpu;
using Beamcast.Net;
using Beamcast.Render;
using Microsoft.UI.Dispatching;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Beamcast.Services;

public enum BroadcastState
{
    Idle,
    Preview,
    Live,
}

public sealed record HostStats(double Fps, double Kbps, double EncodeMs, int Width, int Height, int Viewers, string Codec)
{
    public static readonly HostStats Empty = new(0, 0, 0, 0, 0, 0, string.Empty);
}

/// <summary>
/// Owns the capture → convert → encode → fan-out pipeline for the broadcaster. Lives for the whole
/// process so navigating between pages never interrupts a stream.
///
/// GPU path (default): the captured BGRA texture is scaled and converted to NV12 by the D3D11 video
/// processor and handed to the hardware encoder on the same device, so a 4K frame never crosses
/// the PCIe bus. CPU path (VP8): the frame is read back and encoded with libvpx on a thread.
/// Every event is raised on the UI thread handed to <see cref="Initialize"/>.
/// </summary>
public sealed class BroadcastService
{
    public static BroadcastService Instance { get; } = new();

    private const int Nv12RingSize = 6;
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan Vp8KeyframeInterval = TimeSpan.FromSeconds(10);

    private readonly HostServer _server = new();
    private readonly SemaphoreSlim _frameSignal = new(0, 1);
    private readonly object _sync = new();

    private DispatcherQueue? _ui;
    private GpuDevice? _gpu;
    private SwapChainPresenter? _preview;
    private ScreenCapture? _capture;
    private VideoProcessorConverter? _converter;
    private readonly ID3D11Texture2D?[] _nv12Ring = new ID3D11Texture2D?[Nv12RingSize];
    private int _nv12Index;
    private (int Width, int Height) _nv12Size;

    private MfVideoEncoder? _gpuEncoder;
    private RawFrame? _latest;
    private Thread? _vp8Thread;
    private CancellationTokenSource? _vp8Cts;
    private int _keyframeRequested;
    private long _lastPreviewTicks;
    private volatile bool _paused;
    private volatile bool _live;

    private long _statsWindowStart;
    private int _statsFrames;
    private long _statsBytes;
    private double _statsEncodeMs;

    private string _preset = QualityPreset.Source;
    private int _fps = 60;
    private int _bitrateKbps = 30000;
    private bool _showCursor = true;
    private string _encoderPreference = EncoderPreference.Auto;

    private BroadcastService()
    {
        _server.KeyframeNeeded += OnKeyframeNeeded;
        _server.ViewerJoined += _ => Post(() => ViewersChanged?.Invoke());
        _server.ViewerLeft += _ => Post(() => ViewersChanged?.Invoke());
        _server.Faulted += ex => Post(() => Error?.Invoke(ex.Message));
    }

    public event Action<BroadcastState>? StateChanged;
    public event Action<HostStats>? StatsChanged;
    public event Action? ViewersChanged;
    public event Action<string>? Error;
    /// <summary>Raised on the UI thread once the first frame of a new source hit the preview.</summary>
    public event Action? PreviewStarted;

    public BroadcastState State { get; private set; }
    public CaptureSource? Source { get; private set; }
    public HostOptions? Options { get; private set; }
    public HostStats LastStats { get; private set; } = HostStats.Empty;
    public IReadOnlyList<ViewerInfo> Viewers => _server.Viewers;
    public bool IsPaused => _paused;
    public VideoCodec ActiveCodec { get; private set; } = VideoCodec.Vp8;
    public string EncoderName { get; private set; } = string.Empty;

    /// <summary>The presenter for the live preview; bind it to a GpuVideoView.</summary>
    public SwapChainPresenter Preview => _preview ??= new SwapChainPresenter(Gpu);

    public GpuDevice Gpu => _gpu ??= new GpuDevice();

    public string Preset
    {
        get => _preset;
        set => _preset = QualityPreset.Normalize(value);
    }

    public int Fps
    {
        get => _fps;
        set
        {
            _fps = QualityPreset.NormalizeFps(value);
            if (_capture is not null)
                _capture.MaxFps = _fps;
        }
    }

    public int BitrateKbps
    {
        get => _bitrateKbps;
        set
        {
            _bitrateKbps = QualityPreset.ClampBitrate(value);
            _gpuEncoder?.SetBitrate(_bitrateKbps);
        }
    }

    public string EncoderPreferenceValue
    {
        get => _encoderPreference;
        set => _encoderPreference = EncoderPreference.Normalize(value);
    }

    public bool ShowCursor
    {
        get => _showCursor;
        set
        {
            _showCursor = value;
            if (_capture is not null)
                _capture.ShowCursor = value;
        }
    }

    public void Initialize(DispatcherQueue ui) => _ui = ui;

    public void ApplySettings(AppSettings settings)
    {
        Preset = settings.QualityPreset;
        Fps = settings.Fps;
        BitrateKbps = settings.BitrateKbps;
        ShowCursor = settings.ShowCursor;
        EncoderPreferenceValue = settings.Encoder;
    }

    /// <summary>Starts (or switches) capture. Works both before and during a live session.</summary>
    public void SelectSource(CaptureSource source)
    {
        lock (_sync)
        {
            _capture ??= CreateCapture();
            _capture.ShowCursor = _showCursor;
            _capture.Start(source, _fps);
            Source = source;
            if (State == BroadcastState.Idle)
                SetState(BroadcastState.Preview);
            Interlocked.Exchange(ref _keyframeRequested, 1);
        }
    }

    public void ClearSource()
    {
        lock (_sync)
        {
            if (State == BroadcastState.Live)
                StopLiveCore();
            _capture?.Stop();
            Source = null;
            SetState(BroadcastState.Idle);
        }
        _preview?.Clear();
    }

    public void GoLive(HostOptions options)
    {
        lock (_sync)
        {
            if (State != BroadcastState.Preview || Source is null)
                throw new InvalidOperationException("Pick something to share first.");

            ActiveCodec = MfCodecs.Resolve(_encoderPreference);
            EncoderName = ActiveCodec == VideoCodec.Vp8 ? "libvpx (CPU)" : string.Empty;
            _server.SetStreamInfo(0, 0, _fps, ActiveCodec.ToWireName());
            _server.Start(options);
            Options = options;
            _paused = false;
            ResetStats();

            if (ActiveCodec == VideoCodec.Vp8)
            {
                _vp8Cts = new CancellationTokenSource();
                _vp8Thread = new Thread(() => Vp8Loop(_vp8Cts.Token))
                {
                    Name = "Beamcast VP8 encoder",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                };
                _vp8Thread.Start();
            }

            _live = true;
            Interlocked.Exchange(ref _keyframeRequested, 1);
            SetState(BroadcastState.Live);
        }
    }

    public void StopLive()
    {
        lock (_sync)
        {
            if (State != BroadcastState.Live)
                return;
            StopLiveCore();
            SetState(Source is null ? BroadcastState.Idle : BroadcastState.Preview);
        }
    }

    public void SetPaused(bool paused)
    {
        if (State != BroadcastState.Live || _paused == paused)
            return;
        _paused = paused;
        _server.SetState(paused ? StreamStates.Paused : StreamStates.Live);
        if (!paused)
            Interlocked.Exchange(ref _keyframeRequested, 1);
        Post(() => StateChanged?.Invoke(State));
    }

    public void Shutdown()
    {
        lock (_sync)
        {
            if (State == BroadcastState.Live)
                StopLiveCore();
            _capture?.Dispose();
            _capture = null;
            Source = null;
            State = BroadcastState.Idle;
        }
        _preview?.Dispose();
        _preview = null;
        DisposeGpuResources();
        _gpu?.Dispose();
        _gpu = null;
    }

    private void StopLiveCore()
    {
        _live = false;

        var cts = _vp8Cts;
        _vp8Cts = null;
        cts?.Cancel();
        _vp8Thread?.Join(TimeSpan.FromSeconds(3));
        _vp8Thread = null;
        cts?.Dispose();

        var encoder = _gpuEncoder;
        _gpuEncoder = null;
        encoder?.Dispose();

        _server.Stop();
        Options = null;
        _paused = false;

        var stale = Interlocked.Exchange(ref _latest, null);
        if (stale is not null)
            ArrayPool<byte>.Shared.Return(stale.Pixels);

        LastStats = HostStats.Empty;
        Post(() => StatsChanged?.Invoke(LastStats));
    }

    private ScreenCapture CreateCapture()
    {
        var capture = new ScreenCapture(Gpu);
        capture.TextureArrived += OnTexture;
        capture.Faulted += OnCaptureFaulted;
        return capture;
    }

    private void OnCaptureFaulted(Exception ex)
    {
        Post(() =>
        {
            Error?.Invoke(ex.Message);
            ClearSource();
        });
    }

    private void OnKeyframeNeeded()
    {
        Diag.Log("broadcast: keyframe needed");
        Interlocked.Exchange(ref _keyframeRequested, 1);
        _gpuEncoder?.RequestKeyframe();
    }

    /// <summary>Capture thread, context lock held by the caller.</summary>
    private void OnTexture(GpuFrame frame)
    {
        MaybePreview(frame);

        if (!_live || _paused)
            return;

        if (ActiveCodec == VideoCodec.Vp8)
        {
            QueueForVp8(frame);
            return;
        }

        try
        {
            EncodeOnGpu(frame);
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                Error?.Invoke(ex.Message);
                StopLive();
            });
        }
    }

    private void MaybePreview(GpuFrame frame)
    {
        var presenter = _preview;
        if (presenter is null || !presenter.IsAttached)
            return;
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastPreviewTicks, now) < PreviewInterval)
            return;
        var first = _lastPreviewTicks == 0;
        _lastPreviewTicks = now;
        presenter.Present(frame.Texture, 0, frame.Width, frame.Height, false);
        if (first)
            Post(() => PreviewStarted?.Invoke());
    }

    private void EncodeOnGpu(GpuFrame frame)
    {
        var (width, height) = QualityPreset.Fit(_preset, frame.Width, frame.Height);
        if (width <= 0 || height <= 0)
            return;

        var encoder = _gpuEncoder;
        if (encoder is not null && encoder.KeyframeOverdue)
        {
            // The driver ignored the keyframe request; a fresh encoder starts with an IDR frame.
            Diag.Log("broadcast: keyframe overdue, recreating encoder");
            encoder.Dispose();
            encoder = null;
            _gpuEncoder = null;
        }
        if (encoder is null || encoder.Width != width || encoder.Height != height || encoder.Fps != _fps)
        {
            encoder?.Dispose();
            encoder = new MfVideoEncoder(Gpu, ActiveCodec, width, height, _fps, _bitrateKbps);
            encoder.FrameEncoded += OnGpuFrameEncoded;
            encoder.Faulted += ex => Post(() =>
            {
                Error?.Invoke(ex.Message);
                StopLive();
            });
            _gpuEncoder = encoder;
            EncoderName = encoder.Name;
            Diag.Log($"broadcast: encoder {encoder.Name} {width}x{height}@{_fps} {_bitrateKbps} kbps");
            _server.SetStreamInfo(width, height, _fps, ActiveCodec.ToWireName());
            Interlocked.Exchange(ref _keyframeRequested, 1);
        }

        // The encoder only takes a frame when it asked for one; otherwise this frame is dropped and
        // the next capture wins. That keeps the queue depth at zero, which is the whole point.
        if (!encoder.WantsInput)
            return;

        _converter ??= new VideoProcessorConverter(Gpu);
        var nv12 = NextNv12(width, height);
        _converter.Convert(frame.Texture, 0, frame.Width, frame.Height, false, nv12, width, height, true);

        if (Interlocked.Exchange(ref _keyframeRequested, 0) == 1)
            encoder.RequestKeyframe();
        encoder.TrySubmit(nv12, frame.TimestampMs);
    }

    private ID3D11Texture2D NextNv12(int width, int height)
    {
        if (_nv12Size != (width, height))
        {
            for (var i = 0; i < _nv12Ring.Length; i++)
            {
                _nv12Ring[i]?.Dispose();
                _nv12Ring[i] = null;
            }
            _nv12Size = (width, height);
        }

        _nv12Index = (_nv12Index + 1) % _nv12Ring.Length;
        return _nv12Ring[_nv12Index] ??= Gpu.CreateTexture(Format.NV12, width, height, BindFlags.RenderTarget);
    }

    private void OnGpuFrameEncoded(EncodedFrame frame, double encodeMs)
    {
        if (!_live)
            return;
        if (frame.IsKeyframe || frame.Sequence % 120 == 1)
            Diag.Log($"broadcast: frame #{frame.Sequence} key={frame.IsKeyframe} {frame.Data.Length} B viewers={_server.ViewerCount}");
        _server.Broadcast(frame);
        AccountFrame(frame, encodeMs);
    }

    private void QueueForVp8(GpuFrame frame)
    {
        var raw = _capture!.ReadPixels(frame);
        var previous = Interlocked.Exchange(ref _latest, raw);
        if (previous is not null)
            ArrayPool<byte>.Shared.Return(previous.Pixels);
        try
        {
            _frameSignal.Release();
        }
        catch (SemaphoreFullException) { }
    }

    private void Vp8Loop(CancellationToken ct)
    {
        using var encoder = new Vp8Encoder { BitrateKbps = _bitrateKbps };
        byte[]? encodeBuffer = null;
        var lastKeyframe = Stopwatch.GetTimestamp();
        var lastWidth = 0;
        var lastHeight = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_frameSignal.Wait(250, ct))
                    continue;

                var raw = Interlocked.Exchange(ref _latest, null);
                if (raw is null)
                    continue;

                try
                {
                    var (width, height) = QualityPreset.Fit(_preset, raw.Width, raw.Height);
                    if (width <= 0 || height <= 0)
                        continue;

                    var needed = width * height * 4;
                    if (encodeBuffer is null || encodeBuffer.Length != needed)
                        encodeBuffer = new byte[needed];

                    if (width == raw.Width && height == raw.Height)
                        Buffer.BlockCopy(raw.Pixels, 0, encodeBuffer, 0, needed);
                    else
                        FrameScaler.Resize(raw.Pixels, raw.Width, raw.Height, encodeBuffer, width, height);

                    if (width != lastWidth || height != lastHeight)
                    {
                        lastWidth = width;
                        lastHeight = height;
                        _server.SetStreamInfo(width, height, _fps, VideoCodecs.Vp8Name);
                    }

                    encoder.BitrateKbps = _bitrateKbps;
                    var wantKey = Interlocked.Exchange(ref _keyframeRequested, 0) == 1
                        || Stopwatch.GetElapsedTime(lastKeyframe) >= Vp8KeyframeInterval;
                    if (wantKey)
                        encoder.RequestKeyframe();

                    var start = Stopwatch.GetTimestamp();
                    var encoded = encoder.Encode(encodeBuffer, width, height, raw.TimestampMs);
                    var encodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (encoded is null)
                        continue;
                    if (encoded.IsKeyframe)
                        lastKeyframe = Stopwatch.GetTimestamp();

                    _server.Broadcast(encoded);
                    AccountFrame(encoded, encodeMs);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(raw.Pixels);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Post(() =>
            {
                Error?.Invoke(ex.Message);
                StopLive();
            });
        }
    }

    private void ResetStats()
    {
        _statsWindowStart = Stopwatch.GetTimestamp();
        _statsFrames = 0;
        _statsBytes = 0;
        _statsEncodeMs = 0;
    }

    private void AccountFrame(EncodedFrame frame, double encodeMs)
    {
        _statsFrames++;
        _statsBytes += frame.Data.Length;
        _statsEncodeMs += encodeMs;
        var elapsed = Stopwatch.GetElapsedTime(_statsWindowStart);
        if (elapsed.TotalMilliseconds < 1000)
            return;

        var seconds = elapsed.TotalSeconds;
        var stats = new HostStats(
            _statsFrames / seconds,
            _statsBytes * 8 / 1000.0 / seconds,
            _statsFrames > 0 ? _statsEncodeMs / _statsFrames : 0,
            frame.Width,
            frame.Height,
            _server.ViewerCount,
            ActiveCodec.ToWireName().ToUpperInvariant()
        );
        LastStats = stats;
        ResetStats();
        Post(() => StatsChanged?.Invoke(stats));
    }

    private void DisposeGpuResources()
    {
        _converter?.Dispose();
        _converter = null;
        for (var i = 0; i < _nv12Ring.Length; i++)
        {
            _nv12Ring[i]?.Dispose();
            _nv12Ring[i] = null;
        }
        _nv12Size = default;
    }

    private void SetState(BroadcastState state)
    {
        if (State == state)
            return;
        State = state;
        if (state == BroadcastState.Idle)
            _lastPreviewTicks = 0;
        Post(() => StateChanged?.Invoke(state));
    }

    private void Post(Action action)
    {
        var ui = _ui;
        if (ui is null || ui.HasThreadAccess)
            action();
        else
            ui.TryEnqueue(() => action());
    }
}
