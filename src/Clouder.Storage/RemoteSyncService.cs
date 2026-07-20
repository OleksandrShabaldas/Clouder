using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Providers;
using Clouder.Core.Storage;

namespace Clouder.Storage;

/// <summary>
/// Cloud → local sync. Polls each pool member for remote changes beneath the pool's
/// remote root folder, brings new/changed files down into the local pool folder, and
/// propagates remote deletions. Conflicts (changed on both sides) go through
/// <see cref="ConflictHandler"/>.
///
/// Pairs with <see cref="PoolSyncService"/>, which handles local → cloud. Downloads are
/// written with the remote's modification time and recorded with that same timestamp,
/// so the upload path sees "not newer than tracked" and doesn't bounce the file back.
/// </summary>
public sealed class RemoteSyncService
{
    private readonly IMetadataStore _store;
    private readonly IProviderRegistry _providers;
    private readonly RemoteRootResolver _roots;
    private readonly ConflictHandler _conflicts;
    private readonly PoolSyncService? _localSync;

    /// <summary>Chunk files created by striping — never treated as user files.</summary>
    private const string StripeChunkMarker = ".clpart";

    private const int MaxPathDepth = 64;

    public bool Paused { get; set; }
    public long MaxDownloadBytesPerSec { get; set; }
    public long MinFreeDiskBytes { get; set; }

    public ConflictResolution ConflictPolicy
    {
        get => _conflicts.Policy;
        set => _conflicts.Policy = value;
    }

    /// <summary>Raised as remote sync progresses. Arg = (poolId, message).</summary>
    public event Action<string, string>? StatusChanged;

    /// <summary>Raised after a file is brought down from the cloud. Arg = (poolId, relativePath).</summary>
    public event Action<string, string>? FileDownloaded;

    public RemoteSyncService(
        IMetadataStore store,
        IProviderRegistry providers,
        ConflictHandler conflicts,
        RemoteRootResolver roots,
        PoolSyncService? localSync = null)
    {
        _store = store;
        _providers = providers;
        _conflicts = conflicts;
        _roots = roots;
        _localSync = localSync;
    }

    // ── Entry points ────────────────────────────────────────────────────

    public async Task<RemoteSyncResult> SyncAllPoolsAsync(CancellationToken ct = default)
    {
        var total = new RemoteSyncResult();
        var pools = await _store.GetAllPoolsAsync(ct);
        foreach (var pool in pools)
        {
            try
            {
                total.Add(await SyncPoolAsync(pool.PoolId, ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ClouderLog.Error($"Remote sync failed for pool '{pool.Name}'", ex);
            }
        }
        return total;
    }

    public async Task<RemoteSyncResult> SyncPoolAsync(string poolId, CancellationToken ct = default)
    {
        var result = new RemoteSyncResult();
        if (Paused) return result;

        var pool = await _store.GetPoolAsync(poolId, ct);
        if (pool == null) return result;

        foreach (var member in pool.Members.Where(m => m.IsEnabled))
        {
            ct.ThrowIfCancellationRequested();

            var provider = _providers.GetProvider(member.ProviderId);
            if (provider == null) continue;

            try
            {
                result.Add(await SyncMemberAsync(pool, member, provider, ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ClouderLog.Error($"Remote sync failed for account {member.AccountId} in pool '{pool.Name}'", ex);
                result.Failed++;
            }
        }

        if (result.Downloaded > 0 || result.DeletedLocally > 0 || result.Conflicts > 0)
        {
            ClouderLog.Info(
                $"Remote sync for '{pool.Name}': {result.Downloaded} downloaded, "
                + $"{result.DeletedLocally} deleted locally, {result.Conflicts} conflict(s), {result.Failed} failed");
            StatusChanged?.Invoke(poolId,
                $"Cloud changes applied: {result.Downloaded} downloaded, {result.DeletedLocally} removed");
        }

        return result;
    }

    // ── Per-member change processing ────────────────────────────────────

    private async Task<RemoteSyncResult> SyncMemberAsync(
        StoragePool pool, PoolMember member, ICloudProvider provider, CancellationToken ct)
    {
        var result = new RemoteSyncResult();

        var rootId = await _roots.EnsureAsync(provider, pool, member, ct);
        var cursorKey = CursorKey(pool.PoolId, member.AccountId);
        var cursor = await _store.GetSettingAsync(cursorKey, ct);

        var changeSet = await provider.GetChangesAsync(member.AccountId, rootId, cursor, ct);

        // First poll for this member: the provider only handed back a cursor. Nothing
        // to apply — anything already up there was put there by us.
        if (string.IsNullOrEmpty(cursor) && changeSet.Changes.Count == 0)
        {
            if (!string.IsNullOrEmpty(changeSet.Cursor))
                await _store.SetSettingAsync(cursorKey, changeSet.Cursor, ct);
            return result;
        }

        // Cache of remote folder metadata for path resolution within this run.
        var folderCache = new Dictionary<string, CloudItem?>(StringComparer.Ordinal);
        var seenRemoteIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var change in changeSet.Changes)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (change.Type == RemoteChangeType.Deleted)
                {
                    if (await ApplyRemoteDeleteAsync(pool, member, change.RemoteId, ct))
                        result.DeletedLocally++;
                    continue;
                }

                var item = change.Item;
                if (item == null) continue;
                seenRemoteIds.Add(item.RemoteId);

                // Stripe chunks are storage plumbing, not user files.
                if (item.Name.Contains(StripeChunkMarker, StringComparison.OrdinalIgnoreCase))
                    continue;

                var relativePath = await ResolveRelativePathAsync(provider, member.AccountId, item, rootId, folderCache, ct);
                if (relativePath == null)
                    continue; // outside this pool's remote root

                if (item.Type == CloudItemType.Folder)
                {
                    Directory.CreateDirectory(Path.Combine(pool.LocalPath, relativePath));
                    continue;
                }

                var outcome = await ApplyRemoteUpsertAsync(pool, member, provider, item, relativePath, ct);
                switch (outcome)
                {
                    case ApplyOutcome.Downloaded: result.Downloaded++; break;
                    case ApplyOutcome.Conflict: result.Conflicts++; break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ClouderLog.Error($"Failed to apply remote change {change.RemoteId}", ex);
                result.Failed++;
            }
        }

        // Providers with no change feed hand back a full listing; anything we track
        // that wasn't listed no longer exists remotely.
        if (changeSet.IsFullListing)
            result.DeletedLocally += await InferDeletionsAsync(pool, member, seenRemoteIds, ct);

        if (!string.IsNullOrEmpty(changeSet.Cursor))
            await _store.SetSettingAsync(cursorKey, changeSet.Cursor, ct);

        return result;
    }

    private enum ApplyOutcome { Skipped, Downloaded, Conflict }

    private async Task<ApplyOutcome> ApplyRemoteUpsertAsync(
        StoragePool pool, PoolMember member, ICloudProvider provider,
        CloudItem remote, string relativePath, CancellationToken ct)
    {
        var itemId = $"{pool.PoolId}|{relativePath}";
        var localPath = Path.Combine(pool.LocalPath, relativePath);
        var tracked = await _store.GetItemAsync(itemId, ct);

        // Already have this exact revision (typically the echo of our own upload).
        if (tracked != null
            && tracked.RemoteId == remote.RemoteId
            && remote.ModifiedAtUtc <= tracked.ModifiedAtUtc)
            return ApplyOutcome.Skipped;

        bool localExists = File.Exists(localPath);
        var localModified = localExists ? File.GetLastWriteTimeUtc(localPath) : DateTime.MinValue;
        long localSize = localExists ? new FileInfo(localPath).Length : 0;

        // Local changed since we last synced it — or exists locally but was never
        // tracked, while a file of the same name appeared remotely.
        bool localChanged = localExists && (tracked == null || localModified > tracked.ModifiedAtUtc);

        if (!localChanged)
        {
            await DownloadRemoteFileAsync(pool, member, provider, remote, relativePath, ct);
            return ApplyOutcome.Downloaded;
        }

        var decision = await _conflicts.HandleAsync(
            pool, relativePath, localPath, localModified, localSize, remote, member.AccountId, ct);

        switch (decision)
        {
            case ConflictOutcome.UseRemote:
            case ConflictOutcome.KeptBothTakeRemote:
                // KeepBoth already renamed the local file aside; the original path is free.
                await DownloadRemoteFileAsync(pool, member, provider, remote, relativePath, ct);
                return ApplyOutcome.Downloaded;

            case ConflictOutcome.UseLocal:
                // Leave the local file alone and let the uploader push it.
                if (tracked != null)
                {
                    tracked.SyncState = Clouder.Core.Models.SyncState.PendingUpload;
                    await _store.UpsertItemAsync(tracked, ct);
                }
                return ApplyOutcome.Conflict;

            default:
                return ApplyOutcome.Conflict; // Deferred — recorded for the user
        }
    }

    /// <summary>Returns true if a local file was removed.</summary>
    private async Task<bool> ApplyRemoteDeleteAsync(
        StoragePool pool, PoolMember member, string remoteId, CancellationToken ct)
    {
        var tracked = await _store.GetItemByRemoteIdAsync(member.AccountId, remoteId, ct);
        if (tracked == null) return false;

        // Only act on items belonging to this pool.
        var prefix = pool.PoolId + "|";
        if (!tracked.Id.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var relativePath = tracked.Id[prefix.Length..];
        var localPath = Path.Combine(pool.LocalPath, relativePath);

        if (File.Exists(localPath))
        {
            var localModified = File.GetLastWriteTimeUtc(localPath);
            if (localModified > tracked.ModifiedAtUtc)
            {
                // Deleted in the cloud but edited here — keep the local copy and let
                // it upload again as a new file rather than silently destroying work.
                tracked.SyncState = Clouder.Core.Models.SyncState.PendingUpload;
                await _store.UpsertItemAsync(tracked, ct);
                await _store.DeleteItemAsync(tracked.Id, ct);

                ClouderLog.Warn($"'{relativePath}' was deleted in the cloud but changed locally — keeping the local copy");
                await _store.UpsertNotificationAsync(new AppNotification
                {
                    NotificationId = $"remote-delete-kept-{tracked.Id}-{DateTime.UtcNow:yyyyMMddHHmm}",
                    Title = $"Kept your edited copy of {Path.GetFileName(relativePath)}",
                    Body = "This file was deleted in the cloud, but you had changed it on this PC since the "
                         + "last sync. The local file was kept and will be uploaded again.",
                    Source = "sync",
                    Severity = NotificationSeverity.Warning,
                    TimestampUtc = DateTime.UtcNow,
                    IsRead = false,
                    RelatedAccountId = member.AccountId
                }, ct);
                return false;
            }

            try
            {
                _localSync?.SuppressLocalWrites(localPath, TimeSpan.FromSeconds(20));
                File.Delete(localPath);
                ClouderLog.Info($"Removed '{relativePath}' locally (deleted in the cloud)");
            }
            catch (Exception ex)
            {
                ClouderLog.Error($"Could not delete local file '{localPath}'", ex);
                return false;
            }
        }

        await _store.DeleteItemAsync(tracked.Id, ct);
        return true;
    }

    private async Task<int> InferDeletionsAsync(
        StoragePool pool, PoolMember member, HashSet<string> seenRemoteIds, CancellationToken ct)
    {
        int removed = 0;
        var tracked = await _store.GetItemsByAccountAsync(member.AccountId, ct);
        var prefix = pool.PoolId + "|";

        foreach (var item in tracked)
        {
            if (!item.Id.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (item.Type != CloudItemType.File) continue;
            // Striped files live as chunks under different remote ids — a full listing
            // of user files can't confirm or deny their existence.
            if (item.ProviderId == "clouder-striped") continue;
            if (seenRemoteIds.Contains(item.RemoteId)) continue;

            if (await ApplyRemoteDeleteAsync(pool, member, item.RemoteId, ct))
                removed++;
        }

        return removed;
    }

    // ── Download ────────────────────────────────────────────────────────

    private async Task DownloadRemoteFileAsync(
        StoragePool pool, PoolMember member, ICloudProvider provider,
        CloudItem remote, string relativePath, CancellationToken ct)
    {
        var localPath = Path.Combine(pool.LocalPath, relativePath);
        var dir = Path.GetDirectoryName(localPath)!;
        Directory.CreateDirectory(dir);

        EnsureDiskSpace(localPath, remote.Size);

        StatusChanged?.Invoke(pool.PoolId, $"Downloading {remote.Name}...");

        // Write to a hidden temp file first: a partially-written file at the real path
        // would be picked up by the watcher and uploaded back as a truncated version.
        var tempPath = Path.Combine(dir, $".clouder-{Guid.NewGuid():N}.part");
        _localSync?.SuppressLocalWrites(localPath, TimeSpan.FromMinutes(10));

        try
        {
            await using (var source = await provider.DownloadAsync(member.AccountId, remote.RemoteId, ct))
            {
                Stream src = MaxDownloadBytesPerSec > 0
                    ? new ThrottledReadStream(source, MaxDownloadBytesPerSec)
                    : source;
                await using var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dest, ct);
            }

            File.Move(tempPath, localPath, overwrite: true);

            // Stamp the local file with the remote's modification time and record that
            // same value: the upload path's "newer than tracked?" test then says no,
            // which is what stops a download from bouncing straight back up.
            File.SetLastWriteTimeUtc(localPath, remote.ModifiedAtUtc);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            // Re-arm briefly: the move/attribute changes generate their own events.
            _localSync?.SuppressLocalWrites(localPath, TimeSpan.FromSeconds(20));
        }

        await _store.UpsertItemAsync(new CloudItem
        {
            Id = $"{pool.PoolId}|{relativePath}",
            RemoteId = remote.RemoteId,
            ProviderId = member.ProviderId,
            AccountId = member.AccountId,
            Name = remote.Name,
            ParentId = remote.ParentId,
            Type = CloudItemType.File,
            Size = remote.Size,
            ContentHash = remote.ContentHash,
            CreatedAtUtc = remote.CreatedAtUtc,
            ModifiedAtUtc = remote.ModifiedAtUtc,
            SyncState = Clouder.Core.Models.SyncState.Synced
        }, ct);

        // A resolved file is no longer in conflict.
        await _store.DeleteConflictAsync($"{pool.PoolId}|{relativePath}", ct);

        ClouderLog.Info($"Downloaded '{relativePath}' from {member.AccountId}");
        FileDownloaded?.Invoke(pool.PoolId, relativePath);
    }

    private void EnsureDiskSpace(string destinationPath, long needed)
    {
        if (MinFreeDiskBytes <= 0) return;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destinationPath));
            if (string.IsNullOrEmpty(root)) return;
            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace - needed < MinFreeDiskBytes)
                throw new IOException(
                    $"Not enough local disk space to download '{Path.GetFileName(destinationPath)}'.");
        }
        catch (IOException) { throw; }
        catch { /* DriveInfo can fail on network paths — skip the guard then */ }
    }

    // ── Conflict resolution (user-driven) ───────────────────────────────

    /// <summary>
    /// Applies the user's decision for a conflict recorded under the AlwaysAsk policy.
    /// </summary>
    public async Task<bool> ResolveConflictAsync(
        string conflictId, ConflictResolutionChoice choice, CancellationToken ct = default)
    {
        var conflict = await _store.GetConflictAsync(conflictId, ct);
        if (conflict == null) return false;

        var pool = await _store.GetPoolAsync(conflict.PoolId, ct);
        if (pool == null) return false;

        var member = pool.Members.FirstOrDefault(m => m.AccountId == conflict.AccountId);
        var provider = member != null ? _providers.GetProvider(member.ProviderId) : null;
        if (member == null || provider == null)
        {
            ClouderLog.Warn($"Cannot resolve conflict '{conflictId}': account not connected");
            return false;
        }

        var localPath = Path.Combine(pool.LocalPath, conflict.RelativePath);

        try
        {
            switch (choice)
            {
                case ConflictResolutionChoice.KeepLocal:
                {
                    // Mark for upload; the local→cloud sweep pushes it.
                    var tracked = await _store.GetItemAsync(conflict.ItemId, ct);
                    if (tracked != null)
                    {
                        tracked.SyncState = Clouder.Core.Models.SyncState.PendingUpload;
                        // Predate the tracked timestamp so the uploader sees the local file as newer.
                        tracked.ModifiedAtUtc = DateTime.MinValue;
                        await _store.UpsertItemAsync(tracked, ct);
                    }
                    break;
                }

                case ConflictResolutionChoice.KeepBoth:
                {
                    if (ConflictHandler.RenameLocalAside(localPath) == null)
                        return false;
                    goto case ConflictResolutionChoice.KeepRemote;
                }

                case ConflictResolutionChoice.KeepRemote:
                {
                    var remote = await provider.GetItemAsync(conflict.AccountId, conflict.RemoteId, ct);
                    if (remote == null)
                    {
                        ClouderLog.Warn($"Cannot take the cloud copy of '{conflict.RelativePath}': it no longer exists");
                        return false;
                    }
                    await DownloadRemoteFileAsync(pool, member, provider, remote, conflict.RelativePath, ct);
                    break;
                }
            }

            await _store.DeleteConflictAsync(conflictId, ct);

            var item = await _store.GetItemAsync(conflict.ItemId, ct);
            if (item is { SyncState: Clouder.Core.Models.SyncState.Conflict })
            {
                item.SyncState = Clouder.Core.Models.SyncState.PendingUpload;
                await _store.UpsertItemAsync(item, ct);
            }

            ClouderLog.Info($"Conflict on '{conflict.RelativePath}' resolved: {choice}");
            return true;
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Failed to resolve conflict '{conflictId}'", ex);
            return false;
        }
    }

    // ── Path resolution ─────────────────────────────────────────────────

    /// <summary>
    /// Builds the item's path relative to the pool's remote root by walking up its
    /// parents. Returns null when the item doesn't live under that root — which is how
    /// unrelated files elsewhere in the user's cloud storage get ignored.
    /// </summary>
    private static async Task<string?> ResolveRelativePathAsync(
        ICloudProvider provider, string accountId, CloudItem item, string rootId,
        Dictionary<string, CloudItem?> folderCache, CancellationToken ct)
    {
        var segments = new List<string> { item.Name };
        var parentId = item.ParentId;

        for (int depth = 0; depth < MaxPathDepth; depth++)
        {
            if (string.IsNullOrEmpty(parentId))
                return null; // reached a drive root without passing through our folder

            if (parentId == rootId)
            {
                segments.Reverse();
                return Path.Combine([.. segments]);
            }

            if (!folderCache.TryGetValue(parentId, out var parent))
            {
                try { parent = await provider.GetItemAsync(accountId, parentId, ct); }
                catch { parent = null; }
                folderCache[parentId] = parent;
            }

            if (parent == null)
                return null;

            segments.Add(parent.Name);
            parentId = parent.ParentId;
        }

        return null; // pathologically deep — ignore
    }

    private static string CursorKey(string poolId, string accountId) => $"cursor:{poolId}:{accountId}";
}

public sealed class RemoteSyncResult
{
    public int Downloaded { get; set; }
    public int DeletedLocally { get; set; }
    public int Conflicts { get; set; }
    public int Failed { get; set; }

    public void Add(RemoteSyncResult other)
    {
        Downloaded += other.Downloaded;
        DeletedLocally += other.DeletedLocally;
        Conflicts += other.Conflicts;
        Failed += other.Failed;
    }
}
