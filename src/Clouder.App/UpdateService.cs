using Clouder.Core.Logging;
using Velopack;
using Velopack.Sources;

namespace Clouder_App;

/// <summary>
/// Checks the project's GitHub releases for a newer Clouder and applies it via Velopack.
///
/// Velopack downloads delta packages where it can, so a typical update transfers a few MB
/// rather than the ~140 MB full build — most of that is the bundled .NET runtime and
/// Windows App SDK, which rarely change between releases.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/OleksandrShabaldas/Clouder";

    private readonly UpdateManager _manager;
    private readonly bool _usable;

    public UpdateService()
    {
        UpdateManager? manager = null;
        try
        {
            // No access token: the repository is public. Prereleases are ignored so a
            // draft or test tag can't push itself onto users.
            manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Could not initialise the updater", ex);
        }

        _manager = manager!;
        _usable = manager != null && SafeIsInstalled(manager);
    }

    private static bool SafeIsInstalled(UpdateManager manager)
    {
        try
        {
            return manager.IsInstalled;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// False when Clouder is running from a plain build output — the publish folder, or a
    /// debugger — rather than from a Velopack install. There is no install structure to
    /// update in that case, so checking would only ever produce a confusing failure. The
    /// UI uses this to explain the situation instead of offering a button that can't work.
    /// </summary>
    public bool IsSupported => _usable;

    /// <summary>The running version, or null when that can't be determined.</summary>
    public string? CurrentVersion
    {
        get
        {
            try
            {
                return _manager?.CurrentVersion?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>True once an update is downloaded and only a restart is left.</summary>
    public bool IsRestartPending => PendingAsset != null;

    /// <summary>Returns the available update, or null when already up to date.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (!_usable) return null;

        var info = await _manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);

        // A downgrade means the newest published release is older than what's installed
        // (a release was pulled, or this is a local build ahead of the repo). Applying it
        // would be a surprise rollback, so treat it as "nothing to do".
        if (info == null || info.IsDowngrade) return null;

        return info;
    }

    /// <summary><paramref name="progress"/> receives 0-100 as the download proceeds.</summary>
    public async Task DownloadAsync(UpdateInfo info, Action<int>? progress = null,
        CancellationToken ct = default)
    {
        if (!_usable) return;
        await _manager.DownloadUpdatesAsync(info, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The update downloaded by an earlier run and still waiting for a restart, so it can
    /// be applied without downloading again.
    /// </summary>
    public VelopackAsset? PendingAsset
    {
        get
        {
            try
            {
                return _usable ? _manager.UpdatePendingRestart : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Swaps in the new version and relaunches. Does not return — the process is replaced.
    /// </summary>
    public void ApplyAndRestart(VelopackAsset asset)
    {
        if (!_usable) return;
        ClouderLog.Info($"Applying update {asset.Version} and restarting");
        _manager.ApplyUpdatesAndRestart(asset);
    }
}
