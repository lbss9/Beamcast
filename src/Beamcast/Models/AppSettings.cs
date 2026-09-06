using System.Text.Json.Serialization;

namespace Beamcast;

/// <summary>
/// A host (server) the person uses. Each host has its own app key (BEAMCAST_APP_KEY on that
/// server), kept DPAPI-protected for this Windows account like remembered room passwords.
/// </summary>
public sealed class SavedHost
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    /// <summary>Legacy plain-text key from settings written before 2.1.7; migrated into <see cref="ProtectedAppKey"/> on load.</summary>
    public string AppKey { get; set; } = string.Empty;

    public string ProtectedAppKey { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasAppKey => ProtectedAppKey.Length > 0;
    public bool Favorite { get; set; }
    public DateTimeOffset LastUsedAt { get; set; }
}

/// <summary>A room the person starred. The password, when remembered, is DPAPI-protected for this Windows account.</summary>
public sealed class SavedRoom
{
    public string ServerUrl { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool HasPassword { get; set; }
    public string ProtectedPassword { get; set; } = string.Empty;
    public DateTimeOffset LastUsedAt { get; set; }
}

/// <summary>A room this person created: the DPAPI-protected owner token is what lets them manage it.</summary>
public sealed class OwnedRoom
{
    public string ServerUrl { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ProtectedToken { get; set; } = string.Empty;
}

/// <summary>User preferences plus the last values used on the broadcast and watch screens.</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = AppLanguage.System;

    public string Theme { get; set; } = "System";

    public string DisplayName { get; set; } = string.Empty;

    public string QualityPreset { get; set; } = Beamcast.QualityPreset.Source;

    public int Fps { get; set; } = 60;

    public int BitrateKbps { get; set; } = 30000;

    /// <summary>Auto, H264, HEVC or VP8. Auto picks a GPU encoder when one exists.</summary>
    public string Encoder { get; set; } = "Auto";

    public bool ShowCursor { get; set; } = true;

    /// <summary>The host used last, e.g. ws://192.168.1.20:47710/ws. Always one of <see cref="Hosts"/>.</summary>
    public string RelayUrl { get; set; } = string.Empty;

    /// <summary>Legacy (before 2.1.7): the key of the last host. Moved to that host on load; each host keeps its own key now.</summary>
    public string RelayAppKey { get; set; } = string.Empty;

    public List<SavedHost> Hosts { get; set; } = [];

    public List<SavedRoom> FavoriteRooms { get; set; } = [];

    public List<OwnedRoom> OwnedRooms { get; set; } = [];

    public string LastLoungeCode { get; set; } = string.Empty;

    public string LastLoungeName { get; set; } = string.Empty;

    public string StreamTitle { get; set; } = string.Empty;

    /// <summary>Auto, System, App or Off (see AudioMode).</summary>
    public string AudioMode { get; set; } = "Auto";

    public int Volume { get; set; } = 100;

    public bool CheckUpdatesOnLaunch { get; set; } = true;

    /// <summary>Set once the person has read and accepted the study-only notice.</summary>
    public bool DisclaimerAccepted { get; set; }
}
