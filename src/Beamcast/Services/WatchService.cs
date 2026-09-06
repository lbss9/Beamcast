using System.Collections.Concurrent;
using System.Diagnostics;
using Beamcast.Audio;
using Beamcast.Codec;
using Beamcast.Codec.Gpu;
using Beamcast.Net;
using Beamcast.Render;
using Microsoft.UI.Dispatching;

namespace Beamcast.Services;

/// <param name="LatencyMs">Glass-to-glass delay estimate from the publisher's send stamp; negative when unknown (old host or clocks not synced yet).</param>
public sealed record ViewerStats(double Fps, double Kbps, double AudioKbps, double DecodeMs, int Width, int Height, double LatencyMs);

/// <summary>
/// The streams this member is watching, any number at once. Each one has its own decoder,
/// presenter and audio player. Decoding and presentation happen on the network thread: a packet
/// arrives, the GPU decodes it, the swap chain shows it, the audio goes to WASAPI. No hop through
/// the UI thread, no intermediate copy.
/// </summary>
public sealed class WatchService
{
    public static WatchService Instance { get; } = new();

    private readonly LoungeService _lounge = LoungeService.Instance;
    private readonly ConcurrentDictionary<uint, Viewer> _viewers = new();
    private DispatcherQueue? _ui;
    private GpuDevice? _gpu;
    private float _volume = 1f;
    private bool _muted;

    private WatchService()
    {
        _lounge.MediaReceived += OnMedia;
        _lounge.StreamEnded += id => StopWatching(id, "ended");
        _lounge.StateChanged += state =>
        {
            if (state == LoungeState.Disconnected)
                StopAll("left");
        };
        _lounge.Reconnected += OnReconnected;
    }

    /// <summary>The set of watched streams changed (UI thread).</summary>
    public event Action? WatchingChanged;
    public event Action<uint>? FirstFrame;
    public event Action<uint, ViewerStats>? StatsChanged;
    /// <summary>A stream stopped being watched: id and reason (stopped, ended, left, switched).</summary>
    public event Action<uint, string>? Stopped;

    public bool IsWatching => !_viewers.IsEmpty;
    public bool IsWatchingStream(uint streamId) => _viewers.ContainsKey(streamId);

    /// <summary>Ids of the streams being watched, oldest first.</summary>
    public IReadOnlyList<uint> Watching => _viewers.Values.OrderBy(v => v.Order).Select(v => v.Id).ToList();

    public bool HasFrame(uint streamId) => _viewers.TryGetValue(streamId, out var v) && v.HasFrame;
    public ViewerStats? LastStats(uint streamId) => _viewers.TryGetValue(streamId, out var v) ? v.LastStats : null;
    public SwapChainPresenter? PresenterFor(uint streamId) => _viewers.TryGetValue(streamId, out var v) ? v.Presenter : null;

    public GpuDevice Gpu => _gpu ??= new GpuDevice();

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            foreach (var viewer in _viewers.Values)
                viewer.Volume = _volume;
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            foreach (var viewer in _viewers.Values)
                viewer.IsMuted = value;
        }
    }

    public void Initialize(DispatcherQueue ui) => _ui = ui;

    private int _order;

    public void Watch(uint streamId)
    {
        var stream = _lounge.FindStream(streamId);
        if (stream is null || stream.IsMine || _viewers.ContainsKey(streamId))
            return;
        VideoCodecs.TryParse(stream.Meta.Codec, out var codec);
        var viewer = new Viewer(streamId, codec, stream.OwnerName, stream.Meta.Title, Gpu, Interlocked.Increment(ref _order))
        {
            Volume = _volume,
            IsMuted = _muted,
        };
        if (!_viewers.TryAdd(streamId, viewer))
        {
            viewer.Dispose();
            return;
        }
        _lounge.Subscribe(streamId);
        Post(() => WatchingChanged?.Invoke());
    }

    public void StopWatching(uint streamId, string reason = "stopped")
    {
        if (!_viewers.TryRemove(streamId, out var viewer))
            return;
        if (_lounge.IsConnected)
            _lounge.Unsubscribe(streamId);
        viewer.Dispose();
        Post(() =>
        {
            WatchingChanged?.Invoke();
            Stopped?.Invoke(streamId, reason);
        });
    }

    public void StopAll(string reason = "stopped")
    {
        foreach (var id in _viewers.Keys.ToList())
            StopWatching(id, reason);
    }

    /// <summary>Stream ids are new after a reconnect; follow each stream with the same owner and title.</summary>
    private void OnReconnected()
    {
        var old = _viewers.Values.OrderBy(v => v.Order).ToList();
        foreach (var viewer in old)
        {
            _viewers.TryRemove(viewer.Id, out _);
            var again = _lounge.FindStreamLike(viewer.OwnerName, viewer.Title);
            if (again is null || _viewers.ContainsKey(again.Id))
            {
                viewer.Dispose();
                var lost = viewer.Id;
                Post(() => Stopped?.Invoke(lost, "ended"));
                continue;
            }
            VideoCodecs.TryParse(again.Meta.Codec, out var codec);
            var fresh = viewer.Rebind(again.Id, codec);
            _viewers[again.Id] = fresh;
            _lounge.Subscribe(again.Id);
        }
        Post(() => WatchingChanged?.Invoke());
    }

    /// <summary>Network thread. Decodes and presents in place.</summary>
    private void OnMedia(uint streamId, MessageType type, bool keyframe, byte[] body, uint sendStamp)
    {
        if (!_viewers.TryGetValue(streamId, out var viewer))
            return;

        if (type == MessageType.Audio)
        {
            viewer.OnAudio(body);
            return;
        }
        if (type != MessageType.Video)
            return;

        var latency = -1.0;
        if (sendStamp != 0)
        {
            var now = _lounge.ServerClockNow();
            if (now != 0)
                latency = LoungeMux.ClockDelta(now, sendStamp);
        }

        var outcome = viewer.OnVideo(body, latency);
        if (outcome == Viewer.VideoOutcome.NeedKeyframe)
            _lounge.RequestKeyframe(streamId);
        else if (outcome == Viewer.VideoOutcome.FirstFrame)
            Post(() => FirstFrame?.Invoke(viewer.Id));
        if (viewer.TakeStats() is { } stats)
            Post(() => StatsChanged?.Invoke(viewer.Id, stats));
    }

    private void Post(Action action)
    {
        var ui = _ui;
        if (ui is null || ui.HasThreadAccess)
            action();
        else
            ui.TryEnqueue(() => action());
    }

    /// <summary>One watched stream: decoder, presenter, audio, stats. Touched by the network thread only, except the volume knobs.</summary>
    private sealed class Viewer : IDisposable
    {
        public enum VideoOutcome { Presented, FirstFrame, Skipped, NeedKeyframe }

        private readonly GpuDevice _gpu;
        private MfVideoDecoder? _gpuDecoder;
        private Vp8Decoder? _vp8Decoder;
        private AudioPlayer? _audio;
        private VideoCodec _codec;
        private int _firstFrame;
        private float _volume = 1f;
        private bool _muted;

        private long _windowStart;
        private int _framesWindow;
        private long _bytesWindow;
        private long _audioBytesWindow;
        private double _decodeWindowMs;
        private double _latencyWindowMs;
        private int _latencySamples;
        private double _latencyEma = -1;
        private int _width;
        private int _height;

        public Viewer(uint id, VideoCodec codec, string ownerName, string title, GpuDevice gpu, int order)
        {
            Id = id;
            _codec = codec;
            OwnerName = ownerName;
            Title = title;
            _gpu = gpu;
            Order = order;
            Presenter = new SwapChainPresenter(gpu);
            _windowStart = Stopwatch.GetTimestamp();
        }

        public uint Id { get; private set; }
        public string OwnerName { get; }
        public string Title { get; }
        public int Order { get; }
        public SwapChainPresenter Presenter { get; }
        public bool HasFrame => Volatile.Read(ref _firstFrame) != 0;
        public ViewerStats? LastStats { get; private set; }

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = value;
                if (_audio is not null)
                    _audio.Volume = value;
            }
        }

        public bool IsMuted
        {
            get => _muted;
            set
            {
                _muted = value;
                if (_audio is not null)
                    _audio.IsMuted = value;
            }
        }

        /// <summary>After a reconnect: same presenter and view, new stream id, fresh decoders.</summary>
        public Viewer Rebind(uint newId, VideoCodec codec)
        {
            DisposeDecoders(clearScreen: false);
            Id = newId;
            _codec = codec;
            _windowStart = Stopwatch.GetTimestamp();
            return this;
        }

        public void OnAudio(byte[] body)
        {
            if (!AudioPacket.TryParse(body, out var header, out var opus))
                return;
            _audio ??= new AudioPlayer { Volume = _volume, IsMuted = _muted };
            _audio.Push(header, opus.Span);
            Interlocked.Add(ref _audioBytesWindow, body.Length);
        }

        public VideoOutcome OnVideo(byte[] body, double latencyMs)
        {
            if (!VideoPacket.TryParse(body, out var header, out var bitstream))
                return VideoOutcome.Skipped;

            var start = Stopwatch.GetTimestamp();
            if (_codec.IsGpu())
            {
                var decoder = _gpuDecoder;
                if (decoder is null || decoder.Codec != _codec)
                {
                    decoder?.Dispose();
                    try
                    {
                        decoder = new MfVideoDecoder(_gpu, _codec, header.Width, header.Height);
                    }
                    catch (Exception)
                    {
                        return VideoOutcome.Skipped;
                    }
                    _gpuDecoder = decoder;
                }

                DecodedTexture? picture;
                try
                {
                    picture = decoder.Decode(bitstream.Span, header.TimestampMs);
                }
                catch (Exception)
                {
                    _gpuDecoder?.Dispose();
                    _gpuDecoder = null;
                    return VideoOutcome.NeedKeyframe;
                }
                if (picture is null)
                    return VideoOutcome.Skipped;
                using (picture)
                {
                    var width = Math.Min(header.Width, picture.Width);
                    var height = Math.Min(header.Height, picture.Height);
                    Presenter.Present(picture.Texture, picture.Subresource, width, height, true);
                }
            }
            else
            {
                _vp8Decoder ??= new Vp8Decoder();
                DecodedFrame? frame;
                try
                {
                    frame = _vp8Decoder.Decode(bitstream.Span);
                }
                catch (Exception)
                {
                    return VideoOutcome.NeedKeyframe;
                }
                if (frame is null)
                    return VideoOutcome.Skipped;
                Presenter.PresentPixels(frame.Bgra, frame.Width, frame.Height);
            }

            _width = header.Width;
            _height = header.Height;
            _framesWindow++;
            _bytesWindow += body.Length;
            _decodeWindowMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (latencyMs >= 0)
            {
                _latencyWindowMs += latencyMs;
                _latencySamples++;
            }
            return Interlocked.Exchange(ref _firstFrame, 1) == 0 ? VideoOutcome.FirstFrame : VideoOutcome.Presented;
        }

        /// <summary>Once a second: the stats for the window that just closed, else null.</summary>
        public ViewerStats? TakeStats()
        {
            var elapsed = Stopwatch.GetElapsedTime(_windowStart);
            if (elapsed.TotalMilliseconds < 1000)
                return null;
            var seconds = elapsed.TotalSeconds;
            var audioBytes = Interlocked.Exchange(ref _audioBytesWindow, 0);
            if (_latencySamples > 0)
            {
                var average = _latencyWindowMs / _latencySamples;
                _latencyEma = _latencyEma < 0 ? average : _latencyEma * 0.5 + average * 0.5;
            }
            var stats = new ViewerStats(
                _framesWindow / seconds,
                _bytesWindow * 8 / 1000.0 / seconds,
                audioBytes * 8 / 1000.0 / seconds,
                _framesWindow > 0 ? _decodeWindowMs / _framesWindow : 0,
                _width,
                _height,
                _latencyEma
            );
            _framesWindow = 0;
            _bytesWindow = 0;
            _decodeWindowMs = 0;
            _latencyWindowMs = 0;
            _latencySamples = 0;
            _windowStart = Stopwatch.GetTimestamp();
            LastStats = stats;
            return stats;
        }

        private void DisposeDecoders(bool clearScreen)
        {
            _gpuDecoder?.Dispose();
            _gpuDecoder = null;
            _vp8Decoder?.Dispose();
            _vp8Decoder = null;
            _audio?.Dispose();
            _audio = null;
            Interlocked.Exchange(ref _firstFrame, 0);
            if (clearScreen)
                Presenter.Clear();
        }

        public void Dispose()
        {
            DisposeDecoders(clearScreen: true);
            Presenter.Detach();
            Presenter.Dispose();
        }
    }
}
