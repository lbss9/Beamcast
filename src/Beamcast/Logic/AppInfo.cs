namespace Beamcast;

/// <summary>Identity used by the UI, the pack script and the update client.</summary>
public static class AppInfo
{
    public const string Name = "Beamcast";

    public const string DefaultRepoUrl = "https://github.com/lbss9/Beamcast";

    /// <summary>Default server port for a bare host in Settings (ws://host → ws://host:47710/ws).</summary>
    public const int DefaultServerPort = Net.LoungeProtocol.DefaultPort;

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
