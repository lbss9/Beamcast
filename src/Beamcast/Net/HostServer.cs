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
    int MaxViewers,
    InviteKind Kind = InviteKind.Direct,
    string? Secret = null,
    string? RelayUrl = null,
    string? AppKey = null
)
{
    /// <summary>Without a secret the stream is plaintext and anyone reaching the port can watch; only for LAN tests.</summary>
    public bool IsSecure => !string.IsNullOrEmpty(Secret);
}

public sealed record ViewerInfo(Guid Id, string Name, string RemoteAddress, DateTimeOffset JoinedAt);

/// <summary>
/// The broadcaster's server. Two shapes, same handshake and message flow:
///
/// - Direct: a TCP listener; every viewer gets its own outbox with a keyframe gate, so one slow
///   connection never stalls the others.
/// - Relay: one WebSocket to the relay, which multiplexes the viewers. Broadcast messages are sent
///   once and fanned out by the relay (which runs the same gate per viewer); per-viewer messages
///   travel tagged with the viewer id.
///
/// With a secret, everything after the Challenge is end-to-end encrypted; the relay only ever sees
/// message types and the keyframe flag.
/// </summary>
public sealed class HostServer : IDisposable
{
    private const int MaxPendingFramesPerViewer = 4;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<Guid, ViewerConnection> _viewers = new();
    private readonly ConcurrentDictionary<uint, ViewerConnection> _relayViewers = new();
    private readonly ConcurrentDictionary<uint, RelayViewerTransport> _relayTransports = new();
    private TcpListener? _listener;
    private RelayHostLink? _relay;
    private FrameGate? _upstreamGate;
    private SecureChannel? _secure;
    private CancellationTokenSource? _cts;
    private HostOptions? _options;
    private volatile string _state = StreamStates.Live;
    private int _width;
    private int _height;
    private int _fps;
    private string _codec = "vp8";
    private string? _audio;
    private long _lastKeyframeRequestTicks;

    public event Action<ViewerInfo>? ViewerJoined;
    public event Action<ViewerInfo>? ViewerLeft;
    public event Action? KeyframeNeeded;
    public event Action<Exception>? Faulted;
    public event Action<string>? RelayClosed;

    public bool IsRunning => _listener is not null || _relay is not null;

    public int ViewerCount => _viewers.Count;

    public string? RoomCode => _relay?.Room;

    public IReadOnlyList<ViewerInfo> Viewers =>
        _viewers.Values.Select(v => v.Info).OrderBy(v => v.JoinedAt).ToList();

    public void SetStreamInfo(int width, int height, int fps, string codec, string? audio = null)
    {
        _width = width;
        _height = height;
        _fps = fps;
        _codec = codec;
        _audio = audio;
    }

    public void SetAudio(string? audio) => _audio = audio;

    public async Task StartAsync(HostOptions options, CancellationToken ct)
    {
        if (IsRunning)
            throw new InvalidOperationException("Server already running.");

        _options = options;
        _secure = options.IsSecure ? SecureChannel.FromSecret(options.Secret!) : null;
        _cts = new CancellationTokenSource();

        if (options.Kind == InviteKind.Relay)
        {
            if (!InviteCode.IsValidRelayUrl(options.RelayUrl))
                throw new InvalidOperationException("Relay address is not valid.");
            var relay = await RelayHostLink.ConnectAsync(options.RelayUrl!, options.AppKey, ct).ConfigureAwait(false);
            relay.ViewerJoined += OnRelayViewerJoined;
            relay.ViewerLeft += OnRelayViewerLeft;
            relay.DataReceived += OnRelayData;
            relay.Closed += OnRelayClosed;
            _upstreamGate = new FrameGate(MaxPendingFramesPerViewer);
            _relay = relay;
            relay.Start(_cts.Token);
            Diag.Log($"host: relay started, room {relay.Room}");
        }
        else
        {
            _listener = CreateListener(options.Port);
            _listener.Start();
            _ = AcceptLoopAsync(_listener, _cts.Token);
        }
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
        var bye = Frame(MessageType.Bye, ReadOnlySpan<byte>.Empty);
        foreach (var viewer in _viewers.Values)
            viewer.Close(bye);
        _viewers.Clear();
        _relayViewers.Clear();
        foreach (var transport in _relayTransports.Values)
            transport.Dispose();
        _relayTransports.Clear();

        var cts = _cts;
        _cts = null;
        cts?.Cancel();

        var listener = _listener;
        _listener = null;
        SafeTry.Run(() => listener?.Stop());

        var relay = _relay;
        _relay = null;
        _upstreamGate = null;
        relay?.Dispose();

        cts?.Dispose();
        _secure?.Dispose();
        _secure = null;
    }

    public void SetState(string state)
    {
        _state = state;
        BroadcastControl(MessageType.StreamState, Json.Serialize(new StreamStateMessage { State = state }));
    }

    /// <summary>Offers a frame to every viewer (direct) or once to the relay.</summary>
    public void Broadcast(EncodedFrame frame)
    {
        if (_viewers.IsEmpty && _relay is null)
            return;

        var header = new VideoPacketHeader(frame.Sequence, frame.TimestampMs, frame.Width, frame.Height, frame.IsKeyframe);
        var body = VideoPacket.Build(header, frame.Data);
        var framed = Frame(MessageType.Video, body, frame.IsKeyframe ? MessageFlags.Keyframe : MessageFlags.None);

        var needKeyframe = false;
        if (_relay is { } relay && _upstreamGate is { } gate)
        {
            GateDecision decision;
            lock (gate)
            {
                decision = gate.Offer(frame.IsKeyframe, relay.PendingBroadcastFrames);
            }
            if (decision == GateDecision.Send)
                relay.SendBroadcast(framed, isVideo: true);
            else if (decision == GateDecision.DropAndRequestKeyframe)
                needKeyframe = true;
        }
        else
        {
            foreach (var viewer in _viewers.Values)
            {
                if (viewer.OfferVideo(framed, frame.IsKeyframe))
                    needKeyframe = true;
            }
        }

        if (needKeyframe)
            RaiseKeyframeNeeded();
    }

    /// <summary>Sends an encoded audio packet to everyone. Audio is never gated; it is tiny.</summary>
    public void BroadcastAudio(byte[] audioPacket) => BroadcastControl(MessageType.Audio, audioPacket);

    private void BroadcastControl(MessageType type, byte[] payload)
    {
        var framed = Frame(type, payload);
        if (_relay is { } relay)
        {
            relay.SendBroadcast(framed, isVideo: false);
            return;
        }
        foreach (var viewer in _viewers.Values)
            viewer.EnqueueFramed(framed);
    }

    /// <summary>Frames a message, encrypting it when the session has a secret.</summary>
    private byte[] Frame(MessageType type, ReadOnlySpan<byte> payload, byte flags = MessageFlags.None) =>
        _secure is { } secure ? secure.Seal(type, payload, flags) : Framing.Encode(type, payload, flags);

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
                client.NoDelay = true;
                var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
                var transport = new StreamTransport(client.GetStream(), client);
                _ = HandshakeAsync(transport, remote, relayViewerId: null, ct);
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

    private void OnRelayViewerJoined(uint viewerId)
    {
        var relay = _relay;
        var cts = _cts;
        if (relay is null || cts is null)
            return;
        Diag.Log($"host: relay viewer {viewerId} joined");
        var transport = new RelayViewerTransport(relay, viewerId);
        _relayTransports[viewerId] = transport;
        _ = HandshakeAsync(transport, $"relay#{viewerId}", viewerId, cts.Token);
    }

    private void OnRelayViewerLeft(uint viewerId)
    {
        if (_relayTransports.TryRemove(viewerId, out var transport))
            transport.Dispose();
        if (_relayViewers.TryRemove(viewerId, out var viewer))
            viewer.Close(null);
    }

    private void OnRelayData(uint viewerId, Message message)
    {
        if (_relayTransports.TryGetValue(viewerId, out var transport))
            transport.Deliver(message);
        else
            Diag.Log($"host: data from unknown relay viewer {viewerId} type {message.Type}");
    }

    private void OnRelayClosed(string reason)
    {
        RelayClosed?.Invoke(reason);
    }

    private async Task HandshakeAsync(IMessageTransport transport, string remote, uint? relayViewerId, CancellationToken ct)
    {
        var options = _options;
        if (options is null)
        {
            transport.Dispose();
            return;
        }

        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(HandshakeTimeout);
            var hct = handshakeCts.Token;

            var nonce = AuthProof.NewNonce();
            var requiresPassword = !string.IsNullOrEmpty(options.Password);
            var challenge = new ChallengeMessage
            {
                Protocol = AppInfo.ProtocolVersion,
                Nonce = nonce,
                RequiresPassword = requiresPassword,
                RequiresSecret = _secure is not null,
            };
            await transport.WriteFramedAsync(Framing.Encode(MessageType.Challenge, Json.Serialize(challenge)), hct).ConfigureAwait(false);
            Diag.Log($"host: challenge sent to {remote}");

            var message = await transport.ReadAsync(hct).ConfigureAwait(false);
            Diag.Log($"host: handshake reply from {remote}: {(message is null ? "null" : message.Value.Type.ToString())}");
            if (message is null || message.Value.Type != MessageType.Hello)
            {
                transport.Dispose();
                return;
            }

            var helloBytes = message.Value.Payload;
            if (_secure is { } secure)
            {
                if (!secure.TryOpen(message.Value, out helloBytes))
                {
                    await RejectAsync(transport, RejectReasons.Secret, hct).ConfigureAwait(false);
                    transport.Dispose();
                    return;
                }
            }

            var hello = Json.Deserialize<HelloMessage>(helloBytes);
            if (hello is null || hello.Protocol != AppInfo.ProtocolVersion)
            {
                await RejectAsync(transport, RejectReasons.Version, hct).ConfigureAwait(false);
                transport.Dispose();
                return;
            }

            if (requiresPassword && !AuthProof.Verify(options.Password!, nonce, hello.Auth))
            {
                await RejectAsync(transport, RejectReasons.Password, hct).ConfigureAwait(false);
                transport.Dispose();
                return;
            }

            if (options.MaxViewers > 0 && _viewers.Count >= options.MaxViewers)
            {
                await RejectAsync(transport, RejectReasons.Full, hct).ConfigureAwait(false);
                transport.Dispose();
                return;
            }

            var name = SanitizeName(hello.Name);
            var info = new ViewerInfo(Guid.NewGuid(), name, remote, DateTimeOffset.Now);
            var viewer = new ViewerConnection(transport, info, MaxPendingFramesPerViewer, _secure, ct);
            viewer.KeyframeRequested += RaiseKeyframeNeeded;
            viewer.Closed += () => OnViewerClosed(viewer);
            _viewers[info.Id] = viewer;
            if (relayViewerId is { } id)
                _relayViewers[id] = viewer;

            var welcome = new WelcomeMessage
            {
                SessionName = options.SessionName,
                HostName = options.HostName,
                Codec = _codec,
                Width = _width,
                Height = _height,
                Fps = _fps,
                State = _state,
                Audio = _audio,
                Viewers = Viewers.Select(v => v.Name).ToList(),
            };
            await transport.WriteFramedAsync(Frame(MessageType.Welcome, Json.Serialize(welcome)), hct).ConfigureAwait(false);

            Diag.Log($"host: welcome sent to {remote} ({name})");
            viewer.Start();
            ViewerJoined?.Invoke(info);
            BroadcastViewerList();
            RaiseKeyframeNeeded();
        }
        catch (Exception ex)
        {
            Diag.Log($"host: handshake with {remote} failed: {ex.GetType().Name} {ex.Message}");
            transport.Dispose();
        }
    }

    private static string SanitizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return "Viewer";
        return trimmed.Length > 32 ? trimmed[..32] : trimmed;
    }

    /// <summary>Rejects are always plaintext: the viewer may not hold the secret, and needs to know why.</summary>
    private static Task RejectAsync(IMessageTransport transport, string reason, CancellationToken ct) =>
        transport.WriteFramedAsync(Framing.Encode(MessageType.Reject, Json.Serialize(new RejectMessage { Reason = reason })), ct);

    private void OnViewerClosed(ViewerConnection viewer)
    {
        if (!_viewers.TryRemove(viewer.Info.Id, out _))
            return;
        foreach (var pair in _relayViewers.Where(p => ReferenceEquals(p.Value, viewer)).ToList())
        {
            _relayViewers.TryRemove(pair.Key, out _);
            if (_relayTransports.TryRemove(pair.Key, out var transport))
                transport.Dispose();
        }
        ViewerLeft?.Invoke(viewer.Info);
        BroadcastViewerList();
    }

    private void BroadcastViewerList() =>
        BroadcastControl(MessageType.Viewers, Json.Serialize(new ViewersMessage { Viewers = Viewers.Select(v => v.Name).ToList() }));

    public void Dispose() => Stop();

    /// <summary>One connected viewer: an outbox with the frame gate, plus a reader for control messages.</summary>
    private sealed class ViewerConnection
    {
        private readonly IMessageTransport _transport;
        private readonly Channel<(byte[] Bytes, bool IsVideo)> _outbox;
        private readonly FrameGate _gate;
        private readonly object _gateLock = new();
        private readonly SecureChannel? _secure;
        private readonly CancellationTokenSource _cts;
        private int _pendingVideo;
        private int _closed;

        public ViewerConnection(IMessageTransport transport, ViewerInfo info, int maxPending, SecureChannel? secure, CancellationToken parent)
        {
            _transport = transport;
            Info = info;
            _secure = secure;
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

        public void EnqueueFramed(byte[] framed) => _outbox.Writer.TryWrite((framed, false));

        private async Task SendLoopAsync(CancellationToken ct)
        {
            try
            {
                var reader = _outbox.Reader;
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var item))
                    {
                        await _transport.WriteFramedAsync(item.Bytes, ct).ConfigureAwait(false);
                        if (item.IsVideo)
                            Interlocked.Decrement(ref _pendingVideo);
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                Close(null);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var message = await _transport.ReadAsync(ct).ConfigureAwait(false);
                    if (message is null)
                        break;

                    var m = message.Value;
                    byte[] payload = m.Payload;
                    if (m.IsEncrypted)
                    {
                        if (_secure is null || !_secure.TryOpen(m, out payload))
                            continue;
                    }
                    else if (_secure is not null && m.Type != MessageType.KeyframeRequest && m.Type != MessageType.Bye)
                    {
                        // Only the relay's synthesized keyframe requests may arrive in the clear.
                        continue;
                    }

                    switch (m.Type)
                    {
                        case MessageType.Ping:
                            EnqueueFramed(_secure is { } s ? s.Seal(MessageType.Pong, payload) : Framing.Encode(MessageType.Pong, payload));
                            break;
                        case MessageType.KeyframeRequest:
                            lock (_gateLock)
                            {
                                _gate.RequestKeyframe();
                            }
                            KeyframeRequested?.Invoke();
                            break;
                        case MessageType.Bye:
                            Close(null);
                            return;
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                Close(null);
            }
        }

        public void Close(byte[]? bye)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
                return;

            if (bye is not null)
            {
                SafeTry.Run(() =>
                {
                    using var timeout = new CancellationTokenSource(500);
                    _transport.WriteFramedAsync(bye, timeout.Token).Wait(timeout.Token);
                });
            }

            _outbox.Writer.TryComplete();
            SafeTry.Run(() => _cts.Cancel());
            SafeTry.Run(() => _transport.Dispose());
            _cts.Dispose();
            Closed?.Invoke();
        }
    }
}
