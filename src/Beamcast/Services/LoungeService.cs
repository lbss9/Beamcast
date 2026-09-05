using Beamcast.Net;
using Microsoft.UI.Dispatching;

namespace Beamcast.Services;

public enum LoungeState
{
    Disconnected,
    Connecting,
    Connected,
}

public sealed class LoungeMember
{
    public uint Id { get; init; }
    public string Name { get; set; } = string.Empty;
    public bool IsMe { get; init; }
}

public sealed class LoungeStream
{
    public uint Id { get; init; }
    public uint OwnerId { get; init; }
    public string OwnerName { get; set; } = string.Empty;
    public StreamMetaMessage Meta { get; set; } = new();
    public bool IsMine { get; init; }
}

/// <summary>
/// The member's seat in a lounge: who is here, what is being streamed, and the pipe the
/// broadcast and watch services publish to and subscribe from. Lives for the whole process.
/// Events are raised on the UI thread except <see cref="MediaReceived"/> and
/// <see cref="KeyframeRequested"/>, which stay on the network thread for latency.
/// </summary>
public sealed class LoungeService
{
    public static LoungeService Instance { get; } = new();

    private readonly object _sync = new();
    private readonly Dictionary<uint, LoungeMember> _members = new();
    private readonly Dictionary<uint, LoungeStream> _streams = new();
    private DispatcherQueue? _ui;
    private LoungeClient? _client;
    private string _displayName = string.Empty;

    private LoungeService() { }

    public event Action<LoungeState>? StateChanged;
    public event Action? MembersChanged;
    public event Action? StreamsChanged;
    public event Action<uint>? StreamEnded;
    public event Action<string>? Closed;
    public event Action<uint, MessageType, bool, byte[]>? MediaReceived;
    public event Action<uint>? KeyframeRequested;

    public LoungeState State { get; private set; }
    public string Code => _client?.Code ?? string.Empty;
    public string Name => _client?.Name ?? string.Empty;
    public string ServerUrl => _client?.ServerUrl ?? string.Empty;
    public uint MemberId => _client?.MemberId ?? 0;
    public bool IsConnected => State == LoungeState.Connected && _client?.IsOpen == true;

    public IReadOnlyList<LoungeMember> Members
    {
        get
        {
            lock (_sync)
                return _members.Values.OrderByDescending(m => m.IsMe).ThenBy(m => m.Name).ToList();
        }
    }

    public IReadOnlyList<LoungeStream> Streams
    {
        get
        {
            lock (_sync)
                return _streams.Values.OrderBy(s => s.Id).ToList();
        }
    }

    public string InviteCode => _client is null ? string.Empty : LoungeInvite.Encode(new LoungeTarget(_client.ServerUrl, _client.Code));

    public void Initialize(DispatcherQueue ui) => _ui = ui;

    public Task CreateAsync(string serverUrl, string loungeName, string password, string displayName, CancellationToken ct) =>
        ConnectAsync(() => LoungeClient.CreateAsync(serverUrl, loungeName, password, SettingsStore.Load().RelayAppKey, ct), displayName);

    public Task JoinAsync(string serverUrl, string code, string password, string displayName, CancellationToken ct) =>
        ConnectAsync(() => LoungeClient.JoinAsync(serverUrl, code, password, SettingsStore.Load().RelayAppKey, ct), displayName);

    private async Task ConnectAsync(Func<Task<LoungeClient>> connect, string displayName)
    {
        if (State != LoungeState.Disconnected)
            throw new InvalidOperationException("Already in a lounge.");

        _displayName = displayName.Trim().Length == 0 ? Environment.UserName : displayName.Trim();
        SetState(LoungeState.Connecting);
        LoungeClient client;
        try
        {
            client = await connect();
        }
        catch
        {
            SetState(LoungeState.Disconnected);
            throw;
        }

        lock (_sync)
        {
            _members.Clear();
            _streams.Clear();
            _members[client.MemberId] = new LoungeMember { Id = client.MemberId, Name = _displayName, IsMe = true };
            foreach (var info in client.InitialMembers)
            {
                var presence = client.Open<PresenceMessage>(info.Presence);
                _members[info.Id] = new LoungeMember { Id = info.Id, Name = presence?.Name ?? Placeholder(info.Id) };
            }
            foreach (var info in client.InitialStreams)
            {
                var meta = client.Open<StreamMetaMessage>(info.Meta) ?? new StreamMetaMessage();
                _streams[info.Id] = new LoungeStream { Id = info.Id, OwnerId = info.Owner, OwnerName = NameOf(info.Owner), Meta = meta, IsMine = info.Owner == client.MemberId };
            }
        }

        client.MemberJoined += OnMemberJoined;
        client.MemberLeft += OnMemberLeft;
        client.PresenceReceived += OnPresence;
        client.StreamStarted += OnStreamStarted;
        client.StreamEnded += OnStreamEnded;
        client.StreamMetaUpdated += OnStreamMetaUpdated;
        client.MediaReceived += (id, type, key, body) => MediaReceived?.Invoke(id, type, key, body);
        client.KeyframeRequested += id => KeyframeRequested?.Invoke(id);
        client.Closed += reason => Post(() => OnClosed(client, reason));

        _client = client;
        client.SendPresence(new PresenceMessage { Name = _displayName, AppVersion = AppInfo.Version });
        SetState(LoungeState.Connected);
        Post(() =>
        {
            MembersChanged?.Invoke();
            StreamsChanged?.Invoke();
        });
    }

    public async Task LeaveAsync()
    {
        var client = _client;
        if (client is null)
            return;
        await client.LeaveAsync();
    }

    private string Placeholder(uint id) => Loc.Format("Lounge_MemberPlaceholder", id);

    private string NameOf(uint memberId)
    {
        lock (_sync)
            return _members.TryGetValue(memberId, out var m) ? m.Name : Placeholder(memberId);
    }

    private void OnMemberJoined(uint id)
    {
        lock (_sync)
        {
            if (!_members.ContainsKey(id))
                _members[id] = new LoungeMember { Id = id, Name = Placeholder(id) };
        }
        // Newcomers need our name too.
        _client?.SendPresence(new PresenceMessage { Name = _displayName, AppVersion = AppInfo.Version });
        Post(() => MembersChanged?.Invoke());
    }

    private void OnMemberLeft(uint id)
    {
        lock (_sync)
        {
            _members.Remove(id);
        }
        Post(() => MembersChanged?.Invoke());
    }

    private void OnPresence(uint id, PresenceMessage presence)
    {
        var name = presence.Name.Trim();
        if (name.Length == 0)
            name = Placeholder(id);
        if (name.Length > 32)
            name = name[..32];
        lock (_sync)
        {
            if (_members.TryGetValue(id, out var member))
                member.Name = name;
            else
                _members[id] = new LoungeMember { Id = id, Name = name };
            foreach (var stream in _streams.Values.Where(s => s.OwnerId == id))
                stream.OwnerName = name;
        }
        Post(() =>
        {
            MembersChanged?.Invoke();
            StreamsChanged?.Invoke();
        });
    }

    private void OnStreamStarted(uint streamId, uint owner, StreamMetaMessage meta)
    {
        lock (_sync)
        {
            _streams[streamId] = new LoungeStream { Id = streamId, OwnerId = owner, OwnerName = NameOf(owner), Meta = meta, IsMine = owner == MemberId };
        }
        Post(() => StreamsChanged?.Invoke());
    }

    private void OnStreamEnded(uint streamId)
    {
        lock (_sync)
        {
            _streams.Remove(streamId);
        }
        Post(() =>
        {
            StreamEnded?.Invoke(streamId);
            StreamsChanged?.Invoke();
        });
    }

    private void OnStreamMetaUpdated(uint streamId, StreamMetaMessage meta)
    {
        lock (_sync)
        {
            if (_streams.TryGetValue(streamId, out var stream))
                stream.Meta = meta;
        }
        Post(() => StreamsChanged?.Invoke());
    }

    /// <summary>Registers the caller's own stream locally so the list shows it right away.</summary>
    public void RegisterOwnStream(uint streamId, StreamMetaMessage meta)
    {
        lock (_sync)
        {
            _streams[streamId] = new LoungeStream { Id = streamId, OwnerId = MemberId, OwnerName = _displayName, Meta = meta, IsMine = true };
        }
        Post(() => StreamsChanged?.Invoke());
    }

    public void ForgetOwnStream(uint streamId)
    {
        lock (_sync)
        {
            _streams.Remove(streamId);
        }
        Post(() => StreamsChanged?.Invoke());
    }

    private void OnClosed(LoungeClient client, string reason)
    {
        if (!ReferenceEquals(_client, client))
            return;
        _client = null;
        client.Dispose();
        lock (_sync)
        {
            _members.Clear();
            _streams.Clear();
        }
        SetState(LoungeState.Disconnected);
        MembersChanged?.Invoke();
        StreamsChanged?.Invoke();
        Closed?.Invoke(reason);
    }

    // Pipe for the broadcast/watch services.
    public Task<uint> PublishAsync(StreamMetaMessage meta, CancellationToken ct) =>
        (_client ?? throw new InvalidOperationException("Not in a lounge.")).PublishAsync(meta, ct);

    public void Unpublish(uint streamId) => _client?.Unpublish(streamId);

    public void UpdateStreamMeta(uint streamId, StreamMetaMessage meta)
    {
        lock (_sync)
        {
            if (_streams.TryGetValue(streamId, out var stream))
                stream.Meta = meta;
        }
        _client?.UpdateStreamMeta(streamId, meta);
        Post(() => StreamsChanged?.Invoke());
    }

    public void SendMedia(uint streamId, MessageType type, ReadOnlySpan<byte> body, bool keyframe) =>
        _client?.SendMedia(streamId, type, body, keyframe);

    public int PendingVideo(uint streamId) => _client?.PendingVideo(streamId) ?? 0;

    public void Subscribe(uint streamId) => _client?.Subscribe(streamId);

    public void Unsubscribe(uint streamId) => _client?.Unsubscribe(streamId);

    public void RequestKeyframe(uint streamId) => _client?.RequestKeyframe(streamId);

    public LoungeStream? FindStream(uint streamId)
    {
        lock (_sync)
            return _streams.TryGetValue(streamId, out var s) ? s : null;
    }

    private void SetState(LoungeState state)
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
