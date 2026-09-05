using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Beamcast.Net;

public sealed class ConnectException : Exception
{
    public ConnectException(string reason, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>One of <see cref="RejectReasons"/>, a relay reason, or "unreachable" / "timeout" / "protocol" / "codec".</summary>
    public string Reason { get; }
}

public sealed record ViewerStats(double Fps, double Kbps, double AudioKbps, double RttMs, double DecodeMs, int Width, int Height, long FramesReceived);

/// <summary>
/// Connects to a host (directly over TCP or through a relay room), completes the handshake and
/// delivers compressed frames to <see cref="VideoReceived"/> on the receive thread. Decoding is
/// the consumer's job so the GPU decoder and the presenter can run without any extra hop.
/// </summary>
public sealed class ViewerClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);

    private IMessageTransport? _transport;
    private SecureChannel? _secure;
    private CancellationTokenSource? _cts;
    private int _closed;

    private long _bytesWindow;
    private long _audioBytesWindow;
    private int _framesWindow;
    private double _decodeWindowMs;
    private long _windowStart;
    private long _framesReceived;
    private double _rttMs;
    private int _width;
    private int _height;

    /// <summary>Called for each frame with its header and Annex B / VP8 payload; returns the decode time in ms.</summary>
    public event Func<VideoPacketHeader, ReadOnlyMemory<byte>, double>? VideoReceived;
    public event Action<AudioPacketHeader, ReadOnlyMemory<byte>>? AudioReceived;
    public event Action<IReadOnlyList<string>>? ViewersChanged;
    public event Action<string>? StreamStateChanged;
    public event Action<ViewerStats>? StatsUpdated;
    public event Action<string>? Closed;

    public WelcomeMessage? Welcome { get; private set; }

    public bool IsConnected => _transport is not null && Interlocked.CompareExchange(ref _closed, 0, 0) == 0;

    public async Task<WelcomeMessage> ConnectAsync(InviteTarget target, string displayName, CancellationToken ct)
    {
        if (_transport is not null)
            throw new InvalidOperationException("Already connected.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConnectTimeout);

        var transport = target.Kind == InviteKind.Relay
            ? await ConnectRelayAsync(target, displayName, timeoutCts.Token, ct).ConfigureAwait(false)
            : await ConnectTcpAsync(target, timeoutCts.Token, ct).ConfigureAwait(false);

        try
        {
            var first = await transport.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
            var challenge = first?.Type == MessageType.Challenge
                ? Json.Deserialize<ChallengeMessage>(first.Value.Payload)
                : null;
            if (challenge is null)
                throw new ConnectException("protocol", "The host did not answer with a Beamcast handshake.");
            if (challenge.Protocol != AppInfo.ProtocolVersion)
                throw new ConnectException(RejectReasons.Version, "Protocol version mismatch.");
            if (challenge.RequiresSecret && !target.HasSecret)
                throw new ConnectException(RejectReasons.Secret, "This stream needs the full invite code.");
            if (challenge.RequiresPassword && !target.HasPassword)
                throw new ConnectException(RejectReasons.Password, "This stream needs a password.");

            var secure = target.HasSecret && challenge.RequiresSecret ? SecureChannel.FromSecret(target.Secret!) : null;
            var hello = new HelloMessage
            {
                Protocol = AppInfo.ProtocolVersion,
                Name = displayName,
                AppVersion = AppInfo.Version,
                Auth = target.HasPassword ? AuthProof.Compute(target.Password!, challenge.Nonce) : null,
            };
            var helloBytes = Json.Serialize(hello);
            await transport.WriteFramedAsync(
                secure is not null ? secure.Seal(MessageType.Hello, helloBytes) : Framing.Encode(MessageType.Hello, helloBytes),
                timeoutCts.Token
            ).ConfigureAwait(false);

            var answer = await transport.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
            if (answer is null)
                throw new ConnectException("protocol", "The host closed the connection.");
            if (answer.Value.Type == MessageType.Reject)
            {
                var reject = Json.Deserialize<RejectMessage>(answer.Value.Payload);
                throw new ConnectException(reject?.Reason ?? RejectReasons.Unknown, "The host rejected the connection.");
            }
            if (answer.Value.Type != MessageType.Welcome)
                throw new ConnectException("protocol", "Unexpected handshake reply.");

            var welcomeBytes = answer.Value.Payload;
            if (answer.Value.IsEncrypted)
            {
                if (secure is null || !secure.TryOpen(answer.Value, out welcomeBytes))
                    throw new ConnectException(RejectReasons.Secret, "Could not decrypt the host's reply.");
            }

            var welcome = Json.Deserialize<WelcomeMessage>(welcomeBytes)
                ?? throw new ConnectException("protocol", "Malformed welcome.");

            Welcome = welcome;
            _secure = secure;
            _transport = transport;
            _cts = new CancellationTokenSource();
            _windowStart = Stopwatch.GetTimestamp();
            _ = ReceiveLoopAsync(_cts.Token);
            _ = PingLoopAsync(_cts.Token);
            return welcome;
        }
        catch (ConnectException)
        {
            transport.Dispose();
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            transport.Dispose();
            throw new ConnectException("timeout", "The host took too long to answer.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or WebSocketException)
        {
            transport.Dispose();
            throw new ConnectException("protocol", ex.Message, ex);
        }
    }

    private static async Task<IMessageTransport> ConnectTcpAsync(InviteTarget target, CancellationToken timeout, CancellationToken user)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(target.Host, target.Port, timeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!user.IsCancellationRequested)
        {
            client.Dispose();
            throw new ConnectException("timeout", "Connection timed out.");
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new ConnectException("unreachable", ex.Message, ex);
        }
        client.ReceiveBufferSize = 4 * 1024 * 1024;
        return new StreamTransport(client.GetStream(), client);
    }

    private static async Task<IMessageTransport> ConnectRelayAsync(InviteTarget target, string displayName, CancellationToken timeout, CancellationToken user)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await socket.ConnectAsync(new Uri(target.RelayUrl!), timeout).ConfigureAwait(false);
            var join = new RelayJoin
            {
                Role = RelayProtocol.RoleViewer,
                Room = target.Room,
                AppKey = SettingsStore.Load().RelayAppKey,
                Name = displayName,
            };
            await socket.SendAsync(Json.Serialize(join), WebSocketMessageType.Text, true, timeout).ConfigureAwait(false);
            var result = await RelayClient.ReadJoinResultAsync(socket, timeout).ConfigureAwait(false);
            if (result is null || !result.Ok)
            {
                socket.Dispose();
                throw new ConnectException(result?.Reason ?? "protocol", "The relay refused the room.");
            }
            return new WebSocketTransport(socket);
        }
        catch (ConnectException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!user.IsCancellationRequested)
        {
            socket.Dispose();
            throw new ConnectException("timeout", "The relay did not answer.");
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException)
        {
            socket.Dispose();
            throw new ConnectException("relay_unreachable", ex.Message, ex);
        }
    }

    public void RequestKeyframe() => _ = SendAsync(MessageType.KeyframeRequest, Array.Empty<byte>());

    public Task DisconnectAsync() => CloseAsync("left", sendBye: true);

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var reason = "closed";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await _transport!.ReadAsync(ct).ConfigureAwait(false);
                if (message is null)
                    break;

                var m = message.Value;
                var payload = m.Payload;
                if (m.IsEncrypted)
                {
                    if (_secure is null || !_secure.TryOpen(m, out payload))
                        continue;
                }
                else if (_secure is not null && m.Type != MessageType.Bye)
                {
                    continue;
                }

                switch (m.Type)
                {
                    case MessageType.Video:
                        HandleVideo(payload);
                        break;
                    case MessageType.Audio:
                        HandleAudio(payload);
                        break;
                    case MessageType.Pong:
                        if (payload.Length >= 8)
                        {
                            var sent = BinaryPrimitives.ReadInt64LittleEndian(payload);
                            _rttMs = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                        }
                        break;
                    case MessageType.Viewers:
                        var viewers = Json.Deserialize<ViewersMessage>(payload);
                        if (viewers is not null)
                            ViewersChanged?.Invoke(viewers.Viewers);
                        break;
                    case MessageType.StreamState:
                        var state = Json.Deserialize<StreamStateMessage>(payload);
                        if (state is not null)
                            StreamStateChanged?.Invoke(state.State);
                        break;
                    case MessageType.Bye:
                        reason = "ended";
                        goto done;
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = "left";
        }
        catch (Exception)
        {
            reason = "lost";
        }

        done:
        await CloseAsync(reason, sendBye: false).ConfigureAwait(false);
    }

    private void HandleVideo(byte[] payload)
    {
        if (!VideoPacket.TryParse(payload, out var header, out var bitstream))
            return;

        var handler = VideoReceived;
        var decodeMs = handler?.Invoke(header, bitstream) ?? 0;

        _width = header.Width;
        _height = header.Height;
        Interlocked.Increment(ref _framesReceived);
        _bytesWindow += payload.Length;
        _framesWindow++;
        _decodeWindowMs += decodeMs;
        MaybePublishStats();
    }

    private void HandleAudio(byte[] payload)
    {
        if (!AudioPacket.TryParse(payload, out var header, out var opus))
            return;
        _audioBytesWindow += payload.Length;
        AudioReceived?.Invoke(header, opus);
        MaybePublishStats();
    }

    private void MaybePublishStats()
    {
        var elapsed = Stopwatch.GetElapsedTime(_windowStart);
        if (elapsed.TotalMilliseconds < 1000)
            return;

        var seconds = elapsed.TotalSeconds;
        var stats = new ViewerStats(
            _framesWindow / seconds,
            _bytesWindow * 8 / 1000.0 / seconds,
            _audioBytesWindow * 8 / 1000.0 / seconds,
            _rttMs,
            _framesWindow > 0 ? _decodeWindowMs / _framesWindow : 0,
            _width,
            _height,
            Interlocked.Read(ref _framesReceived)
        );
        _framesWindow = 0;
        _bytesWindow = 0;
        _audioBytesWindow = 0;
        _decodeWindowMs = 0;
        _windowStart = Stopwatch.GetTimestamp();
        StatsUpdated?.Invoke(stats);
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            var payload = new byte[8];
            while (!ct.IsCancellationRequested)
            {
                BinaryPrimitives.WriteInt64LittleEndian(payload, Stopwatch.GetTimestamp());
                await SendAsync(MessageType.Ping, payload).ConfigureAwait(false);
                await Task.Delay(PingInterval, ct).ConfigureAwait(false);
            }
        }
        catch (Exception) { }
    }

    private async Task SendAsync(MessageType type, byte[] payload)
    {
        var transport = _transport;
        var cts = _cts;
        if (transport is null || cts is null)
            return;

        try
        {
            var framed = _secure is { } s ? s.Seal(type, payload) : Framing.Encode(type, payload);
            await transport.WriteFramedAsync(framed, cts.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    private async Task CloseAsync(string reason, bool sendBye)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;

        if (sendBye)
            await SendAsync(MessageType.Bye, Array.Empty<byte>()).ConfigureAwait(false);

        SafeTry.Run(() => _cts?.Cancel());
        SafeTry.Run(() => _transport?.Dispose());
        Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        _ = CloseAsync("disposed", sendBye: false);
        _cts?.Dispose();
        _secure?.Dispose();
    }
}
