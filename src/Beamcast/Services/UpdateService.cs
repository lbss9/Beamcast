using Velopack;
using Velopack.Sources;

namespace Beamcast;

public enum UpdateCheckKind
{
    NotInstalled,
    UpToDate,
    Available,
    Failed,
}

/// <summary>Result of looking for a newer Velopack release.</summary>
public sealed record UpdateCheck(UpdateCheckKind Kind, UpdateOffer? Offer = null);

/// <summary>A newer build the user can choose to install.</summary>
public sealed record UpdateOffer(string Version, string Notes);

/// <summary>Checks GitHub Releases and applies updates through Velopack.</summary>
public static class UpdateService
{
    private static UpdateInfo? _pending;

    public static bool IsInstalled
    {
        get
        {
            try
            {
                return CreateManager().IsInstalled;
            }
            catch
            {
                return false;
            }
        }
    }

    public static async Task<UpdateCheck> CheckAsync()
    {
        try
        {
            var manager = CreateManager();
            if (!manager.IsInstalled)
                return new UpdateCheck(UpdateCheckKind.NotInstalled);

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                _pending = null;
                return new UpdateCheck(UpdateCheckKind.UpToDate);
            }

            _pending = update;
            var version = update.TargetFullRelease.Version.ToString();
            var notes = string.IsNullOrWhiteSpace(update.TargetFullRelease.NotesMarkdown)
                ? ChangelogStore.Read()
                : update.TargetFullRelease.NotesMarkdown;
            return new UpdateCheck(UpdateCheckKind.Available, new UpdateOffer(version, notes));
        }
        catch
        {
            return new UpdateCheck(UpdateCheckKind.Failed);
        }
    }

    public static async Task<UpdateCheckKind> DownloadAndApplyAsync()
    {
        try
        {
            if (_pending is null)
                return UpdateCheckKind.UpToDate;

            var manager = CreateManager();
            if (!manager.IsInstalled)
                return UpdateCheckKind.NotInstalled;

            await manager.DownloadUpdatesAsync(_pending);
            manager.ApplyUpdatesAndRestart(_pending);
            return UpdateCheckKind.Available;
        }
        catch
        {
            return UpdateCheckKind.Failed;
        }
    }

    private static UpdateManager CreateManager()
    {
        var url = AppInfo.GitHubRepoUrl;
        if (!AppInfo.IsTrustedGitHubRepo(url))
            throw new InvalidOperationException("Update feed is not configured.");

        return new UpdateManager(new GithubSource(url, accessToken: null, prerelease: false));
    }
}
