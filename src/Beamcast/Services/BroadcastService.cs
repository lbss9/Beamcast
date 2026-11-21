using System.Buffers;
using System.Diagnostics;
using Beamcast.Audio;
using Beamcast.Capture;
using Beamcast.Codec;
using Beamcast.Codec.Gpu;
using Beamcast.Net;
using Beamcast.Render;
using Microsoft.UI.Dispatching;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Beamcast.Services;

public enum BroadcastState
{
    Idle,
    Preview,
    Live,
}

/// <param name="Viewers">People currently watching this stream (host 2.3.0+; 0 on older hosts).</param>
/// <param name="TargetKbps">The bitrate the encoder runs at now: the chosen one, or lower while adapting.</param>
/// <param name="Adapted">True while adaptive quality holds the bitrate below the chosen one.</param>
/// <param name="Standby">True while live with nobody watching: capture and preview run, encoder and network rest.</param>
/// <param name="WorstViewerDelayMs">The highest delay any viewer reported in this window (0 = nobody complained; host 2.5.0+).</param>
public sealed record HostStats(double Fps, double Kbps, double AudioKbps, double EncodeMs, int Width, int Height, string Codec, int Viewers, int TargetKbps, bool Adapted, bool Standby, int WorstViewerDelayMs)
{
    public static readonly HostStats Empty = new(0, 0, 0, 0, 0, 0, string.Empty, 0, 0, false, false, 0);
}

/// <summary>
/// Owns the capture → convert → encode pipeline and publishes the result as one stream in the
/// lounge. Lives for the whole process so navigating between pages never interrupts a stream.
///
/// GPU path (default): the captured BGRA texture is scaled and converted to NV12 by the D3D11 video
/// processor and handed to the hardware encoder on the same device, so a 4K frame never crosses
/// the PCIe bus. CPU path (VP8): the frame is read back and encoded with libvpx on a thread.
/// Audio, when enabled, is captured per process and sent on the same stream.
/// Every event is raised on the UI thread handed to <see cref="Initialize"/>.
/// </summary>
public sealed partial class BroadcastService
{
    public static BroadcastService Instance { get; } = new();

    private const int Nv12RingSize = 6;
    private const int MaxPendingUpstreamFrames = 4;
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan Vp8KeyframeInterval = TimeSpan.FromSeconds(10);

    private readonly LoungeService _lounge = LoungeService.Instance;
    private readonly AudioBroadcaster _audio = new();
    private readonly FrameGate _upstreamGate = new(MaxPendingUpstreamFrames);
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
    private long _lastRebuildTicks;
    private bool _rebuildHeld;

    /// <summary>Shortest gap between two encoder rebuilds forced by an overdue keyframe.</summary>
    private const long RebuildGapMs = 2000;
    private volatile bool _paused;
    private volatile bool _live;
    private uint _streamId;
    private StreamMetaMessage _meta = new();

    private Task? _teardown;
    private System.Threading.Timer? _watchdog;
    private long _lastFrameTicks;
    private bool _framesStalled;
    private int _fallingBack;
    private long _statsWindowStart;
    private long _lastStatsLogTicks;
    private int _statsFrames;
    private long _statsBytes;
    private long _statsAudioBytes;
    private double _statsEncodeMs;

    private string _preset = QualityPreset.Source;
    private int _fps = 60;
    private int _bitrateKbps = 30000;
    private bool _showCursor = true;
    private string _encoderPreference = EncoderPreference.Auto;
    private string _audioMode = AudioMode.Auto;

    private volatile Calibration? _calibration;
    private readonly AdaptiveBitrate _adaptive = new();
    private volatile bool _adaptiveEnabled = true;
    private volatile bool _viewerSounds = true;
    private volatile bool _standbyEnabled = true;
    private readonly HashSet<uint> _viewers = [];

    private BroadcastService()
    {
        _lounge.KeyframeRequested += OnKeyframeRequested;
        _lounge.ViewerJoined += (streamId, viewerId, name) => OnViewer(streamId, viewerId, name, joined: true);
        _lounge.ViewerLeft += (streamId, viewerId, name) => OnViewer(streamId, viewerId, name, joined: false);
        _lounge.ViewerReported += OnViewerReported;
        _lounge.DirectCongested += OnDirectCongested;
        _lounge.ReducedKeyframeRequested += OnReducedKeyframeRequested;
        _lounge.StateChanged += state =>
        {
            if (state == LoungeState.Disconnected)
                StopLive();
        };
        _lounge.Reconnected += () => _ = RepublishAsync();
        _audio.PacketReady += OnAudioPacket;
        _audio.Faulted += message => Post(() => Error?.Invoke(message));
    }

    public event Action<BroadcastState>? StateChanged;
    public event Action<HostStats>? StatsChanged;
    public event Action<string>? Error;
    /// <summary>UI thread: someone started (true) or stopped (false) watching my stream, by name.</summary>
    public event Action<string, bool>? ViewerChanged;
    /// <summary>Raised on the UI thread once the first frame of a new source hit the preview.</summary>
    public event Action? PreviewStarted;

    public BroadcastState State { get; private set; }
    public CaptureSource? Source { get; private set; }
    public HostStats LastStats { get; private set; } = HostStats.Empty;
    public bool IsPaused => _paused;
    public VideoCodec ActiveCodec { get; private set; } = VideoCodec.Vp8;
    public string EncoderName { get; private set; } = string.Empty;
    public uint StreamId => _streamId;
    public AudioCaptureInfo AudioInfo => _audio.Info;
    public bool AudioActive => _audio.IsRunning;

    /// <summary>The presenter for the live preview; bind it to a GpuVideoView.</summary>
    public SwapChainPresenter Preview => _preview ??= new SwapChainPresenter(Gpu);

    public GpuDevice Gpu => _gpu ??= new GpuDevice();

    public string Preset
    {
        get => _preset;
        set
        {
            _preset = QualityPreset.Normalize(value);
            RefreshLiveSize();
        }
    }

    /// <summary>
    /// While live the stream keeps one resolution: the one announced at go-live, or the one the
    /// preset now asks for. Frames of any other size (a window that was resized, a switch from a
    /// monitor to a window) are letterboxed into it, so the encoder is never rebuilt behind the
    /// viewers' backs and their decoders never see a mid-stream format change.
    /// </summary>
    private (int Width, int Height) StreamSize(int frameWidth, int frameHeight)
    {
        if (_live && _meta.Width > 0 && _meta.Height > 0)
            return (_meta.Width, _meta.Height);
        return QualityPreset.Fit(_preset, frameWidth, frameHeight);
    }

    private void RefreshLiveSize()
    {
        var source = Source;
        if (!_live || source is null)
            return;
        var (width, height) = QualityPreset.Fit(_preset, source.Width, source.Height);
        if (width <= 0 || height <= 0 || (width == _meta.Width && height == _meta.Height))
            return;
        _meta = new StreamMetaMessage
        {
            Title = _meta.Title,
            Codec = _meta.Codec,
            Width = width,
            Height = height,
            Fps = _meta.Fps,
            Audio = _meta.Audio,
            State = _meta.State,
        };
        _lounge.UpdateStreamMeta(_streamId, _meta);
        Interlocked.Exchange(ref _keyframeRequested, 1);
        Diag.Log($"broadcast: stream size now {width}x{height}");
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
            _gpuEncoder?.SetBitrate(EffectiveBitrateKbps);
        }
    }

    /// <summary>Lower the bitrate while the uplink drops frames, back up when it stops. The chosen bitrate is the ceiling.</summary>
    public bool AdaptiveQuality
    {
        get => _adaptiveEnabled;
        set
        {
            _adaptiveEnabled = value;
            if (!value)
                _adaptive.Reset(Environment.TickCount64);
            _gpuEncoder?.SetBitrate(EffectiveBitrateKbps);
        }
    }

    /// <summary>Play a sound when someone starts or stops watching.</summary>
    public bool ViewerSounds
    {
        get => _viewerSounds;
        set => _viewerSounds = value;
    }

    /// <summary>
    /// Encode and send only while someone is watching. With nobody in, capture and preview keep
    /// running so going live again is instant, but the encoder and the uplink rest. Needs a host
    /// that reports viewers (2.3.0+); on older hosts the count is unknown, so this never engages.
    /// </summary>
    public bool StandbyWithoutViewers
    {
        get => _standbyEnabled;
        set
        {
            if (_standbyEnabled == value)
                return;
            _standbyEnabled = value;
            Diag.Log($"broadcast: standby without viewers {(value ? "on" : "off")}");
            OnStandbyChanged();
        }
    }

    /// <summary>True while live and resting because nobody is watching.</summary>
    public bool IsStandby => _live && _standbyEnabled && _lounge.ClockSynced && ViewerCount == 0;

    /// <summary>People watching my stream right now (needs host 2.3.0; 0 otherwise).</summary>
    public int ViewerCount
    {
        get
        {
            lock (_viewers)
                return _viewers.Count;
        }
    }

    private int EffectiveBitrateKbps => _adaptiveEnabled ? _adaptive.Apply(_bitrateKbps) : _bitrateKbps;

    private void OnViewer(uint streamId, uint viewerId, string name, bool joined)
    {
        if (streamId != _streamId || !_live)
            return;
        bool changed;
        lock (_viewers)
            changed = joined ? _viewers.Add(viewerId) : _viewers.Remove(viewerId);
        if (!changed)
            return;
        if (!joined)
        {
            _congestion.Forget(viewerId);
            ForgetViewerQuality(viewerId);
        }
        Diag.Log($"broadcast: viewer {viewerId} ({name}) {(joined ? "joined" : "left")}; {ViewerCount} watching");
        if (_viewerSounds)
            SoundEffects.Play(joined ? SoundEffects.ViewerIn : SoundEffects.ViewerOut);
        ViewerChanged?.Invoke(name, joined);
        OnStandbyChanged();
    }

    private bool _wasStandby;

    /// <summary>Leaving standby: the first frame out must be a keyframe. Entering: tell the UI the numbers stopped.</summary>
    private void OnStandbyChanged()
    {
        var standby = IsStandby;
        if (standby == _wasStandby)
            return;
        _wasStandby = standby;
        if (!_live)
            return;
        if (standby)
        {
            Diag.Log("broadcast: standby (nobody watching), encoder and uplink resting");
            LastStats = LastStats with { Fps = 0, Kbps = 0, AudioKbps = 0, EncodeMs = 0, Viewers = 0, Standby = true, WorstViewerDelayMs = 0 };
            Post(() => StatsChanged?.Invoke(LastStats));
        }
        else
        {
            Diag.Log("broadcast: leaving standby, keyframe requested");
            Interlocked.Exchange(ref _keyframeRequested, 1);
            _gpuEncoder?.RequestKeyframe();
            ResetStats();
            LastStats = LastStats with { Standby = false, Viewers = ViewerCount };
            Post(() => StatsChanged?.Invoke(LastStats));
        }
    }

    private void ClearViewers()
    {
        lock (_viewers)
            _viewers.Clear();
        _congestion.Reset();
        ResetReduced();
    }

    private int _worstViewerDelayMs;

    /// <summary>Network thread. A viewer is late: that is the signal the adaptive ladder was missing.</summary>
    private void OnViewerReported(uint streamId, uint viewerId, int delayMs)
    {
        if (streamId != _streamId || !_live)
            return;
        Diag.Log($"broadcast: viewer {viewerId} reports {delayMs} ms of delay");
        InterlockedMax(ref _worstViewerDelayMs, delayMs);
        if (delayMs >= ViewerLagPolicy.ReportAboveMs)
            OnViewerStruggling(viewerId, $"{delayMs} ms late");
    }

    /// <summary>Network thread. A direct link fell behind and held video back for that viewer.</summary>
    private void OnDirectCongested(uint streamId, uint viewerId)
    {
        if (streamId == _streamId && _live)
            OnViewerStruggling(viewerId, "direct link behind");
    }

    private readonly ViewerCongestion _congestion = new();
    private long _lastSparedLogTicks;

    /// <summary>
    /// One viewer in trouble does not lower the bitrate for everyone: it takes at least half of
    /// them (see <see cref="ViewerCongestion"/>). My own uplink dropping frames still does, at once.
    /// </summary>
    private void OnViewerStruggling(uint viewerId, string why)
    {
        // A viewer on a direct link gets its own, lighter stream instead of costing everyone quality.
        if (TryReduceViewer(viewerId, why))
            return;
        var viewers = ViewerCount;
        if (_congestion.Complain(viewerId, Environment.TickCount64, viewers))
        {
            AdaptOnDrop();
            return;
        }
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastSparedLogTicks) >= 5000)
        {
            Interlocked.Exchange(ref _lastSparedLogTicks, now);
            Diag.Log($"broadcast: viewer {viewerId} struggling ({why}) but fewer than {ViewerCongestion.Needed(viewers)} of {viewers} are; bitrate kept");
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    /// <summary>Once per stats window: let the ladder climb back after a quiet spell.</summary>
    private void AdaptTick()
    {
        QualityTick();
        if (!_adaptiveEnabled || !_adaptive.OnTick(Environment.TickCount64))
            return;
        Diag.Log($"broadcast: uplink quiet, bitrate back up to {EffectiveBitrateKbps} kbps (level {_adaptive.Level})");
        _gpuEncoder?.SetBitrate(EffectiveBitrateKbps);
    }

    /// <summary>The uplink gate dropped a frame: step the ladder down, at most once per 1.5 s.</summary>
    private void AdaptOnDrop()
    {
        if (!_adaptiveEnabled || !_adaptive.OnDrop(Environment.TickCount64))
            return;
        Diag.Log($"broadcast: uplink congested, bitrate down to {EffectiveBitrateKbps} kbps (level {_adaptive.Level})");
        _gpuEncoder?.SetBitrate(EffectiveBitrateKbps);
    }

    public string EncoderPreferenceValue
    {
        get => _encoderPreference;
        set => _encoderPreference = EncoderPreference.Normalize(value);
    }

    public string AudioModeValue
    {
        get => _audioMode;
        set
        {
            _audioMode = AudioMode.Normalize(value);
            if (_live)
                RestartAudio();
        }
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
        AdaptiveQuality = settings.AdaptiveQuality;
        ViewerSounds = settings.ViewerSounds;
        StandbyWithoutViewers = settings.StandbyWithoutViewers;
        ShowCursor = settings.ShowCursor;
        EncoderPreferenceValue = settings.Encoder;
        MfCodecs.Tuning = new EncoderTuning(settings.ScreenContent, settings.VariableBitrate);
        _audioMode = AudioMode.Normalize(settings.AudioMode);
    }

    /// <summary>Starts (or switches) capture. Works both before and during a live session.</summary>
    public void SelectSource(CaptureSource source)
    {
        Diag.Log($"broadcast: source {source.Kind} '{source.Title}' {source.Width}x{source.Height}");
        if (!WaitForTeardown())
        {
            Post(() => Error?.Invoke(Loc.Get("Broadcast_StillStopping")));
            return;
        }
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
        if (_live)
            _audio.Retarget(_audioMode, source);
    }

    public void ClearSource()
    {
        if (!WaitForTeardown())
            return;
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

    /// <summary>Publishes the current source as a stream in the lounge.</summary>
    public async Task GoLiveAsync(string title, CancellationToken ct)
    {
        Diag.Log($"broadcast: go live '{title}' from {Source?.Kind} '{Source?.Title}' {Source?.Width}x{Source?.Height}, preset {_preset}, {_fps} fps, {_bitrateKbps} kbps, encoder pref {_encoderPreference}, audio {_audioMode}, cursor {_showCursor}, adaptive {_adaptiveEnabled}, standby {_standbyEnabled}");
        if (State != BroadcastState.Preview || Source is null)
            throw new InvalidOperationException("Pick something to share first.");
        if (!_lounge.IsConnected)
            throw new InvalidOperationException("Not in a lounge.");

        // Off the UI thread: the first go-live in a run test-encodes on the hardware encoder.
        var preference = _encoderPreference;
        var codec = await Task.Run(() => MfCodecs.Resolve(preference, Gpu), ct);
        var audioMode = AudioBroadcaster.Resolve(_audioMode, Source);
        var meta = new StreamMetaMessage
        {
            Title = title.Trim().Length == 0 ? Loc.Format("Stream_DefaultTitle", SettingsStore.Load().DisplayName) : title.Trim(),
            Codec = codec.ToWireName(),
            Fps = _fps,
            Audio = audioMode != AudioMode.Off && AudioBroadcaster.IsSupported ? "opus" : null,
            State = StreamStates.Live,
        };
        (meta.Width, meta.Height) = QualityPreset.Fit(_preset, Source.Width, Source.Height);

        var streamId = await _lounge.PublishAsync(meta, ct);

        lock (_sync)
        {
            if (State != BroadcastState.Preview || Source is null)
            {
                _lounge.Unpublish(streamId);
                throw new InvalidOperationException("Pick something to share first.");
            }

            ActiveCodec = codec;
            EncoderName = ActiveCodec == VideoCodec.Vp8 ? "libvpx (CPU)" : string.Empty;
            _streamId = streamId;
            _meta = meta;
            _paused = false;
            lock (_upstreamGate)
                _upstreamGate.RequestKeyframe();
            ResetStats();
            _adaptive.Reset(Environment.TickCount64);
            ClearViewers();
            _lounge.RegisterOwnStream(streamId, meta);

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
            Volatile.Write(ref _lastFrameTicks, Environment.TickCount64);
            _framesStalled = false;
            _watchdog?.Dispose();
            _watchdog = new System.Threading.Timer(_ => CheckFrames(), null, 5000, 2000);
            SetState(BroadcastState.Live);
            _wasStandby = false;
            OnStandbyChanged();
        }

        if (meta.Audio is not null)
            _audio.Start(_audioMode, Source);
    }

    /// <summary>
    /// Ends the broadcast. The state flips right away and the teardown (encoder, audio, capture)
    /// runs on its own thread: closing a hardware encoder can take seconds, or hang in the driver,
    /// and the window must not freeze with it. <see cref="WaitForTeardown"/> is what anything that
    /// needs the old session gone waits on.
    /// </summary>
    public void StopLive()
    {
        Diag.Log($"broadcast: stop live (state {State}, stream #{_streamId}, viewers {ViewerCount})");
        lock (_sync)
        {
            if (State != BroadcastState.Live)
                return;
            // Stops the capture thread from feeding the encoder before anything is disposed.
            _live = false;
            // Viewers learn now, not when the encoder finally lets go.
            if (_streamId != 0)
            {
                _lounge.Unpublish(_streamId);
                _lounge.ForgetOwnStream(_streamId);
                _streamId = 0;
            }
            ClearViewers();
            Source = null;
            SetState(BroadcastState.Idle);
            _teardown = Task.Run(TearDown);
        }
    }

    private void TearDown()
    {
        var started = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            StopLiveCore();
            Diag.Log("broadcast: stop, session closed");
            // Release the capture too: after "stop" nothing of the screen should be read any more,
            // and Windows only removes its capture border once the session is gone.
            _capture?.Stop();
        }
        _preview?.Clear();
        Diag.Log($"broadcast: stop finished in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");
    }

    /// <summary>
    /// Waits for a teardown started by <see cref="StopLive"/>. False means it is still running after
    /// <paramref name="seconds"/>, and the caller must give up instead of blocking on the same lock.
    /// </summary>
    private bool WaitForTeardown(int seconds = 8)
    {
        var teardown = _teardown;
        if (teardown is null || teardown.IsCompleted)
            return true;
        Diag.Log("broadcast: waiting for the previous stop to finish");
        if (teardown.Wait(TimeSpan.FromSeconds(seconds)))
            return true;
        Diag.Log($"broadcast: the previous stop has not finished in {seconds} s");
        return false;
    }

    /// <summary>
    /// The connection came back with a fresh member id: announce the same stream again so viewers
    /// find it under the same title, and start with a keyframe. Capture and encoder never stopped.
    /// </summary>
    private async Task RepublishAsync()
    {
        if (!_live)
            return;
        var meta = _meta;
        uint streamId;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            streamId = await _lounge.PublishAsync(meta, timeout.Token);
        }
        catch (Exception ex)
        {
            Diag.Log($"republish failed: {ex.Message}");
            Post(() =>
            {
                Error?.Invoke(ex.Message);
                StopLive();
            });
            return;
        }

        lock (_sync)
        {
            if (!_live)
            {
                _lounge.Unpublish(streamId);
                return;
            }
            _streamId = streamId;
            ClearViewers();
            lock (_upstreamGate)
                _upstreamGate.RequestKeyframe();
            _lounge.RegisterOwnStream(streamId, meta);
            Interlocked.Exchange(ref _keyframeRequested, 1);
        }
        Diag.Log($"republished as stream {streamId}");
    }

    public void SetPaused(bool paused)
    {
        if (State != BroadcastState.Live || _paused == paused)
            return;
        Diag.Log($"broadcast: {(paused ? "paused" : "resumed")}");
        _paused = paused;
        _meta = new StreamMetaMessage
        {
            Title = _meta.Title,
            Codec = _meta.Codec,
            Width = _meta.Width,
            Height = _meta.Height,
            Fps = _meta.Fps,
            Audio = _meta.Audio,
            State = paused ? StreamStates.Paused : StreamStates.Live,
        };
        _lounge.UpdateStreamMeta(_streamId, _meta);
        if (!paused)
            Interlocked.Exchange(ref _keyframeRequested, 1);
        Post(() => StateChanged?.Invoke(State));
    }

    public void Shutdown()
    {
        // On the way out a stuck driver is not worth waiting for: the process is about to end.
        if (!WaitForTeardown(3))
            return;
        lock (_sync)
        {
            if (State == BroadcastState.Live)
                StopLiveCore();
            _capture?.Dispose();
            _capture = null;
            Source = null;
            State = BroadcastState.Idle;
        }
        _audio.Dispose();
        _preview?.Dispose();
        _preview = null;
        DisposeGpuResources();
        _gpu?.Dispose();
        _gpu = null;
    }

    /// <summary>
    /// Off the UI thread, every couple of seconds: a live broadcast that is neither paused nor
    /// resting should be producing frames. Silence means the capture or the encoder is stuck, and
    /// the log is the only place that can say when it started.
    /// </summary>
    private void CheckFrames()
    {
        if (!_live || _paused || IsStandby)
        {
            _framesStalled = false;
            return;
        }
        if (_gpuEncoder is { LooksDead: true } dead && Interlocked.Exchange(ref _fallingBack, 1) == 0)
        {
            Diag.Log($"broadcast: hardware encoder never produced a frame ({dead.Describe()}), switching to VP8");
            MfCodecs.MarkBroken(dead.Codec, "silent while live");
            Post(() => _ = SwitchToVp8Async());
            return;
        }
        var quiet = Environment.TickCount64 - Volatile.Read(ref _lastFrameTicks);
        if (quiet < 5000)
        {
            if (_framesStalled)
            {
                _framesStalled = false;
                Diag.Log("broadcast: frames are coming again");
            }
            return;
        }
        if (_framesStalled)
            return;
        _framesStalled = true;
        Diag.Log($"broadcast: no encoded frame for {quiet} ms (codec {ActiveCodec}, {Source?.Kind} {Source?.Width}x{Source?.Height}, viewers {ViewerCount}, capture {(_capture?.Method is { Length: > 0 } m ? m : "?")})");
    }

    /// <summary>
    /// The hardware encoder took frames and never gave any back. Carry on with VP8 on the CPU: the
    /// capture keeps running, the stream is announced again with the new codec under the same title,
    /// and viewers follow it like they follow a publisher that reconnected.
    /// </summary>
    private async Task SwitchToVp8Async()
    {
        try
        {
            if (!_live || ActiveCodec == VideoCodec.Vp8)
                return;
            var old = _meta;
            var meta = new StreamMetaMessage
            {
                Title = old.Title,
                Codec = VideoCodec.Vp8.ToWireName(),
                Width = old.Width,
                Height = old.Height,
                Fps = old.Fps,
                Audio = old.Audio,
                State = old.State,
            };
            uint streamId;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                streamId = await _lounge.PublishAsync(meta, timeout.Token);
            }
            catch (Exception ex)
            {
                Diag.Log("broadcast: VP8 fallback could not publish: " + ex.Message);
                Error?.Invoke(ex.Message);
                StopLive();
                return;
            }

            MfVideoEncoder? encoder;
            lock (_sync)
            {
                if (!_live)
                {
                    _lounge.Unpublish(streamId);
                    return;
                }
                // The capture thread reads ActiveCodec on every frame, holding the context lock:
                // swapped under it, no frame can rebuild the hardware encoder halfway through.
                lock (Gpu.ContextLock)
                {
                    ActiveCodec = VideoCodec.Vp8;
                    encoder = _gpuEncoder;
                    _gpuEncoder = null;
                    DisposeReduced(); // no reduced stream on the CPU codec
                }
                EncoderName = "libvpx (CPU)";

                if (_streamId != 0)
                {
                    _lounge.Unpublish(_streamId);
                    _lounge.ForgetOwnStream(_streamId);
                }
                _streamId = streamId;
                _meta = meta;
                ClearViewers();
                lock (_upstreamGate)
                    _upstreamGate.RequestKeyframe();
                _lounge.RegisterOwnStream(streamId, meta);
                Interlocked.Exchange(ref _keyframeRequested, 1);
                Volatile.Write(ref _lastFrameTicks, Environment.TickCount64);
                _framesStalled = false;

                _vp8Cts = new CancellationTokenSource();
                var token = _vp8Cts.Token;
                _vp8Thread = new Thread(() => Vp8Loop(token))
                {
                    Name = "Beamcast VP8 encoder",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                };
                _vp8Thread.Start();
            }
            // A driver that never produced a frame may also hang on close; never on this thread.
            if (encoder is not null)
                _ = Task.Run(encoder.Dispose);
            Diag.Log($"broadcast: now on VP8 as stream #{streamId}");
            StateChanged?.Invoke(State);
        }
        finally
        {
            Interlocked.Exchange(ref _fallingBack, 0);
        }
    }

    private void StopLiveCore()
    {
        _live = false;
        _watchdog?.Dispose();
        _watchdog = null;
        _framesStalled = false;
        _audio.Stop();

        var cts = _vp8Cts;
        _vp8Cts = null;
        cts?.Cancel();
        _vp8Thread?.Join(TimeSpan.FromSeconds(3));
        _vp8Thread = null;
        cts?.Dispose();

        var encoder = _gpuEncoder;
        _gpuEncoder = null;
        encoder?.Dispose();
        DisposeReduced();

        if (_streamId != 0)
        {
            _lounge.Unpublish(_streamId);
            _lounge.ForgetOwnStream(_streamId);
            _streamId = 0;
        }
        ClearViewers();
        _paused = false;

        var stale = Interlocked.Exchange(ref _latest, null);
        if (stale is not null)
            ArrayPool<byte>.Shared.Return(stale.Pixels);

        LastStats = HostStats.Empty;
        Post(() => StatsChanged?.Invoke(LastStats));
    }

    private void RestartAudio()
    {
        if (!_live)
            return;
        var mode = AudioBroadcaster.Resolve(_audioMode, Source);
        if (mode == AudioMode.Off || !AudioBroadcaster.IsSupported)
            _audio.Stop();
        else
            _audio.Start(_audioMode, Source);
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
        Diag.Log("broadcast: capture faulted: " + ex);
        Post(() =>
        {
            Error?.Invoke(ex.Message);
            ClearSource();
        });
    }

    private void OnKeyframeRequested(uint streamId)
    {
        if (streamId != _streamId)
            return;
        Diag.Log("broadcast: keyframe needed");
        Interlocked.Exchange(ref _keyframeRequested, 1);
        _gpuEncoder?.RequestKeyframe();
    }

    /// <summary>Capture thread, context lock held by the caller.</summary>
    private void OnTexture(GpuFrame frame)
    {
        MaybePreview(frame);
        _calibration?.Offer(frame);

        if (_live && IsStandby != _wasStandby)
            OnStandbyChanged();
        if (!_live || _paused || IsStandby)
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

    private void DisposeGpuResources()
    {
        DisposeReduced();
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
        Diag.Log($"broadcast: state {State} -> {state}");
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
