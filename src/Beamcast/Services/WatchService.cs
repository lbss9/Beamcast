using System.Diagnostics;
using Beamcast.Codec;
using Beamcast.Codec.Gpu;
using Beamcast.Net;
using Beamcast.Render;
using Microsoft.UI.Dispatching;

namespace Beamcast.Services;

public enum WatchState
{
    Disconnected,
    Connecting,
    Watching,
}

/// <summary>
/// Process-wide viewer session so the stream survives page navigation. Decoding and presentation
/// happen on the network thread: a packet arrives, the GPU decodes it, the swap chain shows it.
/// No hop through the UI thread, no intermediate copy.
/// </summary>
public sealed class WatchService
{
    public static WatchService Instance { get; } = new();

    private DispatcherQueue? _ui;
    private GpuDevice? _gpu;
    private SwapChainPresenter? _presenter;
    private ViewerClient? _client;
    private MfVideoDecoder? _gpuDecoder;
    private Vp8Decoder? _vp8Decoder;
    private VideoCodec _codec;
    private int _firstFrame;

    private WatchService() { }

    public event Action<WatchState>? StateChanged;
    public event Action? FirstFrame;
    public event Action<IReadOnlyList<string>>? ViewersChanged;
    public event Action<ViewerStats>? StatsChanged;
    public event Action<string>? StreamStateChanged;
    public event Action<string>? Closed;

    public WatchState State { get; private set; }
    public WelcomeMessage? Welcome { get; private set; }
    public IReadOnlyList<string> Viewers { get; private set; } = [];
    public ViewerStats? LastStats { get; private set; }
    public string StreamState { get; private set; } = StreamStates.Live;
    public InviteTarget? Target { get; private set; }
    public bool HasFrame => Volatile.Read(ref _firstFrame) != 0;

    public GpuDevice Gpu => _gpu ??= new GpuDevice();

    public SwapChainPresenter Presenter => _presenter ??= new SwapChainPresenter(Gpu);

    public void Initialize(DispatcherQueue ui) => _ui = ui;

    public async Task<WelcomeMessage> ConnectAsync(InviteTarget target, string displayName, CancellationToken ct)
    {
        if (State != WatchState.Disconnected)
            throw new InvalidOperationException("Already connected.");

        SetState(WatchState.Connecting);
        var client = new ViewerClient();
        client.VideoReceived += OnVideo;
        client.ViewersChanged += viewers => Post(() =>
        {
            Viewers = viewers;
            ViewersChanged?.Invoke(viewers);
        });
        client.StatsUpdated += stats => Post(() =>
        {
            LastStats = stats;
            StatsChanged?.Invoke(stats);
        });
        client.StreamStateChanged += state => Post(() =>
        {
            StreamState = state;
            StreamStateChanged?.Invoke(state);
        });
        client.Closed += reason => Post(() => OnClosed(client, reason));

        try
        {
            var welcome = await client.ConnectAsync(target, displayName, ct);
            if (!VideoCodecs.TryParse(welcome.Codec, out var codec))
                throw new ConnectException("codec", "Unknown codec " + welcome.Codec);
            if (codec.IsGpu() && !MfCodecs.HasDecoder(codec))
                throw new ConnectException("codec", "This machine cannot decode " + welcome.Codec);

            _codec = codec;
            Interlocked.Exchange(ref _firstFrame, 0);
            _client = client;
            Welcome = welcome;
            Target = target;
            Viewers = welcome.Viewers;
            StreamState = welcome.State;
            LastStats = null;
            SetState(WatchState.Watching);
            return welcome;
        }
        catch
        {
            client.Dispose();
            SetState(WatchState.Disconnected);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        var client = _client;
        if (client is null)
            return;
        await client.DisconnectAsync();
    }

    private void OnClosed(ViewerClient client, string reason)
    {
        if (!ReferenceEquals(_client, client))
            return;
        _client = null;
        client.Dispose();
        DisposeDecoders();
        Welcome = null;
        Target = null;
        SetState(WatchState.Disconnected);
        Closed?.Invoke(reason);
    }

    /// <summary>Network thread. Decodes and presents in place.</summary>
    private double OnVideo(VideoPacketHeader header, ReadOnlyMemory<byte> bitstream)
    {
        var presenter = _presenter;
        var start = Stopwatch.GetTimestamp();

        if (_codec.IsGpu())
        {
            var decoder = _gpuDecoder;
            if (decoder is null || decoder.Codec != _codec)
            {
                decoder?.Dispose();
                decoder = new MfVideoDecoder(Gpu, _codec, header.Width, header.Height);
                _gpuDecoder = decoder;
            }

            DecodedTexture? picture;
            try
            {
                picture = decoder.Decode(bitstream.Span, header.TimestampMs);
            }
            catch (Exception)
            {
                // Corrupt or out-of-order data: rebuild the decoder and ask for a keyframe.
                _gpuDecoder?.Dispose();
                _gpuDecoder = null;
                _client?.RequestKeyframe();
                return 0;
            }

            if (picture is null)
                return 0;
            using (picture)
            {
                // The decoder's surface is padded to macroblock size (e.g. 1088 for 1080); show the
                // real picture area the host announced in the packet header.
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
                _client?.RequestKeyframe();
                return 0;
            }
            if (frame is null)
                return 0;
            presenter?.PresentPixels(frame.Bgra, frame.Width, frame.Height);
        }

        if (Interlocked.Exchange(ref _firstFrame, 1) == 0)
            Post(() => FirstFrame?.Invoke());
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void DisposeDecoders()
    {
        _gpuDecoder?.Dispose();
        _gpuDecoder = null;
        _vp8Decoder?.Dispose();
        _vp8Decoder = null;
        Interlocked.Exchange(ref _firstFrame, 0);
        _presenter?.Clear();
    }

    private void SetState(WatchState state)
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
