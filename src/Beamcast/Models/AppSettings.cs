namespace Beamcast;

/// <summary>A host (server) the person uses; the app key is the one that host demands, if any.</summary>
public sealed class SavedHost
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string AppKey { get; set; } = string.Empty;
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

    /// <summary>The host used last, e.g. ws://192.168.1.20:47710/ws.</summary>
    public string RelayUrl { get; set; } = string.Empty;

    /// <summary>Optional key the last host may demand (BEAMCAST_APP_KEY on the server).</summary>
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
