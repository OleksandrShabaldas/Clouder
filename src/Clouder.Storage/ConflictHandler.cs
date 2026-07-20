using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Storage;

namespace Clouder.Storage;

/// <summary>What the caller should do about a file that changed on both sides.</summary>
public enum ConflictOutcome
{
    /// <summary>Local copy wins: upload it, don't download.</summary>
    UseLocal,
    /// <summary>Remote copy wins: download it, don't upload.</summary>
    UseRemote,
    /// <summary>The local file was renamed aside; now take the remote copy under the original name.</summary>
    KeptBothTakeRemote,
    /// <summary>Recorded for the user to decide — leave both sides untouched for now.</summary>
    Deferred
}

/// <summary>
/// Applies the configured <see cref="ConflictResolution"/> policy when a file changed
/// locally AND remotely since the last sync. Shared by the upload path (local → cloud)
/// and the download path (cloud → local) so both behave consistently.
/// </summary>
public sealed class ConflictHandler
{
    private readonly IMetadataStore _store;

    public ConflictResolution Policy { get; set; } = ConflictResolution.NewestWins;

    /// <summary>Raised when a conflict needs the user's attention. Arg = (poolId, relativePath).</summary>
    public event Action<string, string>? ConflictDetected;

    public ConflictHandler(IMetadataStore store) => _store = store;

    public async Task<ConflictOutcome> HandleAsync(
        StoragePool pool,
        string relativePath,
        string localFilePath,
        DateTime localModifiedUtc,
        long localSize,
        CloudItem remote,
        string accountId,
        CancellationToken ct = default)
    {
        var itemId = $"{pool.PoolId}|{relativePath}";

        switch (Policy)
        {
            case ConflictResolution.KeepBoth:
            {
                var renamed = RenameLocalAside(localFilePath);
                if (renamed == null)
                    return ConflictOutcome.UseLocal; // couldn't rename — don't clobber the local file

                ClouderLog.Warn($"Conflict on '{relativePath}': kept local copy as '{Path.GetFileName(renamed)}', taking cloud version");
                await NotifyAsync(pool, relativePath, accountId, NotificationSeverity.Info,
                    $"Both copies of \"{Path.GetFileName(relativePath)}\" changed",
                    $"Your local version was saved as \"{Path.GetFileName(renamed)}\" and the cloud version "
                    + "was downloaded under the original name. Both are now in the pool.");
                ConflictDetected?.Invoke(pool.PoolId, relativePath);
                return ConflictOutcome.KeptBothTakeRemote;
            }

            case ConflictResolution.AlwaysAsk:
            {
                await _store.UpsertConflictAsync(new FileConflict
                {
                    ConflictId = itemId,
                    PoolId = pool.PoolId,
                    ItemId = itemId,
                    RelativePath = relativePath,
                    AccountId = accountId,
                    RemoteId = remote.RemoteId,
                    LocalModifiedUtc = localModifiedUtc,
                    RemoteModifiedUtc = remote.ModifiedAtUtc,
                    LocalSize = localSize,
                    RemoteSize = remote.Size,
                    DetectedAtUtc = DateTime.UtcNow
                }, ct);

                var tracked = await _store.GetItemAsync(itemId, ct);
                if (tracked != null)
                {
                    tracked.SyncState = Clouder.Core.Models.SyncState.Conflict;
                    await _store.UpsertItemAsync(tracked, ct);
                }

                ClouderLog.Warn($"Conflict on '{relativePath}' — awaiting user decision");
                await NotifyAsync(pool, relativePath, accountId, NotificationSeverity.Warning,
                    $"Sync conflict: {Path.GetFileName(relativePath)}",
                    $"This file changed both on this PC ({localModifiedUtc.ToLocalTime():g}, {FormatBytes(localSize)}) "
                    + $"and in the cloud ({remote.ModifiedAtUtc.ToLocalTime():g}, {FormatBytes(remote.Size)}). "
                    + "Open Files → Resolve conflicts to choose which copy to keep. "
                    + "Neither copy will be changed until you decide.");
                ConflictDetected?.Invoke(pool.PoolId, relativePath);
                return ConflictOutcome.Deferred;
            }

            case ConflictResolution.NewestWins:
            default:
            {
                bool localWins = localModifiedUtc >= remote.ModifiedAtUtc;
                ClouderLog.Warn($"Conflict on '{relativePath}': {(localWins ? "local" : "cloud")} copy is newer — it wins");
                await NotifyAsync(pool, relativePath, accountId, NotificationSeverity.Info,
                    $"Sync conflict resolved: {Path.GetFileName(relativePath)}",
                    $"This file changed in both places. The {(localWins ? "local" : "cloud")} version was newer and "
                    + $"replaced the other. Switch the conflict policy to \"KeepBoth\" in Settings if you'd rather "
                    + "keep both copies.");
                ConflictDetected?.Invoke(pool.PoolId, relativePath);
                return localWins ? ConflictOutcome.UseLocal : ConflictOutcome.UseRemote;
            }
        }
    }

    /// <summary>Renames a local file to "name (conflicted copy 2026-07-17 1530).ext". Returns the new path.</summary>
    public static string? RenameLocalAside(string localFilePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(localFilePath)!;
            var stem = Path.GetFileNameWithoutExtension(localFilePath);
            var ext = Path.GetExtension(localFilePath);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmm");

            var candidate = Path.Combine(dir, $"{stem} (conflicted copy {stamp}){ext}");
            int n = 2;
            while (File.Exists(candidate))
                candidate = Path.Combine(dir, $"{stem} (conflicted copy {stamp}) ({n++}){ext}");

            File.Move(localFilePath, candidate);
            return candidate;
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Could not set aside conflicted copy of '{localFilePath}'", ex);
            return null;
        }
    }

    private async Task NotifyAsync(
        StoragePool pool, string relativePath, string accountId,
        NotificationSeverity severity, string title, string body)
    {
        await _store.UpsertNotificationAsync(new AppNotification
        {
            // Stable per file+detection minute, so a repeatedly-detected conflict
            // doesn't spam the list but a genuinely new one still appears.
            NotificationId = $"conflict-{pool.PoolId}-{relativePath}-{DateTime.UtcNow:yyyyMMddHHmm}",
            Title = title,
            Body = body,
            Source = "sync",
            Severity = severity,
            TimestampUtc = DateTime.UtcNow,
            IsRead = false,
            RelatedAccountId = accountId
        });
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return $"{size:F1} {units[i]}";
    }
}
