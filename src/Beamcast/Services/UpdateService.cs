using Velopack;
using Velopack.Sources;

namespace Beamcast;

public enum UpdateCheckKind
{
    NotInstalled,
    UpToDate,
    Available,
    /// <summary>An update was already downloaded; the app only needs to restart to finish it.</summary>
    ReadyToRestart,
    Failed,
}

/// <summary>Result of looking for a newer Velopack release.</summary>
public sealed record UpdateCheck(UpdateCheckKind Kind, UpdateOffer? Offer = null, string? Error = null);

/// <summary>A newer build the user can choose to install.</summary>
public sealed record UpdateOffer(
    string Version,
    string Notes,
    bool Downloaded = false,
    string CurrentVersion = "",
    long SizeBytes = 0,
    bool IsDelta = false
);

/// <summary>
/// Checks GitHub Releases and applies updates through Velopack.
///
/// The feed is the <c>releases.win.json</c> asset of the latest (non-prerelease) GitHub release,
/// next to the full/delta <c>.nupkg</c> packages; <c>scripts/pack.ps1</c> publishes all of them.
/// Every failure is logged to diag.log so a silent "no update" can be explained.
/// </summary>
public static class UpdateService
{
    private static readonly object Sync = new();
    private static UpdateManager? _manager;
    private static UpdateInfo? _pending;
    private static int _busy;

    public static bool IsInstalled
    {
        get
        {
            try
            {
                return Manager.IsInstalled;
            }
            catch (Exception ex)
            {
                Diag.Log("update: IsInstalled failed: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>The version Velopack thinks is installed (null when running from a build folder).</summary>
    public static string? InstalledVersion
    {
        get
        {
            try
            {
                return Manager.IsInstalled ? Manager.CurrentVersion?.ToString() : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public static async Task<UpdateCheck> CheckAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0)
            return new UpdateCheck(UpdateCheckKind.Failed, Error: "busy");
        try
        {
            var manager = Manager;
            if (!manager.IsInstalled)
                return new UpdateCheck(UpdateCheckKind.NotInstalled);

            // A package downloaded on a previous run that was never applied: no need to hit the network.
            if (manager.UpdatePendingRestart is { } staged)
            {
                Diag.Log($"update: {staged.Version} already downloaded, waiting for a restart");
                return new UpdateCheck(UpdateCheckKind.ReadyToRestart, new UpdateOffer(staged.Version.ToString(), NotesFor(staged.NotesMarkdown), Downloaded: true, CurrentVersion: manager.CurrentVersion?.ToString() ?? string.Empty));
            }

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                _pending = null;
                Diag.Log($"update: up to date ({manager.CurrentVersion})");
                return new UpdateCheck(UpdateCheckKind.UpToDate);
            }

            _pending = update;
            var target = update.TargetFullRelease;
            Diag.Log($"update: {manager.CurrentVersion} -> {target.Version} ({target.FileName}, {target.Size / 1024 / 1024} MB, {update.DeltasToTarget.Length} delta(s))");
            var deltaBytes = update.DeltasToTarget.Sum(d => d.Size);
            var isDelta = update.DeltasToTarget.Length > 0 && deltaBytes > 0 && deltaBytes < target.Size;
            return new UpdateCheck(UpdateCheckKind.Available, new UpdateOffer(
                target.Version.ToString(),
                NotesFor(target.NotesMarkdown),
                CurrentVersion: manager.CurrentVersion?.ToString() ?? string.Empty,
                SizeBytes: isDelta ? deltaBytes : target.Size,
                IsDelta: isDelta));
        }
        catch (Exception ex)
        {
            Diag.Log("update: check failed: " + ex);
            return new UpdateCheck(UpdateCheckKind.Failed, Error: ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Downloads (if needed) and restarts into the new version. Only returns on failure.</summary>
    public static async Task<UpdateCheckKind> DownloadAndApplyAsync(Action<int>? progress = null)
    {
        try
        {
            var manager = Manager;
            if (!manager.IsInstalled)
                return UpdateCheckKind.NotInstalled;

            if (_pending is null && manager.UpdatePendingRestart is { } staged)
            {
                Diag.Log($"update: restarting into staged {staged.Version}");
                manager.ApplyUpdatesAndRestart(staged);
                return UpdateCheckKind.ReadyToRestart;
            }
            if (_pending is null)
                return UpdateCheckKind.UpToDate;

            Diag.Log($"update: downloading {_pending.TargetFullRelease.Version}");
            await manager.DownloadUpdatesAsync(_pending, progress).ConfigureAwait(false);
            Diag.Log("update: download complete, applying and restarting");
            manager.ApplyUpdatesAndRestart(_pending);
            return UpdateCheckKind.Available;
        }
        catch (Exception ex)
        {
            Diag.Log("update: apply failed: " + ex);
            return UpdateCheckKind.Failed;
        }
    }

    /// <summary>
    /// Release notes are published in both languages, separated by <c>&lt;!-- lang:xx --&gt;</c>
    /// markers (see scripts/pack.ps1). Picks the block for the app language, falling back to the
    /// whole text, and to the bundled changelog when the release carries no notes.
    /// </summary>
    private static string NotesFor(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return ChangelogStore.Read();
        var language = AppLanguage.Resolve(SettingsStore.Load().Language);
        var blocks = System.Text.RegularExpressions.Regex.Split(markdown, @"<!--\s*lang:([A-Za-z-]+)\s*-->");
        if (blocks.Length < 3)
            return markdown.Trim();
        // Regex.Split yields: [before, lang1, text1, lang2, text2, ...]
        string? chosen = null;
        string? first = null;
        for (var i = 1; i + 1 < blocks.Length; i += 2)
        {
            var text = blocks[i + 1].Trim().Trim('-').Trim();
            first ??= text;
            if (string.Equals(blocks[i], language, StringComparison.OrdinalIgnoreCase))
                chosen = text;
        }
        var result = chosen ?? first ?? markdown;
        return result.Trim().Length == 0 ? ChangelogStore.Read() : result;
    }

    private static UpdateManager Manager
    {
        get
        {
            lock (Sync)
            {
                if (_manager is not null)
                    return _manager;
                var url = AppInfo.GitHubRepoUrl;
                if (!AppInfo.IsTrustedGitHubRepo(url))
                    throw new InvalidOperationException("Update feed is not configured.");
                _manager = new UpdateManager(new GithubSource(url, accessToken: null, prerelease: false), new UpdateOptions
                {
                    AllowVersionDowngrade = false,
                });
                return _manager;
            }
        }
    }
}
