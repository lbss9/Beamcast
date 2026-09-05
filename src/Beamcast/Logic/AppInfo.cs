namespace Beamcast;

/// <summary>Identity used by the UI, the pack script and the update client.</summary>
public static class AppInfo
{
    public const string Name = "Beamcast";

    /// <summary>Wire protocol version. Bump when the handshake or packet layout changes.</summary>
    public const int ProtocolVersion = 1;

    public const int DefaultPort = 47700;

    public const string DefaultRepoUrl = "https://github.com/lbss9/beamcast";

    /// <summary>The relay every install talks to unless Settings says otherwise.</summary>
    public const string DefaultRelayUrl = "wss://relay.example.com/ws";

    /// <summary>
    /// Shared key the relay demands. It only keeps strangers from using the relay's bandwidth;
    /// stream contents are protected by the per-room secret, never by this key.
    /// </summary>
    public const string DefaultRelayAppKey = "";

    public static string Version
    {
        get
        {
            var version = typeof(AppInfo).Assembly.GetName().Version;
            return version is null ? "0.1.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static string GitHubRepoUrl
    {
        get
        {
#if DEBUG
            var overrideUrl = Environment.GetEnvironmentVariable("BEAMCAST_REPO_URL");
            if (IsTrustedGitHubRepo(overrideUrl))
                return overrideUrl!;
#endif
            return IsTrustedGitHubRepo(DefaultRepoUrl) ? DefaultRepoUrl : string.Empty;
        }
    }

    public static bool IsTrustedGitHubRepo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts.All(IsSafeRepoSegment);
    }

    private static bool IsSafeRepoSegment(string value) =>
        value.Length is > 0 and <= 100
        && value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.');
}
