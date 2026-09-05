using System.Text.Json;
using Beamcast.Net;

namespace Beamcast.Server;

public sealed class ServerOptions
{
    /// <summary>When set, every client must present it before anything else.</summary>
    public string? AppKey { get; init; }

    /// <summary>Shown to apps in the host info; defaults to the machine name.</summary>
    public string HostName { get; init; } = Environment.MachineName;

    public string DataDirectory { get; init; } = "data";

    /// <summary>Default lifetime of an empty temporary room when the creator gives none.</summary>
    public double DefaultTemporaryTtlHours { get; init; } = LoungeProtocol.DefaultTtlHours;
}

public sealed class InviteRecord
{
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public int MaxUses { get; set; }
    public int Uses { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) =>
        (ExpiresAt is null || ExpiresAt > now) && (MaxUses <= 0 || Uses < MaxUses);
}

/// <summary>
/// What survives a restart: enough to let members come back with the same code and password and
/// the owner keep managing the room. Never a password, key or any content.
/// </summary>
public sealed class RoomRecord
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Visibility { get; set; } = RoomVisibility.Private;
    public string Kind { get; set; } = RoomKind.Permanent;
    public double TtlHours { get; set; } = LoungeProtocol.DefaultTtlHours;
    public string Broadcast { get; set; } = BroadcastPolicy.Everyone;
    public int MaxMembers { get; set; }

    /// <summary>Base64; empty when the room has no password.</summary>
    public string Salt { get; set; } = string.Empty;
    public string Verifier { get; set; } = string.Empty;

    public string OwnerTokenHash { get; set; } = string.Empty;
    public List<InviteRecord> Invites { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActiveAt { get; set; }
}

/// <summary>Tiny JSON file store. Reads the v2 file (lounges.json) once so old rooms survive the upgrade.</summary>
public sealed class LoungeStore
{
    private readonly string _path;
    private readonly string _legacyPath;
    private readonly ILogger<LoungeStore> _log;
    private readonly object _sync = new();

    public LoungeStore(ServerOptions options, ILogger<LoungeStore> log)
    {
        _path = Path.Combine(options.DataDirectory, "rooms.json");
        _legacyPath = Path.Combine(options.DataDirectory, "lounges.json");
        _log = log;
    }

    public List<RoomRecord> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<RoomRecord>>(File.ReadAllText(_path)) ?? [];
            if (File.Exists(_legacyPath))
            {
                var legacy = JsonSerializer.Deserialize<List<RoomRecord>>(File.ReadAllText(_legacyPath)) ?? [];
                _log.LogInformation("Migrated {Count} room(s) from the v2 data file.", legacy.Count);
                return legacy;
            }
            return [];
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read {Path}; starting empty.", _path);
            return [];
        }
    }

    public void Save(IEnumerable<RoomRecord> records)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(records.ToList(), new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not write {Path}.", _path);
            }
        }
    }
}
