namespace Beamcast;

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

    /// <summary>The Beamcast server (salão) this machine talks to, e.g. ws://192.168.0.4:47710.</summary>
    public string RelayUrl { get; set; } = string.Empty;

    /// <summary>Optional key the server may demand (BEAMCAST_APP_KEY on the server).</summary>
    public string RelayAppKey { get; set; } = string.Empty;

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
