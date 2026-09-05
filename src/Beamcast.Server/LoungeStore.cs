using System.Text.Json;

namespace Beamcast.Server;

public sealed class ServerOptions
{
    /// <summary>When set, every client must present it before anything else.</summary>
    public string? AppKey { get; init; }

    public string DataDirectory { get; init; } = "data";

    /// <summary>How long an empty lounge survives. Zero means forever (until the file is deleted).</summary>
    public TimeSpan EmptyLoungeTtl { get; init; }
}

/// <summary>What survives a restart: enough to let members come back with the same code and password.</summary>
public sealed class LoungeRecord
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Salt { get; set; } = string.Empty;
    public string Verifier { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActiveAt { get; set; }
}

/// <summary>Tiny JSON file store. Lounges hold no content, only the password verifier and a name.</summary>
public sealed class LoungeStore
{
    private readonly string _path;
    private readonly ILogger<LoungeStore> _log;
    private readonly object _sync = new();

    public LoungeStore(ServerOptions options, ILogger<LoungeStore> log)
    {
        _path = Path.Combine(options.DataDirectory, "lounges.json");
        _log = log;
    }

    public List<LoungeRecord> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return [];
            return JsonSerializer.Deserialize<List<LoungeRecord>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read {Path}; starting empty.", _path);
            return [];
        }
    }

    public void Save(IEnumerable<LoungeRecord> records)
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
