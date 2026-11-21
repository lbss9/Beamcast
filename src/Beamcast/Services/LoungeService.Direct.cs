using Beamcast.Net;

namespace Beamcast.Services;

/// <summary>A viewer's video started or stopped coming over a direct link. UI thread.</summary>
/// <param name="AsPublisher">True on the publisher's side (PeerId is the viewer), false on the viewer's (PeerId is the publisher).</param>
/// <param name="Reason">Why it changed: empty when it went direct, otherwise the link's close reason.</param>
public sealed record DirectPathChange(uint StreamId, uint PeerId, string PeerName, bool Direct, bool AsPublisher, string Reason);

/// <summary>One viewer of my stream, for the connection panel.</summary>
/// <param name="DelayMs">The last delay this viewer reported, 0 when it has not complained lately.</param>
/// <param name="Dropped">Video frames held back on its direct link because the link was behind (0 on the relay: the host does that part).</param>
public sealed record ViewerPath(uint ViewerId, string Name, bool Direct, int DelayMs, long Dropped);

/// <summary>
/// Direct (P2P) video links. The host stays in charge of the room (members, streams, keys) and
/// keeps relaying the video until a direct link has proven itself:
///
/// 1. A viewer subscribes through the host as always, so the picture starts at once.
/// 2. The publisher hears about the viewer and, if the viewer's app supports it, offers a link
///    (signals ride the encrypted control channel; the host relays them unread).
/// 3. When the link opens the publisher sends a keyframe on it. On that keyframe the viewer
///    switches over and unsubscribes from the host's copy, which saves the host and the viewer
///    the second stream. Once no viewer needs the relay the publisher stops uploading to the host.
/// 4. If the link fails or breaks, the viewer subscribes through the host again (the host sends
///    it a keyframe) and the publisher retries later, waiting longer after each failure.
///
/// Nothing here needs a new host: old hosts relay the signals like any control message, and old
/// apps never announce <see cref="PresenceMessage.Direct"/>, so nobody offers them a link.
/// </summary>
public sealed partial class LoungeService
{
    /// <summary>Waits before trying a direct link again after the previous attempt failed.</summary>
    private static readonly int[] DirectRetrySeconds = [20, 60, 180, 600];

    /// <summary>A direct path with more than this queued is falling behind: drop to the next keyframe.</summary>
    private const ulong DirectMaxBuffered = 1_500_000;

    private const long DirectKeyframeAskGapMs = 500;

    private sealed class DirectEntry
    {
        public DirectEntry(PeerLink link, bool isPublisher)
        {
            Link = link;
            IsPublisher = isPublisher;
        }

        public PeerLink Link { get; }
        public bool IsPublisher { get; }

        /// <summary>Viewer: video now comes from this link and the relay copy is off.</summary>
        public bool Active { get; set; }

        /// <summary>Publisher: this path dropped (or just opened) and waits for a keyframe.</summary>
        public bool AwaitingKeyframe { get; set; } = true;

        /// <summary>The link opened at some point (it may be closed now).</summary>
        public bool WasOpen { get; set; }

        public long LastKeyframeAsk { get; set; }

        /// <summary>Publisher: video frames not sent on this link because it was behind.</summary>
        public long Dropped;
    }

    private sealed class DirectRetry
    {
        public int Failures { get; set; }
        public long NotBefore { get; set; }
        public bool Declined { get; set; }
    }

    private readonly object _directSync = new();

    /// <summary>
    /// Media used to arrive on one thread (the host socket). With direct links a frame can come from
    /// a link's thread too, and around a switch both at once; the decoders are not thread-safe.
    /// </summary>
    private readonly object _deliverSync = new();
    private readonly Dictionary<(uint Peer, uint Stream), DirectEntry> _direct = new();
    private readonly Dictionary<uint, HashSet<uint>> _relayViewers = new();
    private readonly Dictionary<uint, HashSet<uint>> _knownViewers = new();
    private readonly Dictionary<(uint Peer, uint Stream), DirectRetry> _directRetry = new();
    private readonly HashSet<uint> _watched = new();
    private uint _directSession = (uint)Random.Shared.Next(1, int.MaxValue / 2);
    private volatile bool _directEnabled = SettingsStore.Load().DirectConnection;

    /// <summary>Publisher, network thread: a direct link fell behind and video was held back on it.</summary>
    public event Action<uint>? DirectCongested;

    private readonly Dictionary<(uint Viewer, uint Stream), (int DelayMs, long At)> _viewerDelay = new();

    /// <summary>How long a viewer's delay report stays on the panel.</summary>
    private const long DelayReportTtlMs = 6000;

    private void OnViewerReport(uint streamId, uint viewerId, int delayMs)
    {
        lock (_directSync)
            _viewerDelay[(viewerId, streamId)] = (delayMs, Environment.TickCount64);
        ViewerReported?.Invoke(streamId, viewerId, delayMs);
    }

    /// <summary>Publisher: who watches this stream and how each one is connected.</summary>
    public IReadOnlyList<ViewerPath> ViewerPaths(uint streamId)
    {
        List<(uint Id, bool Direct, int Delay, long Dropped)> rows = [];
        var now = Environment.TickCount64;
        lock (_directSync)
        {
            if (_knownViewers.TryGetValue(streamId, out var known))
            {
                foreach (var id in known)
                {
                    var entry = _direct.TryGetValue((id, streamId), out var e) && e.IsPublisher && e.Link.IsOpen ? e : null;
                    var delay = _viewerDelay.TryGetValue((id, streamId), out var report) && now - report.At < DelayReportTtlMs ? report.DelayMs : 0;
                    rows.Add((id, entry is not null, delay, entry is null ? 0 : Interlocked.Read(ref entry.Dropped)));
                }
            }
        }
        return rows.Select(r => new ViewerPath(r.Id, NameOf(r.Id), r.Direct, r.Delay, r.Dropped)).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A video path switched between direct and the host's relay. UI thread.</summary>
    public event Action<DirectPathChange>? PathChanged;

    /// <summary>Try direct links (setting). Turning it off closes them; the relay takes over.</summary>
    public bool DirectEnabled
    {
        get => _directEnabled;
        set
        {
            if (_directEnabled == value)
                return;
            _directEnabled = value;
            Diag.Log($"p2p: direct links {(value ? "on" : "off")}");
            SafeTry.Run(() => _client?.SendPresence(MyPresence()));
            if (!value)
                CloseAllDirect("off", notifyPeer: true);
        }
    }

    /// <summary>Viewer: the video of this stream comes over a direct link right now.</summary>
    public bool IsDirect(uint streamId)
    {
        lock (_directSync)
            return _direct.Values.Any(e => !e.IsPublisher && e.Active && e.Link.StreamId == streamId);
    }

    /// <summary>Publisher: viewers of this stream on an open direct link.</summary>
    public int DirectViewers(uint streamId)
    {
        lock (_directSync)
            return _direct.Values.Count(e => e.IsPublisher && e.Link.IsOpen && e.Link.StreamId == streamId);
    }

    private PresenceMessage MyPresence() => new() { Name = _displayName, AppVersion = AppInfo.Version, Direct = _directEnabled };

    // ----- media out (publisher) -----

    /// <summary>
    /// Sends one media frame to every viewer: over each open direct link, and through the host
    /// while anyone still watches there. <paramref name="relayGate"/> is asked only when the frame
    /// would go to the host (it holds the uplink back-pressure for the relay).
    /// </summary>
    public void SendMedia(uint streamId, MessageType type, ReadOnlySpan<byte> body, bool keyframe, uint sendStamp = 0, Func<bool>? relayGate = null)
    {
        var client = _client;
        if (client is null)
            return;
        var frame = client.SealMedia(streamId, type, body, keyframe, sendStamp);
        if (frame is null)
            return;
        var isVideo = type == MessageType.Video;

        List<DirectEntry> links;
        lock (_directSync)
            links = _direct.Values.Where(e => e.IsPublisher && e.Link.IsOpen && e.Link.StreamId == streamId).ToList();
        foreach (var entry in links)
            SendDirect(entry, frame, isVideo, keyframe);

        if (NeedsRelay(streamId) && (relayGate?.Invoke() ?? true))
            client.SendSealedMedia(frame, streamId, isVideo, keyframe);
    }

    private void SendDirect(DirectEntry entry, byte[] frame, bool isVideo, bool keyframe)
    {
        var behind = entry.Link.BufferedBytes > DirectMaxBuffered;
        if (isVideo)
        {
            if (behind)
            {
                entry.AwaitingKeyframe = true;
                Interlocked.Increment(ref entry.Dropped);
                DirectCongested?.Invoke(entry.Link.StreamId);
                AskKeyframe(entry);
                return;
            }
            if (entry.AwaitingKeyframe)
            {
                if (!keyframe)
                {
                    Interlocked.Increment(ref entry.Dropped);
                    AskKeyframe(entry);
                    return;
                }
                entry.AwaitingKeyframe = false;
            }
        }
        else if (behind)
        {
            return;
        }
        entry.Link.Send(frame);
    }

    private void AskKeyframe(DirectEntry entry)
    {
        var now = Environment.TickCount64;
        if (now - entry.LastKeyframeAsk < DirectKeyframeAskGapMs)
            return;
        entry.LastKeyframeAsk = now;
        KeyframeRequested?.Invoke(entry.Link.StreamId);
    }

    /// <summary>
    /// The host's copy is needed unless every viewer is on a direct link. Hosts before 2.3.0 never
    /// say who watches (no clock in the heartbeat either), so with those the relay always runs.
    /// </summary>
    private bool NeedsRelay(uint streamId)
    {
        if (!ClockSynced)
            return true;
        lock (_directSync)
        {
            var relay = _relayViewers.TryGetValue(streamId, out var set) ? set.Count : 0;
            var direct = _direct.Values.Count(e => e.IsPublisher && e.Link.IsOpen && e.Link.StreamId == streamId);
            return relay > 0 || direct == 0;
        }
    }

    // ----- viewers of my streams (publisher) -----

    private void OnRelayViewerJoined(uint streamId, uint viewerId)
    {
        lock (_directSync)
        {
            if (!_relayViewers.TryGetValue(streamId, out var set))
                _relayViewers[streamId] = set = [];
            set.Add(viewerId);
        }
        UpdateKnownViewer(streamId, viewerId);
        MaybeOfferDirect(viewerId, streamId);
    }

    private void OnRelayViewerLeft(uint streamId, uint viewerId)
    {
        DirectEntry? pending = null;
        lock (_directSync)
        {
            if (_relayViewers.TryGetValue(streamId, out var set))
                set.Remove(viewerId);
            // A viewer that left the relay without a working direct link stopped watching.
            if (_direct.TryGetValue((viewerId, streamId), out var entry) && !entry.Link.IsOpen)
                pending = entry;
            if (pending is not null || !_direct.ContainsKey((viewerId, streamId)))
                _directRetry.Remove((viewerId, streamId));
        }
        pending?.Link.Close("local:viewer left", notifyPeer: true);
        UpdateKnownViewer(streamId, viewerId);
    }

    /// <summary>Raises ViewerJoined/ViewerLeft only when a viewer starts or stops watching by either path, not when it switches.</summary>
    private void UpdateKnownViewer(uint streamId, uint viewerId)
    {
        bool? change = null;
        lock (_directSync)
        {
            var watching = (_relayViewers.TryGetValue(streamId, out var relay) && relay.Contains(viewerId))
                || (_direct.TryGetValue((viewerId, streamId), out var entry) && entry.IsPublisher && entry.Link.IsOpen);
            if (!_knownViewers.TryGetValue(streamId, out var known))
                _knownViewers[streamId] = known = [];
            if (watching && known.Add(viewerId))
                change = true;
            else if (!watching && known.Remove(viewerId))
                change = false;
        }
        if (change is { } joined)
            Post(() => (joined ? ViewerJoined : ViewerLeft)?.Invoke(streamId, viewerId, NameOf(viewerId)));
    }

    private void MaybeOfferDirect(uint viewerId, uint streamId)
    {
        if (!_directEnabled || _client is null)
            return;
        lock (_sync)
        {
            if (!_members.TryGetValue(viewerId, out var member) || !member.SupportsDirect)
                return;
            if (!_streams.TryGetValue(streamId, out var stream) || !stream.IsMine)
                return;
        }
        PeerLink link;
        lock (_directSync)
        {
            if (_direct.ContainsKey((viewerId, streamId)))
                return;
            if (!_relayViewers.TryGetValue(streamId, out var relay) || !relay.Contains(viewerId))
                return;
            if (_directRetry.TryGetValue((viewerId, streamId), out var retry) && (retry.Declined || retry.NotBefore > Environment.TickCount64))
                return;
            link = NewLink(viewerId, streamId, ++_directSession, offerer: true);
            _direct[(viewerId, streamId)] = new DirectEntry(link, isPublisher: true);
        }
        Diag.Log($"p2p: offering a direct link to {viewerId} ({NameOf(viewerId)}) for #{streamId}");
        _ = link.StartAsync();
    }

    private void ScheduleDirectRetry(uint viewerId, uint streamId, string reason)
    {
        int delay;
        lock (_directSync)
        {
            if (!_directRetry.TryGetValue((viewerId, streamId), out var retry))
                _directRetry[(viewerId, streamId)] = retry = new DirectRetry();
            if (reason.StartsWith("bye:off", StringComparison.Ordinal) || reason.StartsWith("bye:not_watching", StringComparison.Ordinal))
            {
                retry.Declined = reason.StartsWith("bye:off", StringComparison.Ordinal);
                if (retry.Declined)
                {
                    Diag.Log($"p2p: {viewerId} has direct links off; staying on the relay");
                    return;
                }
            }
            delay = DirectRetrySeconds[Math.Min(retry.Failures, DirectRetrySeconds.Length - 1)];
            retry.Failures++;
            retry.NotBefore = Environment.TickCount64 + delay * 1000L;
        }
        Diag.Log($"p2p: next direct attempt with {viewerId} for #{streamId} in {delay} s");
        var client = _client;
        _ = Task.Delay(TimeSpan.FromSeconds(delay)).ContinueWith(_ =>
        {
            if (ReferenceEquals(_client, client))
                MaybeOfferDirect(viewerId, streamId);
        });
    }

    // ----- watching (viewer) -----

    public void Subscribe(uint streamId)
    {
        Diag.Log($"lounge: subscribe #{streamId}");
        lock (_directSync)
            _watched.Add(streamId);
        _client?.Subscribe(streamId);
    }

    public void Unsubscribe(uint streamId)
    {
        Diag.Log($"lounge: unsubscribe #{streamId}");
        List<DirectEntry> links;
        lock (_directSync)
        {
            _watched.Remove(streamId);
            links = _direct.Values.Where(e => !e.IsPublisher && e.Link.StreamId == streamId).ToList();
        }
        foreach (var entry in links)
            entry.Link.Close("local:stopped", notifyPeer: true);
        _client?.Unsubscribe(streamId);
    }

    public void RequestKeyframe(uint streamId)
    {
        Diag.Log($"lounge: keyframe request for #{streamId}");
        if (ActiveViewerLink(streamId) is { } link && link.Send(LoungeMux.Encode(LoungeMux.KeyframeRequest, streamId, 0, ReadOnlySpan<byte>.Empty)))
            return;
        _client?.RequestKeyframe(streamId);
    }

    public void SendViewerReport(uint streamId, int latencyMs)
    {
        if (ActiveViewerLink(streamId) is { } link
            && link.Send(LoungeMux.Encode(LoungeMux.ViewerReport, streamId, (uint)Math.Clamp(latencyMs, 0, int.MaxValue), ReadOnlySpan<byte>.Empty)))
            return;
        _client?.SendViewerReport(streamId, latencyMs);
    }

    private PeerLink? ActiveViewerLink(uint streamId)
    {
        lock (_directSync)
            return _direct.Values.FirstOrDefault(e => !e.IsPublisher && e.Active && e.Link.StreamId == streamId)?.Link;
    }

    /// <summary>Media from the host. Dropped while the same stream comes over a direct link.</summary>
    private void OnRelayMedia(uint streamId, MessageType type, bool keyframe, byte[] body, uint stamp)
    {
        lock (_deliverSync)
        {
            if (ActiveViewerLink(streamId) is not null)
                return;
            MediaReceived?.Invoke(streamId, type, keyframe, body, stamp);
        }
    }

    // ----- signals -----

    private void OnSignal(uint from, PeerSignal signal)
    {
        var key = (from, signal.Stream);
        if (signal.Kind == PeerSignal.KindOffer)
        {
            AcceptOffer(from, signal);
            return;
        }
        DirectEntry? entry;
        lock (_directSync)
            _direct.TryGetValue(key, out entry);
        if (entry is null || entry.Link.Session != signal.Session)
            return;
        _ = entry.Link.HandleAsync(signal);
    }

    private void AcceptOffer(uint from, PeerSignal offer)
    {
        string? refusal = null;
        if (!_directEnabled)
            refusal = "off";
        else
        {
            var stream = FindStream(offer.Stream);
            lock (_directSync)
            {
                if (stream is null || stream.OwnerId != from || !_watched.Contains(offer.Stream))
                    refusal = "not_watching";
            }
        }
        if (refusal is not null)
        {
            Diag.Log($"p2p: declined a direct link from {from} for #{offer.Stream} ({refusal})");
            _client?.SendSignal(new PeerSignal { To = from, Stream = offer.Stream, Session = offer.Session, Kind = PeerSignal.KindBye, Data = refusal });
            return;
        }

        DirectEntry? old;
        PeerLink link;
        lock (_directSync)
        {
            _direct.TryGetValue((from, offer.Stream), out old);
            link = NewLink(from, offer.Stream, offer.Session, offerer: false);
            _direct[(from, offer.Stream)] = new DirectEntry(link, isPublisher: false);
        }
        old?.Link.Close("local:replaced", notifyPeer: false);
        Diag.Log($"p2p: accepting a direct link from {from} ({NameOf(from)}) for #{offer.Stream}");
        _ = link.HandleAsync(offer);
    }

    // ----- link events -----

    private PeerLink NewLink(uint peerId, uint streamId, uint session, bool offerer)
    {
        var client = _client;
        var link = new PeerLink(peerId, streamId, session, offerer, signal => client?.SendSignal(signal));
        link.Opened += OnLinkOpened;
        link.Received += OnLinkReceived;
        link.Closed += OnLinkClosed;
        return link;
    }

    private DirectEntry? EntryOf(PeerLink link)
    {
        lock (_directSync)
            return _direct.TryGetValue((link.PeerId, link.StreamId), out var entry) && ReferenceEquals(entry.Link, link) ? entry : null;
    }

    private void OnLinkOpened(PeerLink link)
    {
        if (EntryOf(link) is not { } entry)
            return;
        if (!entry.IsPublisher)
            return; // the viewer switches on the first keyframe that arrives
        lock (_directSync)
            _directRetry.Remove((link.PeerId, link.StreamId));
        entry.AwaitingKeyframe = true;
        entry.WasOpen = true;
        UpdateKnownViewer(link.StreamId, link.PeerId);
        KeyframeRequested?.Invoke(link.StreamId);
        var name = NameOf(link.PeerId);
        Post(() => PathChanged?.Invoke(new DirectPathChange(link.StreamId, link.PeerId, name, Direct: true, AsPublisher: true, string.Empty)));
    }

    private void OnLinkReceived(PeerLink link, byte[] frame)
    {
        if (EntryOf(link) is not { } entry || !LoungeMux.TryDecode(frame, out var kind, out _, out var b, out var payload))
            return;
        if (entry.IsPublisher)
        {
            if (kind == LoungeMux.KeyframeRequest)
            {
                entry.AwaitingKeyframe = true;
                KeyframeRequested?.Invoke(link.StreamId);
            }
            else if (kind == LoungeMux.ViewerReport)
            {
                OnViewerReport(link.StreamId, link.PeerId, (int)Math.Min(b, int.MaxValue));
            }
            return;
        }

        if (kind != LoungeMux.Media || _client is not { } client || !client.TryOpenMedia(payload, out var type, out var keyframe, out var body))
            return;
        lock (_deliverSync)
        {
            if (!entry.Active)
            {
                if (type != MessageType.Video || !keyframe)
                    return; // the relay still feeds the decoder until a clean switch point
                entry.Active = true;
                client.Unsubscribe(link.StreamId);
                Diag.Log($"p2p: #{link.StreamId} now comes directly from {link.PeerId}; relay copy off");
                var name = NameOf(link.PeerId);
                Post(() => PathChanged?.Invoke(new DirectPathChange(link.StreamId, link.PeerId, name, Direct: true, AsPublisher: false, string.Empty)));
            }
            MediaReceived?.Invoke(link.StreamId, type, keyframe, body, b);
        }
    }

    private void OnLinkClosed(PeerLink link, string reason)
    {
        DirectEntry? entry;
        lock (_directSync)
        {
            entry = _direct.TryGetValue((link.PeerId, link.StreamId), out var current) && ReferenceEquals(current.Link, link) ? current : null;
            if (entry is not null)
                _direct.Remove((link.PeerId, link.StreamId));
        }
        SafeTry.Run(link.Dispose);
        if (entry is null)
            return;
        var name = NameOf(link.PeerId);
        if (entry.IsPublisher)
        {
            var stopped = reason.EndsWith("stopped", StringComparison.Ordinal);
            if (stopped)
            {
                UpdateKnownViewer(link.StreamId, link.PeerId);
            }
            else
            {
                // A broken link is usually followed at once by the viewer coming back through the
                // host; give it a moment so the viewer list does not blink "left" and "joined".
                _ = Task.Delay(3000).ContinueWith(_ => UpdateKnownViewer(link.StreamId, link.PeerId));
                if (!reason.StartsWith("local:", StringComparison.Ordinal))
                    ScheduleDirectRetry(link.PeerId, link.StreamId, reason);
            }
            if (entry.WasOpen && !stopped && !reason.StartsWith("local:", StringComparison.Ordinal))
                Post(() => PathChanged?.Invoke(new DirectPathChange(link.StreamId, link.PeerId, name, Direct: false, AsPublisher: true, reason)));
            return;
        }

        if (!entry.Active)
            return;
        bool watching;
        lock (_directSync)
            watching = _watched.Contains(link.StreamId);
        if (watching)
        {
            // Back to the host's copy; it answers a subscribe with a keyframe.
            Diag.Log($"p2p: direct link for #{link.StreamId} lost ({reason}); back on the relay");
            _client?.Subscribe(link.StreamId);
            Post(() => PathChanged?.Invoke(new DirectPathChange(link.StreamId, link.PeerId, name, Direct: false, AsPublisher: false, reason)));
        }
    }

    // ----- membership -----

    private void CloseDirectWith(uint memberId)
    {
        List<DirectEntry> links;
        lock (_directSync)
        {
            links = _direct.Where(p => p.Key.Peer == memberId).Select(p => p.Value).ToList();
            foreach (var key in _directRetry.Keys.Where(k => k.Peer == memberId).ToList())
                _directRetry.Remove(key);
            foreach (var set in _relayViewers.Values)
                set.Remove(memberId);
        }
        foreach (var entry in links)
            entry.Link.Close("local:member left", notifyPeer: false);
    }

    /// <summary>Publisher: my stream ended; its links go with it.</summary>
    private void CloseDirectFor(uint streamId)
    {
        List<DirectEntry> links;
        lock (_directSync)
        {
            links = _direct.Values.Where(e => e.IsPublisher && e.Link.StreamId == streamId).ToList();
            _relayViewers.Remove(streamId);
            _knownViewers.Remove(streamId);
            foreach (var key in _directRetry.Keys.Where(k => k.Stream == streamId).ToList())
                _directRetry.Remove(key);
        }
        foreach (var entry in links)
            entry.Link.Close("local:stopped", notifyPeer: true);
    }

    private void CloseAllDirect(string reason, bool notifyPeer)
    {
        List<DirectEntry> links;
        lock (_directSync)
            links = _direct.Values.ToList();
        foreach (var entry in links)
            entry.Link.Close("local:" + reason, notifyPeer);
    }

    /// <summary>The connection to the host is gone: member and stream ids will be new, so every link and list starts over.</summary>
    private void ResetDirect()
    {
        List<DirectEntry> links;
        lock (_directSync)
        {
            links = _direct.Values.ToList();
            _direct.Clear();
            _relayViewers.Clear();
            _knownViewers.Clear();
            _directRetry.Clear();
            _viewerDelay.Clear();
            _watched.Clear();
        }
        foreach (var entry in links)
        {
            SafeTry.Run(() => entry.Link.Close("local:reconnect", notifyPeer: false));
            SafeTry.Run(entry.Link.Dispose);
        }
    }
}
