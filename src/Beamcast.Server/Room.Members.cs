using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Beamcast.Net;

namespace Beamcast.Server;

/// <summary>What a room keeps per stream and per connected member.</summary>
internal sealed partial class Room
{
    /// <summary>A published stream: owner, opaque metadata and who listens.</summary>
    private sealed class RoomStream
    {
        public RoomStream(uint id, uint owner, byte[] meta)
        {
            Id = id;
            Owner = owner;
            Meta = meta;
        }

        public uint Id { get; }
        public uint Owner { get; }
        public byte[] Meta { get; }
        public ConcurrentDictionary<uint, byte> Subscribers { get; } = new();
        public long LastKeyframeRequestTicks;
    }

    /// <summary>A connected member: one outbox, one keyframe gate per stream it watches.</summary>
    private sealed class Member
    {
        private readonly Channel<(byte[] Bytes, uint StreamId)> _outbox =
            Channel.CreateUnbounded<(byte[], uint)>(new UnboundedChannelOptions { SingleReader = true });
        private readonly ConcurrentDictionary<uint, Subscription> _subscriptions = new();
        private readonly CancellationTokenSource _lifetime;
        private int _closing;

        public Member(uint id, WebSocket socket, bool isOwner, CancellationTokenSource lifetime)
        {
            Id = id;
            Socket = socket;
            IsOwner = isOwner;
            _lifetime = lifetime;
        }

        public uint Id { get; }
        public WebSocket Socket { get; }
        public bool IsOwner { get; }
        public byte[]? Presence { get; set; }
        public volatile bool KeyPending;
        public byte[]? JoinKey { get; set; }
        public string? ClientId { get; init; }
        public Task? Sender { get; set; }

        public void Subscribe(uint streamId, int maxPending) => _subscriptions[streamId] = new Subscription(maxPending);

        public void Unsubscribe(uint streamId) => _subscriptions.TryRemove(streamId, out _);

        public void ResetGate(uint streamId)
        {
            if (_subscriptions.TryGetValue(streamId, out var sub))
                lock (sub) sub.Gate.RequestKeyframe();
        }

        /// <summary>Returns true when the publisher should be asked for a keyframe on this member's behalf.</summary>
        public bool OfferVideo(uint streamId, byte[] frame, bool keyframe)
        {
            if (!_subscriptions.TryGetValue(streamId, out var sub))
                return false;
            GateDecision decision;
            lock (sub)
            {
                decision = sub.Gate.Offer(keyframe, Volatile.Read(ref sub.Pending));
            }
            switch (decision)
            {
                case GateDecision.Send:
                    Interlocked.Increment(ref sub.Pending);
                    _outbox.Writer.TryWrite((frame, streamId));
                    return false;
                case GateDecision.DropAndRequestKeyframe:
                    return true;
                default:
                    return false;
            }
        }

        public void Enqueue(byte[] frame, uint streamId) => _outbox.Writer.TryWrite((frame, streamId));

        public void Complete() => _outbox.Writer.TryComplete();

        /// <summary>Tells the member why and then closes: the reason is flushed before the socket goes.</summary>
        public void CloseWith(string reason)
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0)
                return;
            Enqueue(LoungeMux.Encode(LoungeMux.Bye, 0, 0, Json.Serialize(new ServerNotice { Reason = reason })), 0);
            Complete();
            _ = Task.Run(async () =>
            {
                await SafeAwait(Sender);
                try
                {
                    _lifetime.Cancel();
                }
                catch (ObjectDisposedException) { }
            });
        }

        public async Task SendLoopAsync(CancellationToken ct)
        {
            try
            {
                var reader = _outbox.Reader;
                while (await reader.WaitToReadAsync(ct))
                {
                    while (reader.TryRead(out var item))
                    {
                        await Socket.SendAsync(item.Bytes, WebSocketMessageType.Binary, true, ct);
                        if (item.StreamId != 0 && _subscriptions.TryGetValue(item.StreamId, out var sub))
                            Interlocked.Decrement(ref sub.Pending);
                    }
                }
            }
            catch (Exception) { }
        }

        private sealed class Subscription
        {
            public Subscription(int maxPending)
            {
                Gate = new FrameGate(maxPending);
            }

            public FrameGate Gate { get; }
            public int Pending;
        }
    }
}
