using SIPSorcery.Net;

namespace Beamcast.Net;

/// <summary>
/// One direct (P2P) link between a publisher and one viewer: a WebRTC data channel over UDP that
/// finds its way through both routers with STUN, so nobody opens a port. It carries the same
/// sealed <see cref="LoungeMux"/> frames the host relays (already end-to-end encrypted with the
/// room key; DTLS adds a second layer), plus the viewer's keyframe requests and delay reports.
///
/// The channel is reliable and ordered, like the relay: inter frames depend on the frames before
/// them, so a lost one would break the picture until the next keyframe. What the link removes is
/// the trip through the host and its proxy; when the path gets slow the publisher drops frames and
/// restarts at a keyframe, exactly as it does on the relay.
///
/// SCTP messages are limited in size, so frames go out in chunks of <see cref="ChunkSize"/> bytes,
/// each prefixed with one byte: 1 on the last chunk of a frame, 0 otherwise.
/// </summary>
public sealed class PeerLink : IDisposable
{
    public const int ChunkSize = 60 * 1024;
    public const int MaxFrameSize = Framing.MaxMessageSize + LoungeMux.HeaderSize;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);

    /// <summary>A path that stays "disconnected" this long is given up (it may come back on its own before).</summary>
    private static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(5);

    private static readonly string[] StunServers = ["stun:stun.cloudflare.com:3478", "stun:stun.l.google.com:19302"];

    private readonly RTCPeerConnection _pc;
    private readonly Action<PeerSignal> _signal;
    private readonly object _sendLock = new();
    private readonly List<string> _earlyCandidates = [];
    private readonly MemoryStream _assembly = new();
    private RTCDataChannel? _channel;
    private bool _descriptionSent;
    private int _state; // 0 connecting, 1 open, 2 closed
    private long _disconnectedSince;

    public PeerLink(uint peerId, uint streamId, uint session, bool offerer, Action<PeerSignal> signal)
    {
        PeerId = peerId;
        StreamId = streamId;
        Session = session;
        IsOfferer = offerer;
        _signal = signal;
        var config = new RTCConfiguration
        {
            iceServers = StunServers.Select(url => new RTCIceServer { urls = url }).ToList(),
        };
        _pc = new RTCPeerConnection(config);
        _pc.onicecandidate += OnLocalCandidate;
        _pc.onconnectionstatechange += OnConnectionState;
        _pc.ondatachannel += channel => Hook(channel);
        _ = TimeoutAsync();
    }

    public uint PeerId { get; }
    public uint StreamId { get; }
    public uint Session { get; }
    public bool IsOfferer { get; }
    public bool IsOpen => Volatile.Read(ref _state) == 1;
    public bool IsClosed => Volatile.Read(ref _state) == 2;

    /// <summary>Bytes handed to the channel that have not left yet: how far behind this path is.</summary>
    public ulong BufferedBytes => SafeTry.Run(() => _channel?.bufferedAmount ?? 0UL);

    /// <summary>Why the link closed ("timeout", "failed", "bye:…", "local:…").</summary>
    public string CloseReason { get; private set; } = string.Empty;

    public event Action<PeerLink>? Opened;
    public event Action<PeerLink, string>? Closed;
    public event Action<PeerLink, byte[]>? Received;

    /// <summary>Publisher: opens the channel and sends the offer.</summary>
    public async Task StartAsync()
    {
        try
        {
            var channel = await _pc.createDataChannel("beam", new RTCDataChannelInit { ordered = true }).ConfigureAwait(false);
            Hook(channel);
            var offer = _pc.createOffer(null);
            await _pc.setLocalDescription(offer).ConfigureAwait(false);
            SendDescription(PeerSignal.KindOffer, offer.toJSON());
        }
        catch (Exception ex)
        {
            Close("failed: " + ex.Message, notifyPeer: true);
        }
    }

    /// <summary>A signal from the other side for this attempt.</summary>
    public async Task HandleAsync(PeerSignal signal)
    {
        if (IsClosed)
            return;
        try
        {
            switch (signal.Kind)
            {
                case PeerSignal.KindOffer when !IsOfferer:
                {
                    if (!RTCSessionDescriptionInit.TryParse(signal.Data ?? string.Empty, out var offer) || _pc.setRemoteDescription(offer) != SetDescriptionResultEnum.OK)
                    {
                        Close("bad offer", notifyPeer: true);
                        return;
                    }
                    var answer = _pc.createAnswer(null);
                    await _pc.setLocalDescription(answer).ConfigureAwait(false);
                    SendDescription(PeerSignal.KindAnswer, answer.toJSON());
                    break;
                }
                case PeerSignal.KindAnswer when IsOfferer:
                    if (!RTCSessionDescriptionInit.TryParse(signal.Data ?? string.Empty, out var remote) || _pc.setRemoteDescription(remote) != SetDescriptionResultEnum.OK)
                        Close("bad answer", notifyPeer: true);
                    break;
                case PeerSignal.KindIce:
                    if (RTCIceCandidateInit.TryParse(signal.Data ?? string.Empty, out var candidate))
                        _pc.addIceCandidate(candidate);
                    break;
                case PeerSignal.KindBye:
                    Close("bye:" + (signal.Data ?? string.Empty), notifyPeer: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            Close("failed: " + ex.Message, notifyPeer: true);
        }
    }

    /// <summary>Sends one whole frame; false when the link is not open (the caller uses the relay).</summary>
    public bool Send(byte[] frame)
    {
        var channel = _channel;
        if (!IsOpen || channel is null)
            return false;
        try
        {
            lock (_sendLock)
            {
                for (var offset = 0; offset < frame.Length || offset == 0; offset += ChunkSize)
                {
                    var count = Math.Min(ChunkSize, frame.Length - offset);
                    var chunk = new byte[count + 1];
                    chunk[0] = offset + count >= frame.Length ? (byte)1 : (byte)0;
                    Buffer.BlockCopy(frame, offset, chunk, 1, count);
                    channel.send(chunk, 0, chunk.Length);
                    if (count == 0)
                        break;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Close("send: " + ex.Message, notifyPeer: false);
            return false;
        }
    }

    public void Close(string reason, bool notifyPeer)
    {
        if (Interlocked.Exchange(ref _state, 2) == 2)
            return;
        CloseReason = reason;
        Diag.Log($"p2p: link with {PeerId} for #{StreamId} (session {Session}) closed: {reason}");
        if (notifyPeer)
            SafeTry.Run(() => _signal(new PeerSignal { To = PeerId, Stream = StreamId, Session = Session, Kind = PeerSignal.KindBye, Data = reason }));
        SafeTry.Run(() => _channel?.close());
        SafeTry.Run(() => _pc.close());
        Closed?.Invoke(this, reason);
    }

    public void Dispose()
    {
        Close("local:disposed", notifyPeer: false);
        SafeTry.Run(() => _pc.Dispose());
    }

    // ----- internals -----

    private void SendDescription(string kind, string json)
    {
        _signal(new PeerSignal { To = PeerId, Stream = StreamId, Session = Session, Kind = kind, Data = json });
        List<string> early;
        lock (_earlyCandidates)
        {
            _descriptionSent = true;
            early = _earlyCandidates.ToList();
            _earlyCandidates.Clear();
        }
        foreach (var candidate in early)
            SendCandidate(candidate);
    }

    private void OnLocalCandidate(RTCIceCandidate candidate)
    {
        if (candidate is null || IsClosed)
            return;
        var json = SafeTry.Run(candidate.toJSON);
        if (string.IsNullOrEmpty(json))
            return;
        // Candidates found before our offer/answer went out would reach the other side before the
        // description they belong to: hold them until it is sent.
        lock (_earlyCandidates)
        {
            if (!_descriptionSent)
            {
                _earlyCandidates.Add(json);
                return;
            }
        }
        SendCandidate(json);
    }

    private void SendCandidate(string json) =>
        _signal(new PeerSignal { To = PeerId, Stream = StreamId, Session = Session, Kind = PeerSignal.KindIce, Data = json });

    private void OnConnectionState(RTCPeerConnectionState state)
    {
        Diag.Log($"p2p: link with {PeerId} for #{StreamId}: {state}");
        switch (state)
        {
            case RTCPeerConnectionState.connected:
                Interlocked.Exchange(ref _disconnectedSince, 0);
                break;
            case RTCPeerConnectionState.disconnected:
                Interlocked.Exchange(ref _disconnectedSince, Environment.TickCount64);
                _ = GraceAsync();
                break;
            case RTCPeerConnectionState.failed:
                Close("failed", notifyPeer: true);
                break;
            case RTCPeerConnectionState.closed:
                Close("closed", notifyPeer: false);
                break;
        }
    }

    private async Task GraceAsync()
    {
        await Task.Delay(DisconnectGrace).ConfigureAwait(false);
        var since = Interlocked.Read(ref _disconnectedSince);
        if (since != 0 && Environment.TickCount64 - since >= DisconnectGrace.TotalMilliseconds - 50)
            Close("disconnected", notifyPeer: true);
    }

    private async Task TimeoutAsync()
    {
        await Task.Delay(ConnectTimeout).ConfigureAwait(false);
        if (Volatile.Read(ref _state) == 0)
            Close("timeout", notifyPeer: true);
    }

    private void Hook(RTCDataChannel channel)
    {
        _channel = channel;
        channel.onopen += MarkOpen;
        channel.onclose += () => Close("channel closed", notifyPeer: false);
        channel.onmessage += (_, _, data) => OnChunk(data);
        if (channel.readyState == RTCDataChannelState.open)
            MarkOpen();
    }

    private void MarkOpen()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return;
        Diag.Log($"p2p: link with {PeerId} for #{StreamId} (session {Session}) open");
        Opened?.Invoke(this);
    }

    private void OnChunk(byte[] data)
    {
        if (data is null || data.Length == 0 || IsClosed)
            return;
        byte[]? whole = null;
        lock (_assembly)
        {
            _assembly.Write(data, 1, data.Length - 1);
            if (_assembly.Length > MaxFrameSize)
            {
                _assembly.SetLength(0);
                Close("frame too large", notifyPeer: true);
                return;
            }
            if (data[0] == 1)
            {
                whole = _assembly.ToArray();
                _assembly.SetLength(0);
            }
        }
        if (whole is not null)
            Received?.Invoke(this, whole);
    }
}
