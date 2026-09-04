using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;

namespace Beamcast.Net;

public sealed class ConnectException : Exception
{
    public ConnectException(string reason, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>One of <see cref="RejectReasons"/>, or "unreachable" / "timeout" / "protocol" / "codec".</summary>
    public string Reason { get; }
}

public sealed record ViewerStats(double Fps, double Kbps, double RttMs, double DecodeMs, int Width, int Height, long FramesReceived);

/// <summary>
/// Connects to a host, completes the handshake and delivers the compressed frames to
/// <see cref="VideoReceived"/> on the receive thread. Decoding is the consumer's job so the
/// GPU decoder and the presenter can run without any extra hop.
/// </summary>
public sealed class ViewerClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _closed;

    private long _bytesWindow;
    private int _framesWindow;
    private double _decodeWindowMs;
    private long _windowStart;
    private long _framesReceived;
    private double _rttMs;
    private int _width;
    private int _height;

    /// <summary>Called for each frame with its header and Annex B / VP8 payload; returns the decode time in ms.</summary>
    public event Func<VideoPacketHeader, ReadOnlyMemory<byte>, double>? VideoReceived;
    public event Action<IReadOnlyList<string>>? ViewersChanged;
    public event Action<string>? StreamStateChanged;
    public event Action<ViewerStats>? StatsUpdated;
    public event Action<string>? Closed;

    public WelcomeMessage? Welcome { get; private set; }

    public bool IsConnected => _client is not null && Interlocked.CompareExchange(ref _closed, 0, 0) == 0;

    public async Task<WelcomeMessage> ConnectAsync(InviteTarget target, string displayName, CancellationToken ct)
    {
        if (_client is not null)
            throw new InvalidOperationException("Already connected.");

        var client = new TcpClient { NoDelay = true };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConnectTimeout);

        try
        {
            await client.ConnectAsync(target.Host, target.Port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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
        var stream = client.GetStream();
        try
        {
            var first = await MessageStream.ReadAsync(stream, timeoutCts.Token).ConfigureAwait(false);
            var challenge = first?.Type == MessageType.Challenge
                ? Json.Deserialize<ChallengeMessage>(first.Value.Payload)
                : null;
            if (challenge is null)
                throw new ConnectException("protocol", "The host did not answer with a Beamcast handshake.");
            if (challenge.Protocol != AppInfo.ProtocolVersion)
                throw new ConnectException(RejectReasons.Version, "Protocol version mismatch.");
            if (challenge.RequiresPassword && !target.HasPassword)
                throw new ConnectException(RejectReasons.Password, "This stream needs a password.");

            var hello = new HelloMessage
            {
                Protocol = AppInfo.ProtocolVersion,
                Name = displayName,
                AppVersion = AppInfo.Version,
                Auth = target.HasPassword ? AuthProof.Compute(target.Password!, challenge.Nonce) : null,
            };
            await MessageStream.WriteJsonAsync(stream, MessageType.Hello, hello, timeoutCts.Token).ConfigureAwait(false);

            var answer = await MessageStream.ReadAsync(stream, timeoutCts.Token).ConfigureAwait(false);
            if (answer is null)
                throw new ConnectException("protocol", "The host closed the connection.");
            if (answer.Value.Type == MessageType.Reject)
            {
                var reject = Json.Deserialize<RejectMessage>(answer.Value.Payload);
                throw new ConnectException(reject?.Reason ?? RejectReasons.Unknown, "The host rejected the connection.");
            }
            if (answer.Value.Type != MessageType.Welcome)
                throw new ConnectException("protocol", "Unexpected handshake reply.");

            var welcome = Json.Deserialize<WelcomeMessage>(answer.Value.Payload)
                ?? throw new ConnectException("protocol", "Malformed welcome.");

            Welcome = welcome;
            _client = client;
            _stream = stream;
            _cts = new CancellationTokenSource();
            _windowStart = Stopwatch.GetTimestamp();
            _ = ReceiveLoopAsync(_cts.Token);
            _ = PingLoopAsync(_cts.Token);
            return welcome;
        }
        catch (ConnectException)
        {
            client.Dispose();
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new ConnectException("timeout", "The host took too long to answer.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
        {
            client.Dispose();
            throw new ConnectException("protocol", ex.Message, ex);
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
                var message = await MessageStream.ReadAsync(_stream!, ct).ConfigureAwait(false);
                if (message is null)
                    break;

                var (type, payload) = message.Value;
                switch (type)
                {
                    case MessageType.Video:
                        HandleVideo(payload);
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

    private void MaybePublishStats()
    {
        var elapsed = Stopwatch.GetElapsedTime(_windowStart);
        if (elapsed.TotalMilliseconds < 1000)
            return;

        var seconds = elapsed.TotalSeconds;
        var stats = new ViewerStats(
            _framesWindow / seconds,
            _bytesWindow * 8 / 1000.0 / seconds,
            _rttMs,
            _framesWindow > 0 ? _decodeWindowMs / _framesWindow : 0,
            _width,
            _height,
            Interlocked.Read(ref _framesReceived)
        );
        _framesWindow = 0;
        _bytesWindow = 0;
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
        var stream = _stream;
        var cts = _cts;
        if (stream is null || cts is null)
            return;

        try
        {
            await _writeLock.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                await MessageStream.WriteAsync(stream, type, payload, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
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
        SafeTry.Run(() => _client?.Dispose());
        Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        _ = CloseAsync("disposed", sendBye: false);
        _cts?.Dispose();
        _writeLock.Dispose();
    }
}
