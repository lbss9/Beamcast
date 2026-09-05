using System.Text.Json;
using Beamcast.Net;

namespace Beamcast;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON in LocalAppData.</summary>
public static class SettingsStore
{
    private const int MaxHosts = 50;
    private const int MaxRooms = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name);

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        var loaded = SafeTry.Run(() =>
        {
            if (!File.Exists(FilePath))
                return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        });

        return Sanitize(loaded ?? new AppSettings());
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(Sanitize(settings), JsonOptions);
        File.WriteAllText(FilePath, json);
    }

    public static void Update(Action<AppSettings> change)
    {
        var settings = Load();
        change(settings);
        Save(settings);
    }

    public static bool Exists() => File.Exists(FilePath);

    private static AppSettings Sanitize(AppSettings settings)
    {
        settings.Language = AppLanguage.Resolve(settings.Language) == settings.Language
            ? settings.Language
            : settings.Language is "System" or "" or null ? AppLanguage.System : settings.Language;
        settings.DisplayName = (settings.DisplayName ?? string.Empty).Trim();
        if (settings.DisplayName.Length == 0)
            settings.DisplayName = Environment.UserName;
        if (settings.DisplayName.Length > 32)
            settings.DisplayName = settings.DisplayName[..32];
        settings.QualityPreset = QualityPreset.Normalize(settings.QualityPreset);
        settings.Fps = QualityPreset.NormalizeFps(settings.Fps);
        settings.BitrateKbps = QualityPreset.ClampBitrate(settings.BitrateKbps);
        settings.RelayUrl = LoungeProtocol.TryNormalizeServer(settings.RelayUrl, out var server) ? server : string.Empty;
        settings.RelayAppKey = (settings.RelayAppKey ?? string.Empty).Trim();
        settings.LastLoungeCode = LoungeProtocol.NormalizeCode(settings.LastLoungeCode);
        settings.LastLoungeName = (settings.LastLoungeName ?? string.Empty).Trim();
        settings.StreamTitle = (settings.StreamTitle ?? string.Empty).Trim();
        settings.AudioMode = Audio.AudioMode.Normalize(settings.AudioMode);
        settings.Volume = Math.Clamp(settings.Volume, 0, 100);

        settings.Hosts = (settings.Hosts ?? [])
            .Where(h => LoungeProtocol.TryNormalizeServer(h.Url, out _))
            .Select(h =>
            {
                LoungeProtocol.TryNormalizeServer(h.Url, out var url);
                h.Url = url;
                h.Name = Trim(h.Name, 40);
                if (h.Name.Length == 0)
                    h.Name = LoungeProtocol.DisplayHost(url);
                h.AppKey = (h.AppKey ?? string.Empty).Trim();
                return h;
            })
            .GroupBy(h => h.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(h => h.LastUsedAt).First())
            .OrderByDescending(h => h.Favorite).ThenByDescending(h => h.LastUsedAt)
            .Take(MaxHosts)
            .ToList();

        // The last-used host is always in the book, so old settings files carry over.
        if (settings.RelayUrl.Length > 0 && !settings.Hosts.Any(h => string.Equals(h.Url, settings.RelayUrl, StringComparison.OrdinalIgnoreCase)))
        {
            settings.Hosts.Insert(0, new SavedHost
            {
                Url = settings.RelayUrl,
                Name = LoungeProtocol.DisplayHost(settings.RelayUrl),
                AppKey = settings.RelayAppKey,
                LastUsedAt = DateTimeOffset.UtcNow,
            });
        }

        settings.FavoriteRooms = (settings.FavoriteRooms ?? [])
            .Where(r => LoungeProtocol.TryNormalizeServer(r.ServerUrl, out _) && LoungeProtocol.IsValidCode(LoungeProtocol.NormalizeCode(r.Code)))
            .Select(r =>
            {
                LoungeProtocol.TryNormalizeServer(r.ServerUrl, out var url);
                r.ServerUrl = url;
                r.Code = LoungeProtocol.NormalizeCode(r.Code);
                r.Name = Trim(r.Name, LoungeProtocol.MaxNameLength);
                r.ProtectedPassword ??= string.Empty;
                return r;
            })
            .GroupBy(r => r.ServerUrl + "|" + r.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.LastUsedAt).First())
            .OrderByDescending(r => r.LastUsedAt)
            .Take(MaxRooms)
            .ToList();

        settings.OwnedRooms = (settings.OwnedRooms ?? [])
            .Where(r => LoungeProtocol.TryNormalizeServer(r.ServerUrl, out _) && LoungeProtocol.IsValidCode(LoungeProtocol.NormalizeCode(r.Code)) && !string.IsNullOrEmpty(r.ProtectedToken))
            .Select(r =>
            {
                LoungeProtocol.TryNormalizeServer(r.ServerUrl, out var url);
                r.ServerUrl = url;
                r.Code = LoungeProtocol.NormalizeCode(r.Code);
                r.Name = Trim(r.Name, LoungeProtocol.MaxNameLength);
                return r;
            })
            .GroupBy(r => r.ServerUrl + "|" + r.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(MaxRooms)
            .ToList();

        return settings;
    }

    private static string Trim(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length > max ? text[..max] : text;
    }
}
