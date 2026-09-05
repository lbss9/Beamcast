namespace Beamcast;

/// <summary>User preferences plus the last values used on the broadcast and watch screens.</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = AppLanguage.System;

    public string Theme { get; set; } = "System";

    public string DisplayName { get; set; } = string.Empty;

    public int Port { get; set; } = AppInfo.DefaultPort;

    public string SessionName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string QualityPreset { get; set; } = Beamcast.QualityPreset.Source;

    public int Fps { get; set; } = 60;

    public int BitrateKbps { get; set; } = 30000;

    /// <summary>Auto, H264, HEVC or VP8. Auto picks a GPU encoder when one exists.</summary>
    public string Encoder { get; set; } = "Auto";

    public bool ShowCursor { get; set; } = true;

    public int MaxViewers { get; set; } = 16;

    public string LastInvite { get; set; } = string.Empty;

    /// <summary>"Relay" (through the server, works anywhere) or "Direct" (TCP to this machine, LAN/port forwarding).</summary>
    public string ConnectionMode { get; set; } = "Relay";

    public string RelayUrl { get; set; } = AppInfo.DefaultRelayUrl;

    public string RelayAppKey { get; set; } = AppInfo.DefaultRelayAppKey;

    public bool CheckUpdatesOnLaunch { get; set; } = true;

    /// <summary>Set once the person has read and accepted the study-only notice.</summary>
    public bool DisclaimerAccepted { get; set; }
}
