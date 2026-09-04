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

    public string QualityPreset { get; set; } = Beamcast.QualityPreset.P1080;

    public int Fps { get; set; } = 30;

    public int BitrateKbps { get; set; } = 5000;

    public bool ShowCursor { get; set; } = true;

    public int MaxViewers { get; set; } = 16;

    public string LastInvite { get; set; } = string.Empty;

    public bool CheckUpdatesOnLaunch { get; set; } = true;

    /// <summary>Set once the person has read and accepted the study-only notice.</summary>
    public bool DisclaimerAccepted { get; set; }
}
