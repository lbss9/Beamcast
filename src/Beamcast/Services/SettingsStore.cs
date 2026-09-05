using System.Text.Json;

namespace Beamcast;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON in LocalAppData.</summary>
public static class SettingsStore
{
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
        if (!Net.InviteCode.IsValidPort(settings.Port))
            settings.Port = AppInfo.DefaultPort;
        settings.SessionName = (settings.SessionName ?? string.Empty).Trim();
        settings.Password ??= string.Empty;
        settings.QualityPreset = QualityPreset.Normalize(settings.QualityPreset);
        settings.Fps = QualityPreset.NormalizeFps(settings.Fps);
        settings.BitrateKbps = QualityPreset.ClampBitrate(settings.BitrateKbps);
        settings.MaxViewers = Math.Clamp(settings.MaxViewers, 1, 100);
        settings.LastInvite ??= string.Empty;
        settings.ConnectionMode = string.Equals(settings.ConnectionMode, "Direct", StringComparison.OrdinalIgnoreCase) ? "Direct" : "Relay";
        settings.RelayUrl = Net.InviteCode.IsValidRelayUrl(settings.RelayUrl?.Trim()) ? settings.RelayUrl!.Trim() : AppInfo.DefaultRelayUrl;
        settings.RelayAppKey = (settings.RelayAppKey ?? string.Empty).Trim();
        return settings;
    }
}
