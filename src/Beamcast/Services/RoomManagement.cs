using Beamcast.Net;

namespace Beamcast.Services;

/// <summary>
/// Short owner sessions used from the Rooms screen to edit or delete a room without entering it:
/// connect with the owner token, send the change, wait for the host to confirm it, leave. The
/// host treats the session like any member, so people inside see a brief join/leave.
/// </summary>
public static class RoomManagement
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Connects as the room's owner. Password rooms still challenge the owner (the key comes from
    /// the password), so this throws <see cref="LoungeException.PasswordRequired"/> or
    /// <c>bad_password</c> exactly like a normal join.
    /// </summary>
    public static async Task<LoungeClient> OpenAsync(string serverUrl, string code, string password, CancellationToken ct)
    {
        if (!LoungeProtocol.TryNormalizeServer(serverUrl, out var url))
            throw new LoungeException(LoungeProtocol.ReasonBadRequest);
        code = LoungeProtocol.NormalizeCode(code);
        var token = LoungeService.OwnerTokenFor(url, code) ?? throw new LoungeException(LoungeProtocol.ReasonNotAllowed);
        var options = new RoomJoinOptions { Code = code, Password = password, OwnerToken = token, ManageOnly = true };
        var client = await LoungeClient.JoinAsync(url, options, LoungeService.AppKeyFor(url), ct);
        if (!client.IsOwner)
        {
            // The host no longer recognises our token (room recreated with the same code, for instance).
            await client.LeaveAsync();
            client.Dispose();
            LoungeService.ForgetOwnedRoom(url, code);
            throw new LoungeException(LoungeProtocol.ReasonNotAllowed);
        }
        return client;
    }

    /// <summary>Applies the update (and a password change, when asked) and returns the room as the host now sees it.</summary>
    public static async Task<RoomInfo> UpdateAsync(LoungeClient client, RoomUpdateMessage update, string? newPassword, CancellationToken ct)
    {
        var expected = newPassword is null ? 1 : 2; // one RoomInfo per update message
        var seen = 0;
        var done = new TaskCompletionSource<RoomInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUpdated(RoomInfo info)
        {
            if (Interlocked.Increment(ref seen) >= expected)
                done.TrySetResult(info);
        }
        void OnClosed(string reason) => done.TrySetException(new LoungeException(reason));
        client.RoomUpdated += OnUpdated;
        client.Closed += OnClosed;
        try
        {
            client.UpdateRoom(update);
            if (newPassword is not null)
                await client.ChangePasswordAsync(newPassword, ct);
            var info = await WaitAsync(done.Task, ct);
            SettingsStore.Update(s =>
            {
                foreach (var owned in s.OwnedRooms.Where(r => Same(r.ServerUrl, r.Code, client.ServerUrl, client.Code)))
                    owned.Name = info.Name;
                foreach (var favorite in s.FavoriteRooms.Where(r => Same(r.ServerUrl, r.Code, client.ServerUrl, client.Code)))
                {
                    favorite.Name = info.Name;
                    favorite.HasPassword = info.HasPassword;
                    if (newPassword is not null)
                        favorite.ProtectedPassword = newPassword.Length == 0 ? string.Empty : SecretStore.Protect(newPassword);
                }
            });
            return info;
        }
        finally
        {
            client.RoomUpdated -= OnUpdated;
            client.Closed -= OnClosed;
        }
    }

    /// <summary>Deletes the room; the host answers by closing the session with <c>room_deleted</c>.</summary>
    public static async Task DeleteAsync(LoungeClient client, CancellationToken ct)
    {
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(string reason) => closed.TrySetResult(reason);
        client.Closed += OnClosed;
        try
        {
            client.DeleteRoom();
            var reason = await WaitAsync(closed.Task, ct);
            if (reason != LoungeProtocol.ReasonRoomDeleted)
                throw new LoungeException(reason);
            LoungeService.ForgetOwnedRoom(client.ServerUrl, client.Code);
        }
        finally
        {
            client.Closed -= OnClosed;
        }
    }

    /// <summary>Leaves politely and releases the socket; safe after the host already closed it.</summary>
    public static async Task CloseAsync(LoungeClient client)
    {
        await client.LeaveAsync();
        client.Dispose();
    }

    private static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConfirmTimeout);
        try
        {
            return await task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LoungeException("timeout");
        }
    }

    private static bool Same(string url1, string code1, string url2, string code2) =>
        string.Equals(url1, url2, StringComparison.OrdinalIgnoreCase) && string.Equals(code1, code2, StringComparison.Ordinal);
}
