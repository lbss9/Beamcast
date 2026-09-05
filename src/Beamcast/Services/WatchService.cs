using System.Diagnostics;
using Beamcast.Audio;
using Beamcast.Codec;
using Beamcast.Codec.Gpu;
using Beamcast.Net;
using Beamcast.Render;
using Microsoft.UI.Dispatching;

namespace Beamcast.Services;

public sealed record ViewerStats(double Fps, double Kbps, double AudioKbps, double DecodeMs, int Width, int Height);

/// <summary>
/// The stream this member is watching. Decoding and presentation happen on the network thread:
/// a packet arrives, the GPU decodes it, the swap chain shows it, the audio goes to WASAPI.
/// No hop through the UI thread, no intermediate copy.
/// </summary>
public sealed class WatchService
{
    public static WatchService Instance { get; } = new();

    private readonly LoungeService _lounge = LoungeService.Instance;
    private DispatcherQueue? _ui;
    private GpuDevice? _gpu;
    private SwapChainPresenter? _presenter;
    private MfVideoDecoder? _gpuDecoder;
    private Vp8Decoder? _vp8Decoder;
    private AudioPlayer? _audio;
    private VideoCodec _codec;
    private uint _streamId;
    private int _firstFrame;
    private float _volume = 1f;
    private bool _muted;

    private long _windowStart;
    private int _framesWindow;
    private long _bytesWindow;
    private long _audioBytesWindow;
    private double _decodeWindowMs;
    private int _width;
    private int _height;

    private WatchService()
    {
        _lounge.MediaReceived += OnMedia;
        _lounge.StreamEnded += id =>
        {
            if (id == _streamId)
                StopWatching("ended");
        };
        _lounge.StateChanged += state =>
        {
            if (state == LoungeState.Disconnected)
                StopWatching("left");
        };
    }

    public event Action<uint>? WatchingChanged;
    public event Action? FirstFrame;
    public event Action<ViewerStats>? StatsChanged;
    public event Action<string>? Stopped;

    /// <summary>Zero when not watching anything.</summary>
    public uint StreamId => _streamId;
    public bool IsWatching => _streamId != 0;
    public bool HasFrame => Volatile.Read(ref _firstFrame) != 0;
    public ViewerStats? LastStats { get; private set; }

    public GpuDevice Gpu => _gpu ??= new GpuDevice();

    public SwapChainPresenter Presenter => _presenter ??= new SwapChainPresenter(Gpu);

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_audio is not null)
                _audio.Volume = _volume;
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

    public void Initialize(DispatcherQueue ui) => _ui = ui;

    public void Watch(uint streamId)
    {
        var stream = _lounge.FindStream(streamId);
        if (stream is null || stream.IsMine)
            return;
        if (_streamId == streamId)
            return;

        StopWatching("switched", notify: false);
        VideoCodecs.TryParse(stream.Meta.Codec, out _codec);
        _streamId = streamId;
        Interlocked.Exchange(ref _firstFrame, 0);
        LastStats = null;
        _windowStart = Stopwatch.GetTimestamp();
        _lounge.Subscribe(streamId);
        Post(() => WatchingChanged?.Invoke(streamId));
    }

    public void StopWatching(string reason = "stopped", bool notify = true)
    {
        var streamId = Interlocked.Exchange(ref _streamId, 0);
        if (streamId == 0)
            return;
        if (_lounge.IsConnected)
            _lounge.Unsubscribe(streamId);
        DisposeDecoders();
        if (notify)
        {
            Post(() =>
            {
                WatchingChanged?.Invoke(0);
                Stopped?.Invoke(reason);
            });
        }
    }

    /// <summary>Network thread. Decodes and presents in place.</summary>
    private void OnMedia(uint streamId, MessageType type, bool keyframe, byte[] body)
    {
        if (streamId != _streamId)
            return;

        if (type == MessageType.Audio)
        {
            if (!AudioPacket.TryParse(body, out var audioHeader, out var opus))
                return;
            _audio ??= new AudioPlayer { Volume = _volume, IsMuted = _muted };
            _audio.Push(audioHeader, opus.Span);
            Interlocked.Add(ref _audioBytesWindow, body.Length);
            return;
        }

        if (type != MessageType.Video || !VideoPacket.TryParse(body, out var header, out var bitstream))
            return;

        var start = Stopwatch.GetTimestamp();
        var presenter = _presenter;
        if (_codec.IsGpu())
        {
            var decoder = _gpuDecoder;
            if (decoder is null || decoder.Codec != _codec)
            {
                decoder?.Dispose();
                try
                {
                    decoder = new MfVideoDecoder(Gpu, _codec, header.Width, header.Height);
                }
                catch (Exception)
                {
                    return;
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
                _lounge.RequestKeyframe(streamId);
                return;
            }

            if (picture is null)
                return;
            using (picture)
            {
                var width = Math.Min(header.Width, picture.Width);
                var height = Math.Min(header.Height, picture.Height);
                presenter?.Present(picture.Texture, picture.Subresource, width, height, true);
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
                _lounge.RequestKeyframe(streamId);
                return;
            }
            if (frame is null)
                return;
            presenter?.PresentPixels(frame.Bgra, frame.Width, frame.Height);
        }

        _width = header.Width;
        _height = header.Height;
        _framesWindow++;
        _bytesWindow += body.Length;
        _decodeWindowMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (Interlocked.Exchange(ref _firstFrame, 1) == 0)
            Post(() => FirstFrame?.Invoke());
        MaybePublishStats();
    }

    private void MaybePublishStats()
    {
        var elapsed = Stopwatch.GetElapsedTime(_windowStart);
        if (elapsed.TotalMilliseconds < 1000)
            return;
        var seconds = elapsed.TotalSeconds;
        var audioBytes = Interlocked.Exchange(ref _audioBytesWindow, 0);
        var stats = new ViewerStats(
            _framesWindow / seconds,
            _bytesWindow * 8 / 1000.0 / seconds,
            audioBytes * 8 / 1000.0 / seconds,
            _framesWindow > 0 ? _decodeWindowMs / _framesWindow : 0,
            _width,
            _height
        );
        _framesWindow = 0;
        _bytesWindow = 0;
        _decodeWindowMs = 0;
        _windowStart = Stopwatch.GetTimestamp();
        LastStats = stats;
        Post(() => StatsChanged?.Invoke(stats));
    }

    private void DisposeDecoders()
    {
        _gpuDecoder?.Dispose();
        _gpuDecoder = null;
        _vp8Decoder?.Dispose();
        _vp8Decoder = null;
        _audio?.Dispose();
        _audio = null;
        Interlocked.Exchange(ref _firstFrame, 0);
        _presenter?.Clear();
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
