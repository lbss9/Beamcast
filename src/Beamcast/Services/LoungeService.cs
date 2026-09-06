using Beamcast.Net;
using Microsoft.UI.Dispatching;

namespace Beamcast.Services;

public enum LoungeState
{
    Disconnected,
    Connecting,
    Connected,
    /// <summary>The connection dropped; the service is trying to get back into the same room.</summary>
    Reconnecting,
}

public sealed class LoungeMember
{
    public uint Id { get; init; }
    public string Name { get; set; } = string.Empty;
    public bool IsMe { get; init; }
    public bool IsOwner { get; init; }
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
/// The member's seat in a room: who is here, what is being streamed, the room's settings, and the
/// pipe the broadcast and watch services publish to and subscribe from. Lives for the whole
/// process. When the connection drops it keeps the seat and reconnects on its own; the broadcast
/// and watch services listen to <see cref="Reconnected"/> to pick up where they were.
/// Events are raised on the UI thread except <see cref="MediaReceived"/> and
/// <see cref="KeyframeRequested"/>, which stay on the network thread for latency.
/// </summary>
public sealed class LoungeService
{
    public static LoungeService Instance { get; } = new();

    private static readonly TimeSpan ReconnectBudget = TimeSpan.FromMinutes(5);
    private static readonly int[] ReconnectDelaysSeconds = [1, 2, 4, 8, 15];

    private readonly object _sync = new();
    private readonly Dictionary<uint, LoungeMember> _members = new();
    private readonly Dictionary<uint, LoungeStream> _streams = new();
    private DispatcherQueue? _ui;
    private LoungeClient? _client;
    private Session? _session;
    private CancellationTokenSource? _reconnectCts;
    private string _displayName = string.Empty;

    private LoungeService() { }

    public event Action<LoungeState>? StateChanged;
    public event Action? MembersChanged;
    public event Action? StreamsChanged;
    public event Action<uint>? StreamEnded;
    public event Action<RoomInfo>? RoomChanged;
    public event Action<string>? Closed;
    public event Action<string>? Notice;
    /// <summary>Back in the same room after a drop: member and stream ids are new.</summary>
    public event Action? Reconnected;
    public event Action<uint, MessageType, bool, byte[]>? MediaReceived;
    public event Action<uint>? KeyframeRequested;

    public LoungeState State { get; private set; }
    public RoomInfo Room => _client?.Room ?? _session?.LastRoom ?? new RoomInfo();
    public string Code => Room.Code;
    public string Name => Room.Name;
    public string ServerUrl => _client?.ServerUrl ?? _session?.ServerUrl ?? string.Empty;
    public uint MemberId => _client?.MemberId ?? 0;
    public bool IsOwner => _client?.IsOwner ?? _session?.IsOwner ?? false;
    public bool IsConnected => State == LoungeState.Connected && _client?.IsOpen == true;
    public bool InRoom => State is LoungeState.Connected or LoungeState.Reconnecting;
    public bool CanBroadcast => Room.Broadcast != BroadcastPolicy.Owner || IsOwner;

    public IReadOnlyList<LoungeMember> Members
    {
        get
        {
            lock (_sync)
                return _members.Values.OrderByDescending(m => m.IsMe).ThenByDescending(m => m.IsOwner).ThenBy(m => m.Name).ToList();
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

    /// <summary>A plain pointer to the room (host + code): no token, no key.</summary>
    public string InviteCode => ServerUrl.Length == 0 ? string.Empty : LoungeInvite.Encode(new LoungeTarget(ServerUrl, Code));

    public void Initialize(DispatcherQueue ui) => _ui = ui;

    // ----- hosts -----

    /// <summary>The app key saved for this host (BEAMCAST_APP_KEY on that server), or empty.</summary>
    public static string AppKeyFor(string serverUrl)
    {
        var host = SettingsStore.Load().Hosts.FirstOrDefault(h => string.Equals(h.Url, serverUrl, StringComparison.OrdinalIgnoreCase));
        return host is null ? string.Empty : SecretStore.Unprotect(host.ProtectedAppKey);
    }

    public Task<HostInfo> ListRoomsAsync(string serverUrl, CancellationToken ct)
    {
        if (!LoungeProtocol.TryNormalizeServer(serverUrl, out var url))
            throw new LoungeException(LoungeProtocol.ReasonBadRequest);
        return LoungeClient.ListRoomsAsync(url, AppKeyFor(url), ct);
    }

    public static void RememberHost(string serverUrl, string? name = null, string? appKey = null, bool? favorite = null)
    {
        if (!LoungeProtocol.TryNormalizeServer(serverUrl, out var url))
            return;
        SettingsStore.Update(s =>
        {
            var host = s.Hosts.FirstOrDefault(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase));
            if (host is null)
            {
                host = new SavedHost { Url = url, Name = LoungeProtocol.DisplayHost(url) };
                s.Hosts.Add(host);
            }
            if (!string.IsNullOrWhiteSpace(name))
                host.Name = name.Trim();
            if (appKey is not null)
                host.ProtectedAppKey = SecretStore.Protect(appKey.Trim());
            if (favorite is { } fav)
                host.Favorite = fav;
            host.LastUsedAt = DateTimeOffset.UtcNow;
            s.RelayUrl = url;
        });
    }

    public static void ForgetHost(string serverUrl) =>
        SettingsStore.Update(s => s.Hosts.RemoveAll(h => string.Equals(h.Url, serverUrl, StringComparison.OrdinalIgnoreCase)));

    // ----- favorites -----

    public static bool IsFavorite(string serverUrl, string code) =>
        SettingsStore.Load().FavoriteRooms.Any(r => Same(r.ServerUrl, r.Code, serverUrl, code));

    public static void SetFavorite(string serverUrl, string code, string name, bool hasPassword, bool favorite, string? rememberPassword = null)
    {
        SettingsStore.Update(s =>
        {
            s.FavoriteRooms.RemoveAll(r => Same(r.ServerUrl, r.Code, serverUrl, code));
            if (!favorite)
                return;
            s.FavoriteRooms.Insert(0, new SavedRoom
            {
                ServerUrl = serverUrl,
                Code = code,
                Name = name,
                HasPassword = hasPassword,
                ProtectedPassword = rememberPassword is { Length: > 0 } ? SecretStore.Protect(rememberPassword) : string.Empty,
                LastUsedAt = DateTimeOffset.UtcNow,
            });
        });
    }

    public static string RememberedPassword(string serverUrl, string code)
    {
        var saved = SettingsStore.Load().FavoriteRooms.FirstOrDefault(r => Same(r.ServerUrl, r.Code, serverUrl, code));
        return saved is null ? string.Empty : SecretStore.Unprotect(saved.ProtectedPassword);
    }

    public static string? OwnerTokenFor(string serverUrl, string code)
    {
        var owned = SettingsStore.Load().OwnedRooms.FirstOrDefault(r => Same(r.ServerUrl, r.Code, serverUrl, code));
        var token = owned is null ? string.Empty : SecretStore.Unprotect(owned.ProtectedToken);
        return token.Length == 0 ? null : token;
    }

    /// <summary>Drops a room we no longer own (deleted, or the host stopped recognising our token).</summary>
    public static void ForgetOwnedRoom(string serverUrl, string code) =>
        SettingsStore.Update(s =>
        {
            s.OwnedRooms.RemoveAll(r => Same(r.ServerUrl, r.Code, serverUrl, code));
            s.FavoriteRooms.RemoveAll(r => Same(r.ServerUrl, r.Code, serverUrl, code));
        });

    private static bool Same(string url1, string code1, string url2, string code2) =>
        string.Equals(url1, url2, StringComparison.OrdinalIgnoreCase) && string.Equals(code1, code2, StringComparison.Ordinal);

    // ----- connect -----

    public async Task CreateAsync(string serverUrl, RoomCreateOptions options, string displayName, CancellationToken ct)
    {
        if (!LoungeProtocol.TryNormalizeServer(serverUrl, out var url))
            throw new LoungeException(LoungeProtocol.ReasonBadRequest);
        var appKey = AppKeyFor(url);
        var client = await ConnectAsync(() => LoungeClient.CreateAsync(url, options, appKey, ct), displayName);

        if (client.OwnerToken is { Length: > 0 } token)
        {
            SettingsStore.Update(s =>
            {
                s.OwnedRooms.RemoveAll(r => Same(r.ServerUrl, r.Code, url, client.Code));
                s.OwnedRooms.Add(new OwnedRoom { ServerUrl = url, Code = client.Code, Name = client.Name, ProtectedToken = SecretStore.Protect(token) });
            });
        }
        _session = new Session(url, appKey, new RoomJoinOptions
        {
            Code = client.Code,
            PasswordKey = client.PasswordKey,
            OwnerToken = client.OwnerToken,
        })
        { LastRoom = client.Room, IsOwner = true };
        RememberHost(url);
        SettingsStore.Update(s =>
        {
            s.LastLoungeCode = client.Code;
            s.LastLoungeName = client.Name;
        });
    }

    public async Task JoinAsync(string serverUrl, RoomJoinOptions options, string displayName, CancellationToken ct)
    {
        if (!LoungeProtocol.TryNormalizeServer(serverUrl, out var url))
            throw new LoungeException(LoungeProtocol.ReasonBadRequest);
        var appKey = AppKeyFor(url);
        options.Code = LoungeProtocol.NormalizeCode(options.Code);
        options.OwnerToken ??= OwnerTokenFor(url, options.Code);
        var client = await ConnectAsync(() => LoungeClient.JoinAsync(url, options, appKey, ct), displayName);

        _session = new Session(url, appKey, new RoomJoinOptions
        {
            Code = client.Code,
            PasswordKey = client.PasswordKey,
            InviteToken = options.InviteToken,
            InviteKey = options.InviteKey,
            OwnerToken = options.OwnerToken,
        })
        { LastRoom = client.Room, IsOwner = client.IsOwner };
        RememberHost(url);
        SettingsStore.Update(s =>
        {
            s.LastLoungeCode = client.Code;
            s.LastLoungeName = client.Name;
            var favorite = s.FavoriteRooms.FirstOrDefault(r => Same(r.ServerUrl, r.Code, url, client.Code));
            if (favorite is not null)
            {
                favorite.Name = client.Name;
                favorite.HasPassword = client.Room.HasPassword;
                favorite.LastUsedAt = DateTimeOffset.UtcNow;
            }
        });
    }

    private async Task<LoungeClient> ConnectAsync(Func<Task<LoungeClient>> connect, string displayName)
    {
        if (State != LoungeState.Disconnected)
            throw new InvalidOperationException("Already in a room.");

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

        Attach(client);
        SetState(LoungeState.Connected);
        Post(() =>
        {
            MembersChanged?.Invoke();
            StreamsChanged?.Invoke();
            RoomChanged?.Invoke(client.Room);
        });
        return client;
    }

    private void Attach(LoungeClient client)
    {
        lock (_sync)
        {
            _members.Clear();
            _streams.Clear();
            _members[client.MemberId] = new LoungeMember { Id = client.MemberId, Name = _displayName, IsMe = true, IsOwner = client.IsOwner };
            foreach (var info in client.InitialMembers)
            {
                var presence = client.Open<PresenceMessage>(info.Presence);
                _members[info.Id] = new LoungeMember { Id = info.Id, Name = presence?.Name ?? Placeholder(info.Id), IsOwner = info.IsOwner };
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
        client.RoomUpdated += info =>
        {
            if (_session is not null)
                _session.LastRoom = info;
            Post(() => RoomChanged?.Invoke(info));
        };
        client.Notice += reason => Post(() => Notice?.Invoke(reason));
        client.MediaReceived += (id, type, key, body) => MediaReceived?.Invoke(id, type, key, body);
        client.KeyframeRequested += id => KeyframeRequested?.Invoke(id);
        client.Closed += reason => Post(() => OnClosed(client, reason));

        _client = client;
        client.SendPresence(new PresenceMessage { Name = _displayName, AppVersion = AppInfo.Version });
    }

    public async Task LeaveAsync()
    {
        _session = null;
        var reconnect = _reconnectCts;
        if (reconnect is not null)
        {
            reconnect.Cancel();
            return;
        }
        var client = _client;
        if (client is null)
            return;
        await client.LeaveAsync();
    }

    // ----- reconnection -----

    private void OnClosed(LoungeClient client, string reason)
    {
        if (!ReferenceEquals(_client, client))
            return;
        _client = null;
        client.Dispose();

        var session = _session;
        if (session is not null && reason is "lost" or "timeout" or "closed")
        {
            lock (_sync)
            {
                _members.Clear();
            }
            SetState(LoungeState.Reconnecting);
            MembersChanged?.Invoke();
            _reconnectCts = new CancellationTokenSource();
            _ = ReconnectAsync(session, _reconnectCts.Token);
            return;
        }

        Finish(reason);
    }

    private void Finish(string reason)
    {
        _session = null;
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

    private async Task ReconnectAsync(Session session, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var attempt = 0;
        var reason = "lost";
        try
        {
            while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow - started < ReconnectBudget)
            {
                var delay = ReconnectDelaysSeconds[Math.Min(attempt, ReconnectDelaysSeconds.Length - 1)];
                attempt++;
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                if (!ReferenceEquals(_session, session))
                    return;

                LoungeClient client;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    client = await LoungeClient.JoinAsync(session.ServerUrl, session.Join, session.AppKey, timeout.Token);
                }
                catch (LoungeException ex) when (ex.Reason is "unreachable" or "timeout" or LoungeProtocol.ReasonRateLimited or LoungeProtocol.ReasonNoKey)
                {
                    continue;
                }
                catch (LoungeException ex)
                {
                    reason = ex.Reason;
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!ReferenceEquals(_session, session) || ct.IsCancellationRequested)
                {
                    client.Dispose();
                    return;
                }

                session.LastRoom = client.Room;
                session.IsOwner = client.IsOwner;
                session.Join.PasswordKey = client.PasswordKey;
                Attach(client);
                _reconnectCts?.Dispose();
                _reconnectCts = null;
                SetState(LoungeState.Connected);
                Post(() =>
                {
                    MembersChanged?.Invoke();
                    StreamsChanged?.Invoke();
                    RoomChanged?.Invoke(client.Room);
                    Reconnected?.Invoke();
                });
                return;
            }
        }
        catch (OperationCanceledException)
        {
            reason = "left";
        }
        finally
        {
            _reconnectCts?.Dispose();
            _reconnectCts = null;
        }
        if (ct.IsCancellationRequested)
            reason = "left";
        Post(() => Finish(reason));
    }

    // ----- room events -----

    private string Placeholder(uint id) => Loc.Format("Lounge_MemberPlaceholder", id);

    private string NameOf(uint memberId)
    {
        lock (_sync)
            return _members.TryGetValue(memberId, out var m) ? m.Name : Placeholder(memberId);
    }

    private void OnMemberJoined(uint id, bool isOwner)
    {
        lock (_sync)
        {
            if (!_members.ContainsKey(id))
                _members[id] = new LoungeMember { Id = id, Name = Placeholder(id), IsOwner = isOwner };
        }
        // Newcomers need our name too.
        SafeTry.Run(() => _client?.SendPresence(new PresenceMessage { Name = _displayName, AppVersion = AppInfo.Version }));
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

    // ----- owner operations -----

    /// <summary>Creates an expiring invite and returns the shareable BC- string.</summary>
    public async Task<string> CreateInviteAsync(TimeSpan? expiresIn, int maxUses, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Not in a room.");
        var created = await client.CreateInviteAsync(expiresIn, maxUses, ct);
        var key = client.Room.HasPassword ? client.ContentKey : null;
        return LoungeInvite.Encode(new LoungeTarget(client.ServerUrl, client.Code, created.Token, key));
    }

    public void RevokeInvites() => _client?.RevokeInvites();

    public void Kick(uint memberId) => _client?.Kick(memberId);

    public void UpdateRoom(RoomUpdateMessage update) => _client?.UpdateRoom(update);

    public Task ChangePasswordAsync(string newPassword, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Not in a room.");
        return client.ChangePasswordAsync(newPassword, ct);
    }

    public async Task DeleteRoomAsync()
    {
        var client = _client;
        if (client is null)
            return;
        var url = client.ServerUrl;
        var code = client.Code;
        _session = null;
        client.DeleteRoom();
        await Task.Delay(300);
        SettingsStore.Update(s =>
        {
            s.OwnedRooms.RemoveAll(r => Same(r.ServerUrl, r.Code, url, code));
            s.FavoriteRooms.RemoveAll(r => Same(r.ServerUrl, r.Code, url, code));
        });
        await client.LeaveAsync();
    }

    // ----- pipe for the broadcast/watch services -----

    public Task<uint> PublishAsync(StreamMetaMessage meta, CancellationToken ct) =>
        (_client ?? throw new InvalidOperationException("Not in a room.")).PublishAsync(meta, ct);

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

    /// <summary>After a reconnect: the stream that looks like the one we were watching (same owner and title).</summary>
    public LoungeStream? FindStreamLike(string ownerName, string title)
    {
        lock (_sync)
        {
            return _streams.Values.FirstOrDefault(s => !s.IsMine && s.OwnerName == ownerName && s.Meta.Title == title)
                ?? _streams.Values.FirstOrDefault(s => !s.IsMine && s.OwnerName == ownerName);
        }
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

    private sealed class Session
    {
        public Session(string serverUrl, string appKey, RoomJoinOptions join)
        {
            ServerUrl = serverUrl;
            AppKey = appKey;
            Join = join;
        }

        public string ServerUrl { get; }
        public string AppKey { get; }
        public RoomJoinOptions Join { get; }
        public RoomInfo? LastRoom { get; set; }
        public bool IsOwner { get; set; }
    }
}
