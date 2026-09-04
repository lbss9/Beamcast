using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Beamcast.Codec;

namespace Beamcast.Net;

public sealed record HostOptions(
    int Port,
    string? Password,
    string SessionName,
    string HostName,
    int MaxViewers
);

public sealed record ViewerInfo(Guid Id, string Name, string RemoteAddress, DateTimeOffset JoinedAt);

/// <summary>
/// TCP server the broadcaster runs. Accepts viewers, performs the challenge/response handshake and
/// fans every encoded frame out to each viewer through its own outbox, so one slow connection
/// never stalls the others.
/// </summary>
public sealed class HostServer : IDisposable
{
    private const int MaxPendingFramesPerViewer = 4;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Guid, ViewerConnection> _viewers = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private HostOptions? _options;
    private volatile string _state = StreamStates.Live;
    private int _width;
    private int _height;
    private int _fps;
    private long _lastKeyframeRequestTicks;

    public event Action<ViewerInfo>? ViewerJoined;
    public event Action<ViewerInfo>? ViewerLeft;
    public event Action? KeyframeNeeded;
    public event Action<Exception>? Faulted;

    public bool IsRunning => _listener is not null;

    public int ViewerCount => _viewers.Count;

    public IReadOnlyList<ViewerInfo> Viewers =>
        _viewers.Values.Select(v => v.Info).OrderBy(v => v.JoinedAt).ToList();

    public void SetStreamInfo(int width, int height, int fps)
    {
        _width = width;
        _height = height;
        _fps = fps;
    }

    public void Start(HostOptions options)
    {
        if (_listener is not null)
            throw new InvalidOperationException("Server already running.");

        _options = options;
        _cts = new CancellationTokenSource();
        _listener = CreateListener(options.Port);
        _listener.Start();
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    private static TcpListener CreateListener(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.IPv6Any, port);
            listener.Server.DualMode = true;
            return listener;
        }
        catch (SocketException)
        {
            return new TcpListener(IPAddress.Any, port);
        }
    }

    public void Stop()
    {
        // Say goodbye while the connections are still alive, then tear everything down.
        foreach (var viewer in _viewers.Values)
            viewer.Close(sendBye: true);
        _viewers.Clear();

        var cts = _cts;
        _cts = null;
        cts?.Cancel();

        var listener = _listener;
        _listener = null;
        SafeTry.Run(() => listener?.Stop());
        cts?.Dispose();
    }

    public void SetState(string state)
    {
        _state = state;
        var payload = Json.Serialize(new StreamStateMessage { State = state });
        foreach (var viewer in _viewers.Values)
            viewer.EnqueueControl(MessageType.StreamState, payload);
    }

    /// <summary>Offers a frame to every viewer. Returns true if at least one viewer wants a keyframe.</summary>
    public void Broadcast(EncodedFrame frame)
    {
        if (_viewers.IsEmpty)
            return;

        var header = new VideoPacketHeader(frame.Sequence, frame.TimestampMs, frame.Width, frame.Height, frame.IsKeyframe);
        var body = VideoPacket.Build(header, frame.Data);
        var framed = Framing.Encode(MessageType.Video, body);

        var needKeyframe = false;
        foreach (var viewer in _viewers.Values)
        {
            if (viewer.OfferVideo(framed, frame.IsKeyframe))
                needKeyframe = true;
        }

        if (needKeyframe)
            RaiseKeyframeNeeded();
    }

    private void RaiseKeyframeNeeded()
    {
        // Coalesce bursts of requests from several viewers into one keyframe.
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastKeyframeRequestTicks);
        if (now - last < 250)
            return;
        Interlocked.Exchange(ref _lastKeyframeRequestTicks, now);
        KeyframeNeeded?.Invoke();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Faulted?.Invoke(ex);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var options = _options;
        if (options is null)
        {
            client.Dispose();
            return;
        }

        client.NoDelay = true;
        var stream = client.GetStream();
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";

        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(HandshakeTimeout);
            var hct = handshakeCts.Token;

            var nonce = AuthProof.NewNonce();
            var requiresPassword = !string.IsNullOrEmpty(options.Password);
            await MessageStream.WriteJsonAsync(
                stream,
                MessageType.Challenge,
                new ChallengeMessage { Protocol = AppInfo.ProtocolVersion, Nonce = nonce, RequiresPassword = requiresPassword },
                hct
            ).ConfigureAwait(false);

            var message = await MessageStream.ReadAsync(stream, hct).ConfigureAwait(false);
            if (message is null || message.Value.Type != MessageType.Hello)
            {
                client.Dispose();
                return;
            }

            var hello = Json.Deserialize<HelloMessage>(message.Value.Payload);
            if (hello is null || hello.Protocol != AppInfo.ProtocolVersion)
            {
                await RejectAsync(stream, RejectReasons.Version, hct).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            if (requiresPassword && !AuthProof.Verify(options.Password!, nonce, hello.Auth))
            {
                await RejectAsync(stream, RejectReasons.Password, hct).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            if (_viewers.Count >= options.MaxViewers)
            {
                await RejectAsync(stream, RejectReasons.Full, hct).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            var name = SanitizeName(hello.Name);
            var info = new ViewerInfo(Guid.NewGuid(), name, remote, DateTimeOffset.Now);
            var viewer = new ViewerConnection(client, stream, info, MaxPendingFramesPerViewer, ct);
            viewer.KeyframeRequested += RaiseKeyframeNeeded;
            viewer.Closed += () => OnViewerClosed(viewer);
            _viewers[info.Id] = viewer;

            var welcome = new WelcomeMessage
            {
                SessionName = options.SessionName,
                HostName = options.HostName,
                Width = _width,
                Height = _height,
                Fps = _fps,
                State = _state,
                Viewers = Viewers.Select(v => v.Name).ToList(),
            };
            await MessageStream.WriteJsonAsync(stream, MessageType.Welcome, welcome, hct).ConfigureAwait(false);

            viewer.Start();
            ViewerJoined?.Invoke(info);
            BroadcastViewerList();
            RaiseKeyframeNeeded();
        }
        catch (Exception)
        {
            client.Dispose();
        }
    }

    private static string SanitizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return "Viewer";
        return trimmed.Length > 32 ? trimmed[..32] : trimmed;
    }

    private static Task RejectAsync(Stream stream, string reason, CancellationToken ct) =>
        MessageStream.WriteJsonAsync(stream, MessageType.Reject, new RejectMessage { Reason = reason }, ct);

    private void OnViewerClosed(ViewerConnection viewer)
    {
        if (!_viewers.TryRemove(viewer.Info.Id, out _))
            return;
        ViewerLeft?.Invoke(viewer.Info);
        BroadcastViewerList();
    }

    private void BroadcastViewerList()
    {
        var payload = Json.Serialize(new ViewersMessage { Viewers = Viewers.Select(v => v.Name).ToList() });
        foreach (var viewer in _viewers.Values)
            viewer.EnqueueControl(MessageType.Viewers, payload);
    }

    public void Dispose() => Stop();

    /// <summary>One connected viewer: an outbox with the frame gate, plus a reader for control messages.</summary>
    private sealed class ViewerConnection
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly Channel<(byte[] Bytes, bool IsVideo)> _outbox;
        private readonly FrameGate _gate;
        private readonly object _gateLock = new();
        private readonly CancellationTokenSource _cts;
        private int _pendingVideo;
        private int _closed;

        public ViewerConnection(TcpClient client, NetworkStream stream, ViewerInfo info, int maxPending, CancellationToken parent)
        {
            _client = client;
            _stream = stream;
            Info = info;
            _gate = new FrameGate(maxPending);
            _outbox = Channel.CreateUnbounded<(byte[], bool)>(new UnboundedChannelOptions { SingleReader = true });
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        }

        public ViewerInfo Info { get; }

        public event Action? KeyframeRequested;
        public event Action? Closed;

        public void Start()
        {
            _ = SendLoopAsync(_cts.Token);
            _ = ReceiveLoopAsync(_cts.Token);
        }

        /// <summary>Returns true when this viewer needs a keyframe from the encoder.</summary>
        public bool OfferVideo(byte[] framed, bool isKeyframe)
        {
            GateDecision decision;
            lock (_gateLock)
            {
                decision = _gate.Offer(isKeyframe, Volatile.Read(ref _pendingVideo));
            }

            switch (decision)
            {
                case GateDecision.Send:
                    Interlocked.Increment(ref _pendingVideo);
                    _outbox.Writer.TryWrite((framed, true));
                    return false;
                case GateDecision.DropAndRequestKeyframe:
                    return true;
                default:
                    return false;
            }
        }

        public void EnqueueControl(MessageType type, byte[] payload) =>
            _outbox.Writer.TryWrite((Framing.Encode(type, payload), false));

        private async Task SendLoopAsync(CancellationToken ct)
        {
            try
            {
                var reader = _outbox.Reader;
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var item))
                    {
                        await _stream.WriteAsync(item.Bytes, ct).ConfigureAwait(false);
                        if (item.IsVideo)
                            Interlocked.Decrement(ref _pendingVideo);
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                Close(sendBye: false);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var message = await MessageStream.ReadAsync(_stream, ct).ConfigureAwait(false);
                    if (message is null)
                        break;

                    switch (message.Value.Type)
                    {
                        case MessageType.Ping:
                            EnqueueControl(MessageType.Pong, message.Value.Payload);
                            break;
                        case MessageType.KeyframeRequest:
                            lock (_gateLock)
                            {
                                _gate.RequestKeyframe();
                            }
                            KeyframeRequested?.Invoke();
                            break;
                        case MessageType.Bye:
                            Close(sendBye: false);
                            return;
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                Close(sendBye: false);
            }
        }

        public void Close(bool sendBye)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;

            if (sendBye)
            {
                SafeTry.Run(() =>
                {
                    _stream.Write(Framing.Encode(MessageType.Bye, ReadOnlySpan<byte>.Empty));
                    _stream.Flush();
                });
            }

            _outbox.Writer.TryComplete();
            SafeTry.Run(() => _cts.Cancel());
            SafeTry.Run(() => _client.Dispose());
            _cts.Dispose();
            Closed?.Invoke();
        }
    }
}
