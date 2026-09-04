using System.Buffers;
using System.Diagnostics;
using Beamcast.Capture;
using Beamcast.Codec;
using Beamcast.Net;
using Microsoft.UI.Dispatching;

namespace Beamcast.Services;

public enum BroadcastState
{
    Idle,
    Preview,
    Live,
}

public sealed record HostStats(double Fps, double Kbps, double EncodeMs, int Width, int Height, int Viewers)
{
    public static readonly HostStats Empty = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Owns the capture → scale → encode → fan-out pipeline for the broadcaster. Lives for the whole
/// process so navigating between pages never interrupts a stream. Every event is raised on the
/// UI thread handed to <see cref="Initialize"/>.
/// </summary>
public sealed class BroadcastService
{
    public static BroadcastService Instance { get; } = new();

    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(66);
    private static readonly TimeSpan KeyframeInterval = TimeSpan.FromSeconds(10);

    private readonly HostServer _server = new();
    private readonly SemaphoreSlim _frameSignal = new(0, 1);
    private readonly object _sync = new();

    private DispatcherQueue? _ui;
    private ScreenCapture? _capture;
    private RawFrame? _latest;
    private Thread? _encodeThread;
    private CancellationTokenSource? _encodeCts;
    private int _keyframeRequested;
    private long _lastPreviewTicks;
    private int _previewPending;
    private byte[]? _previewBuffer;
    private volatile bool _paused;

    private string _preset = QualityPreset.P1080;
    private int _fps = 30;
    private int _bitrateKbps = 5000;
    private bool _showCursor = true;

    private BroadcastService()
    {
        _server.KeyframeNeeded += () => Interlocked.Exchange(ref _keyframeRequested, 1);
        _server.ViewerJoined += _ => Post(() => ViewersChanged?.Invoke());
        _server.ViewerLeft += _ => Post(() => ViewersChanged?.Invoke());
        _server.Faulted += ex => Post(() => Error?.Invoke(ex.Message));
    }

    public event Action<BroadcastState>? StateChanged;
    public event Action<byte[], int, int>? PreviewFrame;
    public event Action<HostStats>? StatsChanged;
    public event Action? ViewersChanged;
    public event Action<string>? Error;

    public BroadcastState State { get; private set; }
    public CaptureSource? Source { get; private set; }
    public HostOptions? Options { get; private set; }
    public HostStats LastStats { get; private set; } = HostStats.Empty;
    public IReadOnlyList<ViewerInfo> Viewers => _server.Viewers;
    public bool IsPaused => _paused;

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
        set => _bitrateKbps = QualityPreset.ClampBitrate(value);
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
    }

    public void GoLive(HostOptions options)
    {
        lock (_sync)
        {
            if (State != BroadcastState.Preview || Source is null)
                throw new InvalidOperationException("Pick something to share first.");

            _server.SetStreamInfo(0, 0, _fps);
            _server.Start(options);
            Options = options;
            _paused = false;
            _encodeCts = new CancellationTokenSource();
            _encodeThread = new Thread(() => EncodeLoop(_encodeCts.Token))
            {
                Name = "Beamcast encoder",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _encodeThread.Start();
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
    }

    private void StopLiveCore()
    {
        var cts = _encodeCts;
        _encodeCts = null;
        cts?.Cancel();
        _encodeThread?.Join(TimeSpan.FromSeconds(3));
        _encodeThread = null;
        cts?.Dispose();

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
        var capture = new ScreenCapture();
        capture.FrameArrived += OnCaptureFrame;
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

    private void OnCaptureFrame(RawFrame frame)
    {
        MaybePreview(frame);

        if (State != BroadcastState.Live || _paused)
        {
            ArrayPool<byte>.Shared.Return(frame.Pixels);
            return;
        }

        var previous = Interlocked.Exchange(ref _latest, frame);
        if (previous is not null)
            ArrayPool<byte>.Shared.Return(previous.Pixels);

        try
        {
            _frameSignal.Release();
        }
        catch (SemaphoreFullException) { }
    }

    private void MaybePreview(RawFrame frame)
    {
        if (PreviewFrame is null || _ui is null)
            return;
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastPreviewTicks, now) < PreviewInterval)
            return;
        if (Interlocked.CompareExchange(ref _previewPending, 1, 0) != 0)
            return;

        _lastPreviewTicks = now;
        var length = frame.ByteLength;
        if (_previewBuffer is null || _previewBuffer.Length < length)
            _previewBuffer = new byte[length];
        Buffer.BlockCopy(frame.Pixels, 0, _previewBuffer, 0, length);

        var buffer = _previewBuffer;
        var width = frame.Width;
        var height = frame.Height;
        if (!_ui.TryEnqueue(() =>
            {
                try
                {
                    PreviewFrame?.Invoke(buffer, width, height);
                }
                finally
                {
                    Interlocked.Exchange(ref _previewPending, 0);
                }
            }))
        {
            Interlocked.Exchange(ref _previewPending, 0);
        }
    }

    private void EncodeLoop(CancellationToken ct)
    {
        using var encoder = new Vp8Encoder { BitrateKbps = _bitrateKbps };
        byte[]? encodeBuffer = null;
        var lastKeyframe = Stopwatch.GetTimestamp();
        var windowStart = Stopwatch.GetTimestamp();
        var windowFrames = 0;
        long windowBytes = 0;
        double windowEncodeMs = 0;
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
                        _server.SetStreamInfo(width, height, _fps);
                    }

                    encoder.BitrateKbps = _bitrateKbps;
                    var wantKey = Interlocked.Exchange(ref _keyframeRequested, 0) == 1
                        || Stopwatch.GetElapsedTime(lastKeyframe) >= KeyframeInterval;
                    if (wantKey)
                        encoder.RequestKeyframe();

                    var start = Stopwatch.GetTimestamp();
                    var encoded = encoder.Encode(encodeBuffer, width, height, raw.TimestampMs);
                    windowEncodeMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (encoded is null)
                        continue;

                    if (encoded.IsKeyframe)
                        lastKeyframe = Stopwatch.GetTimestamp();

                    _server.Broadcast(encoded);
                    windowFrames++;
                    windowBytes += encoded.Data.Length;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(raw.Pixels);
                }

                var elapsed = Stopwatch.GetElapsedTime(windowStart);
                if (elapsed.TotalMilliseconds >= 1000)
                {
                    var seconds = elapsed.TotalSeconds;
                    var stats = new HostStats(
                        windowFrames / seconds,
                        windowBytes * 8 / 1000.0 / seconds,
                        windowFrames > 0 ? windowEncodeMs / windowFrames : 0,
                        lastWidth,
                        lastHeight,
                        _server.ViewerCount
                    );
                    LastStats = stats;
                    windowStart = Stopwatch.GetTimestamp();
                    windowFrames = 0;
                    windowBytes = 0;
                    windowEncodeMs = 0;
                    Post(() => StatsChanged?.Invoke(stats));
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

    private void SetState(BroadcastState state)
    {
        if (State == state)
            return;
        State = state;
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
