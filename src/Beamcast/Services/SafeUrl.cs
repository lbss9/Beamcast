using Windows.System;

namespace Beamcast;

/// <summary>Opens http/https links only.</summary>
public static class SafeUrl
{
    public static async Task<bool> OpenAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;
        return await Launcher.LaunchUriAsync(uri);
    }
}
