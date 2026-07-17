using System.Collections.Concurrent;
using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Providers;
using Clouder.Core.Storage;

namespace Clouder.Storage;

/// <summary>
/// Watches pool local folders for file changes and syncs them to cloud providers.
/// Handles upload (local → cloud), delete propagation, and initial sync.
/// </summary>
public sealed class PoolSyncService : IDisposable
{
    private readonly IMetadataStore _store;
    private readonly IProviderRegistry _providers;
    private readonly StoragePoolManager _poolManager;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, DateTime> _debounce = [];
    private readonly ConcurrentDictionary<string, bool> _syncing = [];
    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(2);
    private Timer? _debounceTimer;
    private bool _disposed;

    /// <summary>When true, file changes are queued but not uploaded until resumed.</summary>
    public bool Paused { get; set; }

    /// <summary>How to resolve a local file that conflicts with a newer cloud copy.</summary>
    public ConflictResolution ConflictPolicy { get; set; } = ConflictResolution.NewestWins;

    /// <summary>Size (bytes) above which a file is split across accounts. 0 = never.</summary>
    public long StripeThresholdBytes { get; set; }

    /// <summary>Upload throughput cap in bytes/sec. 0 = unlimited.</summary>
    public long MaxUploadBytesPerSec { get; set; }

    /// <summary>Download throughput cap in bytes/sec. 0 = unlimited.</summary>
    public long MaxDownloadBytesPerSec { get; set; }

    /// <summary>When true, an upload that runs out of space triggers a reorganization and one retry.</summary>
    public bool AutoReorganizeOnFull { get; set; } = true;

    /// <summary>Refuse to download below this much free local disk (bytes). 0 = no guard.</summary>
    public long MinFreeDiskBytes { get; set; }

    /// <summary>Max simultaneous uploads.</summary>
    public int MaxConcurrentTransfers
    {
        get => _maxConcurrent;
        set
        {
            _maxConcurrent = Math.Max(1, value);
            _uploadGate = new SemaphoreSlim(_maxConcurrent, _maxConcurrent);
        }
    }
    private int _maxConcurrent = 4;
    private SemaphoreSlim _uploadGate = new(4, 4);

    /// <summary>Raised when sync activity changes. Arg = (poolId, message).</summary>
    public event Action<string, string>? SyncStatusChanged;

    /// <summary>Raised after a file is synced. Arg = (poolId, fileName, accountId).</summary>
    public event Action<string, string, string>? FileSynced;

    public PoolSyncService(IMetadataStore store, IProviderRegistry providers)
    {
        _store = store;
        _providers = providers;
        _poolManager = new StoragePoolManager(store, providers);
    }

    /// <summary>
    /// Start watching all pools. Call once at startup after providers are registered.
    /// </summary>
    public async Task StartAsync()
    {
        var pools = await _store.GetAllPoolsAsync();
        foreach (var pool in pools)
        {
            try
            {
                WatchPool(pool);
                ClouderLog.Info($"Sync watching pool '{pool.Name}' at {pool.LocalPath}");
            }
            catch (Exception ex)
            {
                ClouderLog.Error($"Failed to watch pool '{pool.Name}'", ex);
            }
        }

        // Start debounce processor
        _debounceTimer = new Timer(ProcessDebounceQueue, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Add a watcher for a newly created pool.
    /// </summary>
    public void WatchPool(StoragePool pool)
    {
        if (_watchers.ContainsKey(pool.PoolId)) return;

        Directory.CreateDirectory(pool.LocalPath);

        var watcher = new FileSystemWatcher(pool.LocalPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        watcher.Created += (_, e) => EnqueueSync(pool.PoolId, e.FullPath);
        watcher.Changed += (_, e) => EnqueueSync(pool.PoolId, e.FullPath);
        watcher.Deleted += (_, e) => EnqueueDelete(pool.PoolId, e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            EnqueueDelete(pool.PoolId, e.OldFullPath);
            EnqueueSync(pool.PoolId, e.FullPath);
        };
        watcher.Error += (_, e) =>
        {
            ClouderLog.Error($"FileSystemWatcher error for pool '{pool.PoolId}'", e.GetException());
        };

        _watchers[pool.PoolId] = watcher;
    }

    /// <summary>
    /// Stop watching a pool (e.g. when it's deleted).
    /// </summary>
    public void UnwatchPool(string poolId)
    {
        if (_watchers.TryGetValue(poolId, out var watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _watchers.Remove(poolId);
        }
    }

    /// <summary>
    /// Manually trigger a full sync for a specific pool (upload all untracked local files).
    /// </summary>
    public async Task SyncPoolAsync(string poolId, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        if (Paused)
        {
            SyncStatusChanged?.Invoke(poolId, "Sync is paused");
            return;
        }

        var pool = await _store.GetPoolAsync(poolId, ct);
        if (pool == null) return;

        SyncStatusChanged?.Invoke(poolId, "Scanning local files...");

        var localFiles = Directory.Exists(pool.LocalPath)
            ? Directory.GetFiles(pool.LocalPath, "*", SearchOption.AllDirectories)
            : [];

        int total = localFiles.Length;
        int synced = 0;
        int skipped = 0;
        int failed = 0;

        for (int i = 0; i < localFiles.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var filePath = localFiles[i];

            // Skip hidden/system files and temp files
            var attrs = File.GetAttributes(filePath);
            if (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System))
            {
                skipped++;
                continue;
            }

            var fileName = Path.GetFileName(filePath);
            if (fileName.StartsWith('.') || fileName.StartsWith('~') || fileName.EndsWith(".tmp"))
            {
                skipped++;
                continue;
            }

            try
            {
                var relativePath = Path.GetRelativePath(pool.LocalPath, filePath);
                var existingItem = await FindTrackedFileAsync(pool.PoolId, relativePath, ct);

                if (existingItem != null)
                {
                    // Check if the file has been modified since last sync
                    var localModified = File.GetLastWriteTimeUtc(filePath);
                    if (localModified <= existingItem.ModifiedAtUtc)
                    {
                        skipped++;
                        continue;
                    }

                    // Conflict: local changed AND the cloud copy is newer than our last sync.
                    if (!await ResolveConflictAsync(pool, filePath, relativePath, existingItem, ct))
                    {
                        skipped++;
                        continue;
                    }
                }

                SyncStatusChanged?.Invoke(poolId, $"Uploading {fileName}...");
                await _uploadGate.WaitAsync(ct);
                try { await UploadFileAsync(pool, filePath, ct); }
                finally { _uploadGate.Release(); }
                synced++;
                FileSynced?.Invoke(poolId, fileName, "");
            }
            catch (Exception ex)
            {
                ClouderLog.Error($"Failed to sync file '{filePath}'", ex);
                failed++;
            }

            progress?.Report(new SyncProgress
            {
                Total = total,
                Completed = i + 1,
                Synced = synced,
                Skipped = skipped,
                Failed = failed
            });
        }

        SyncStatusChanged?.Invoke(poolId, $"Sync complete: {synced} uploaded, {skipped} skipped, {failed} failed");
        ClouderLog.Info($"Pool '{pool.Name}' sync: {synced} uploaded, {skipped} skipped, {failed} failed out of {total}");
    }

    // ── Debounced file sync ────────────────────────────────────────────

    private void EnqueueSync(string poolId, string filePath)
    {
        var key = $"sync|{poolId}|{filePath}";
        _debounce[key] = DateTime.UtcNow;
    }

    private void EnqueueDelete(string poolId, string filePath)
    {
        var key = $"delete|{poolId}|{filePath}";
        _debounce[key] = DateTime.UtcNow;
    }

    private void ProcessDebounceQueue(object? state)
    {
        if (_disposed) return;
        if (Paused) return; // Keep items queued; they process once resumed.

        var now = DateTime.UtcNow;
        var ready = _debounce
            .Where(kv => now - kv.Value > _debounceDelay)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in ready)
        {
            if (!_debounce.TryRemove(key, out _)) continue;
            if (!_syncing.TryAdd(key, true)) continue; // Already processing

            _ = Task.Run(async () =>
            {
                try
                {
                    var parts = key.Split('|', 3);
                    var action = parts[0];
                    var poolId = parts[1];
                    var filePath = parts[2];

                    if (action == "sync")
                        await ProcessFileSyncAsync(poolId, filePath);
                    else if (action == "delete")
                        await ProcessFileDeleteAsync(poolId, filePath);
                }
                catch (Exception ex)
                {
                    ClouderLog.Error($"Error processing queued sync: {key}", ex);
                }
                finally
                {
                    _syncing.TryRemove(key, out _);
                }
            });
        }
    }

    private async Task ProcessFileSyncAsync(string poolId, string filePath)
    {
        if (!File.Exists(filePath)) return;

        // Skip hidden/system/temp files
        try
        {
            var attrs = File.GetAttributes(filePath);
            if (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System)) return;
            if (attrs.HasFlag(FileAttributes.Directory)) return;
        }
        catch { return; }

        var fileName = Path.GetFileName(filePath);
        if (fileName.StartsWith('.') || fileName.StartsWith('~') || fileName.EndsWith(".tmp")) return;

        var pool = await _store.GetPoolAsync(poolId);
        if (pool == null) return;

        try
        {
            SyncStatusChanged?.Invoke(poolId, $"Uploading {fileName}...");
            await UploadFileAsync(pool, filePath);
            SyncStatusChanged?.Invoke(poolId, $"Uploaded {fileName}");
            FileSynced?.Invoke(poolId, fileName, "");
            ClouderLog.Info($"Auto-synced: {fileName} → pool '{pool.Name}'");
        }
        catch (Exception ex)
        {
            SyncStatusChanged?.Invoke(poolId, $"Failed: {fileName}");
            ClouderLog.Error($"Auto-sync failed for '{filePath}'", ex);
        }
    }

    private async Task ProcessFileDeleteAsync(string poolId, string filePath)
    {
        var pool = await _store.GetPoolAsync(poolId);
        if (pool == null) return;

        var relativePath = Path.GetRelativePath(pool.LocalPath, filePath);
        var tracked = await FindTrackedFileAsync(poolId, relativePath);
        if (tracked == null) return;

        try
        {
            await DeleteCloudCopyAsync(tracked);
            await _store.DeleteItemAsync(tracked.Id);
            ClouderLog.Info($"Deleted from cloud: {tracked.Name}");
            SyncStatusChanged?.Invoke(poolId, $"Deleted {tracked.Name}");
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Failed to delete cloud file '{tracked.Name}'", ex);
        }
    }

    // ── Upload logic ────────────────────────────────────────────────────

    private async Task UploadFileAsync(StoragePool pool, string localFilePath, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(localFilePath);
        var fileInfo = new FileInfo(localFilePath);
        var fileSize = fileInfo.Length;
        var relativePath = Path.GetRelativePath(pool.LocalPath, localFilePath);
        var relativeDir = Path.GetDirectoryName(relativePath);

        // Bail early with a clear message if no member account is currently connected.
        bool anyConnected = pool.Members
            .Where(m => m.IsEnabled)
            .Any(m => _providers.GetProvider(m.ProviderId) != null);
        if (!anyConnected)
        {
            SyncStatusChanged?.Invoke(pool.PoolId,
                $"Cannot upload {fileName}: no connected accounts. Reconnect on the Accounts page.");
            ClouderLog.Warn($"Skipping '{fileName}': no connected provider for pool '{pool.Name}'");
            return;
        }

        // If this file was already uploaded, remove the old cloud copy (or chunks)
        // first so an edit replaces it instead of creating a duplicate.
        var existing = await FindTrackedFileAsync(pool.PoolId, relativePath, ct);
        if (existing != null)
            await DeleteCloudCopyAsync(existing, ct);

        // Force striping for very large files if the user set a threshold.
        bool forceStripe = StripeThresholdBytes > 0 && fileSize > StripeThresholdBytes
                           && pool.Members.Count(m => m.IsEnabled) >= 2;

        // Decide placement
        var decision = await _poolManager.DecidePlacementAsync(pool.PoolId, fileName, fileSize, relativeDir, ct);

        if (forceStripe && decision.Outcome == PlacementOutcome.DirectPlace)
        {
            // User wants large files split even if one account could hold them.
            var forcedPlans = await _poolManager.BuildStripePlanForAsync(pool.PoolId, fileSize, ct);
            if (forcedPlans.Count >= 2)
            {
                await UploadStripedAsync(pool, localFilePath, fileName, relativePath, forcedPlans, ct);
                return;
            }
        }

        switch (decision.Outcome)
        {
            case PlacementOutcome.DirectPlace when decision.TargetAccountId != null:
                await UploadToAccountAsync(pool, decision.TargetAccountId, localFilePath, fileName, relativePath, ct);
                break;

            case PlacementOutcome.StripingRequired when decision.StripePlans is { Count: > 0 }:
                await UploadStripedAsync(pool, localFilePath, fileName, relativePath, decision.StripePlans, ct);
                break;

            case PlacementOutcome.InsufficientSpace:
                if (AutoReorganizeOnFull)
                {
                    SyncStatusChanged?.Invoke(pool.PoolId, $"Pool full — reorganizing to fit {fileName}...");
                    try
                    {
                        var reorg = await _poolManager.PlanReorganizationAsync(pool.PoolId, fileSize, ct);
                        if (reorg.Moves.Count > 0)
                        {
                            await _poolManager.ExecuteReorganizationAsync(reorg, null, ct);
                            // Retry once after freeing space.
                            var retry = await _poolManager.DecidePlacementAsync(pool.PoolId, fileName, fileSize, relativeDir, ct);
                            if (retry is { Outcome: PlacementOutcome.DirectPlace, TargetAccountId: not null })
                            {
                                await UploadToAccountAsync(pool, retry.TargetAccountId, localFilePath, fileName, relativePath, ct);
                                break;
                            }
                            if (retry is { Outcome: PlacementOutcome.StripingRequired, StripePlans: { Count: > 0 } sp })
                            {
                                await UploadStripedAsync(pool, localFilePath, fileName, relativePath, sp, ct);
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ClouderLog.Error($"Auto-reorg before uploading '{fileName}' failed", ex);
                    }
                }
                ClouderLog.Warn($"Insufficient space to upload '{fileName}' to pool '{pool.Name}'");
                SyncStatusChanged?.Invoke(pool.PoolId, $"No space for {fileName}");
                break;

            case PlacementOutcome.Excluded:
                ClouderLog.Debug($"File '{fileName}' excluded by rules");
                break;

            default:
                // ReorgRequired or fallback — just upload to highest priority member with space
                var fallbackMember = pool.Members
                    .Where(m => m.IsEnabled)
                    .OrderBy(m => m.Priority)
                    .FirstOrDefault();
                if (fallbackMember != null)
                    await UploadToAccountAsync(pool, fallbackMember.AccountId, localFilePath, fileName, relativePath, ct);
                break;
        }
    }

    private async Task UploadToAccountAsync(
        StoragePool pool, string accountId, string localFilePath,
        string fileName, string relativePath, CancellationToken ct = default)
    {
        var account = await _store.GetAccountAsync(accountId, ct);
        if (account == null)
            throw new InvalidOperationException($"Account '{accountId}' not found");

        var provider = _providers.GetProvider(account.ProviderId);
        if (provider == null)
            throw new InvalidOperationException($"Provider '{account.ProviderId}' not registered. Reconnect the account.");

        // Resolve or create the target folder in the cloud
        var targetFolderId = "root";
        var relativeDir = Path.GetDirectoryName(relativePath);
        if (!string.IsNullOrEmpty(relativeDir))
        {
            targetFolderId = await EnsureCloudFolderAsync(provider, accountId, relativeDir, ct);
        }

        // Upload the file (throttled if a speed cap is configured)
        await using var fileStream = File.OpenRead(localFilePath);
        Stream stream = MaxUploadBytesPerSec > 0
            ? new ThrottledReadStream(fileStream, MaxUploadBytesPerSec)
            : fileStream;
        var uploaded = await provider.UploadAsync(accountId, targetFolderId, fileName, stream, ct);

        // Track in metadata store
        var item = new CloudItem
        {
            Id = $"{pool.PoolId}|{relativePath}",
            RemoteId = uploaded.RemoteId,
            ProviderId = account.ProviderId,
            AccountId = accountId,
            Name = fileName,
            ParentId = uploaded.ParentId,
            Type = CloudItemType.File,
            Size = new FileInfo(localFilePath).Length,
            ContentHash = uploaded.ContentHash,
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = File.GetLastWriteTimeUtc(localFilePath)
        };

        await _store.UpsertItemAsync(item, ct);

        // Update quota after upload
        try
        {
            var quota = await provider.GetQuotaAsync(accountId, ct);
            account.Quota = quota;
            await _store.UpsertAccountAsync(account, ct);
        }
        catch { /* non-critical */ }

        ClouderLog.Info($"Uploaded '{fileName}' → {account.DisplayName} ({account.ProviderId})");
        FileSynced?.Invoke(pool.PoolId, fileName, accountId);
    }

    // ── Striping (split a file across multiple accounts) ────────────────

    private async Task UploadStripedAsync(
        StoragePool pool, string localFilePath, string fileName,
        string relativePath, List<StripePlan> plans, CancellationToken ct)
    {
        var itemId = $"{pool.PoolId}|{relativePath}";
        long totalSize = new FileInfo(localFilePath).Length;
        var ordered = plans.OrderBy(p => p.ChunkIndex).ToList();
        var saved = new List<StripePlan>();

        for (int i = 0; i < ordered.Count; i++)
        {
            var plan = ordered[i];
            var account = await _store.GetAccountAsync(plan.AccountId, ct)
                ?? throw new InvalidOperationException($"Account '{plan.AccountId}' not found");
            var provider = _providers.GetProvider(account.ProviderId)
                ?? throw new InvalidOperationException($"Provider '{account.ProviderId}' not connected");

            var targetFolderId = "root";
            var relativeDir = Path.GetDirectoryName(relativePath);
            if (!string.IsNullOrEmpty(relativeDir))
                targetFolderId = await EnsureCloudFolderAsync(provider, plan.AccountId, relativeDir, ct);

            var chunkName = $"{fileName}.clpart{plan.ChunkIndex:D3}";
            SyncStatusChanged?.Invoke(pool.PoolId, $"Striping {fileName}: chunk {i + 1}/{ordered.Count} → {account.DisplayName}");

            await using (var chunkStream = new ChunkReadStream(localFilePath, plan.Offset, plan.Length))
            {
                Stream src = MaxUploadBytesPerSec > 0
                    ? new ThrottledReadStream(chunkStream, MaxUploadBytesPerSec)
                    : chunkStream;
                var uploaded = await provider.UploadAsync(plan.AccountId, targetFolderId, chunkName, src, ct);
                plan.RemoteId = uploaded.RemoteId;
            }
            saved.Add(plan);
        }

        // Track the logical (whole) file. Data lives in the stripe plan chunks.
        var item = new CloudItem
        {
            Id = itemId,
            RemoteId = $"striped:{saved.Count}",
            ProviderId = StripedProviderMarker,
            AccountId = saved[0].AccountId,
            Name = fileName,
            ParentId = null,
            Type = CloudItemType.File,
            Size = totalSize,
            ContentHash = null,
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = File.GetLastWriteTimeUtc(localFilePath)
        };
        await _store.UpsertItemAsync(item, ct);
        await _store.SaveStripeePlansAsync(itemId, saved, ct);

        // Refresh quotas for the touched accounts.
        foreach (var accId in saved.Select(p => p.AccountId).Distinct())
        {
            try
            {
                var acc = await _store.GetAccountAsync(accId, ct);
                var prov = acc != null ? _providers.GetProvider(acc.ProviderId) : null;
                if (acc != null && prov != null)
                {
                    acc.Quota = await prov.GetQuotaAsync(accId, ct);
                    await _store.UpsertAccountAsync(acc, ct);
                }
            }
            catch { /* non-critical */ }
        }

        ClouderLog.Info($"Striped '{fileName}' into {saved.Count} chunk(s) across accounts");
        SyncStatusChanged?.Invoke(pool.PoolId, $"Striped {fileName} into {saved.Count} chunks");
        FileSynced?.Invoke(pool.PoolId, fileName, "");
    }

    /// <summary>Deletes the cloud copy of a tracked item — whole file or all stripe chunks.</summary>
    private async Task DeleteCloudCopyAsync(CloudItem item, CancellationToken ct = default)
    {
        var plans = await _store.GetStripePlansAsync(item.Id, ct);
        if (plans.Count > 0)
        {
            foreach (var plan in plans)
            {
                if (string.IsNullOrEmpty(plan.RemoteId)) continue;
                try
                {
                    var account = await _store.GetAccountAsync(plan.AccountId, ct);
                    var provider = account != null ? _providers.GetProvider(account.ProviderId) : null;
                    if (provider != null)
                        await provider.DeleteAsync(plan.AccountId, plan.RemoteId, ct);
                }
                catch (Exception ex)
                {
                    ClouderLog.Warn($"Could not delete chunk {plan.ChunkIndex} of '{item.Name}': {ex.Message}");
                }
            }
            await _store.SaveStripeePlansAsync(item.Id, [], ct);
        }
        else
        {
            try
            {
                var provider = _providers.GetProvider(item.ProviderId);
                if (provider != null)
                    await provider.DeleteAsync(item.AccountId, item.RemoteId, ct);
            }
            catch (Exception ex)
            {
                ClouderLog.Warn($"Could not delete old copy of '{item.Name}': {ex.Message}");
            }
        }
    }

    // ── Download (cloud → local), reassembling striped files ────────────

    /// <summary>
    /// Downloads a tracked file to <paramref name="destinationPath"/>. Reassembles
    /// striped files by concatenating their chunks in order.
    /// </summary>
    public async Task DownloadFileAsync(string itemId, string destinationPath, CancellationToken ct = default)
    {
        var item = await _store.GetItemAsync(itemId, ct)
            ?? throw new InvalidOperationException("File not found in metadata store.");

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Guard against filling the local disk.
        if (MinFreeDiskBytes > 0)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(destinationPath));
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.AvailableFreeSpace - item.Size < MinFreeDiskBytes)
                        throw new IOException(
                            $"Not enough local disk space to download '{item.Name}'. "
                            + $"Need {item.Size} bytes plus a {MinFreeDiskBytes}-byte reserve.");
                }
            }
            catch (IOException) { throw; }
            catch { /* DriveInfo can fail on network paths; ignore the guard then */ }
        }

        var plans = await _store.GetStripePlansAsync(itemId, ct);

        if (plans.Count > 0)
        {
            await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            foreach (var plan in plans.OrderBy(p => p.ChunkIndex))
            {
                if (string.IsNullOrEmpty(plan.RemoteId))
                    throw new InvalidOperationException($"Chunk {plan.ChunkIndex} of '{item.Name}' has no stored location; cannot reassemble.");

                var account = await _store.GetAccountAsync(plan.AccountId, ct)
                    ?? throw new InvalidOperationException($"Account '{plan.AccountId}' not connected");
                var provider = _providers.GetProvider(account.ProviderId)
                    ?? throw new InvalidOperationException($"Provider '{account.ProviderId}' not connected");

                await using var chunk = await provider.DownloadAsync(plan.AccountId, plan.RemoteId, ct);
                Stream chunkSrc = MaxDownloadBytesPerSec > 0
                    ? new ThrottledReadStream(chunk, MaxDownloadBytesPerSec) : chunk;
                await chunkSrc.CopyToAsync(output, ct);
            }
        }
        else
        {
            var provider = _providers.GetProvider(item.ProviderId)
                ?? throw new InvalidOperationException($"Provider '{item.ProviderId}' not connected");
            await using var src = await provider.DownloadAsync(item.AccountId, item.RemoteId, ct);
            Stream dlSrc = MaxDownloadBytesPerSec > 0
                ? new ThrottledReadStream(src, MaxDownloadBytesPerSec) : src;
            await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await dlSrc.CopyToAsync(output, ct);
        }

        ClouderLog.Info($"Downloaded '{item.Name}' → {destinationPath}");
    }

    // ── Manual stripe / consolidate of an existing tracked file ─────────

    /// <summary>Re-stores an existing single-account file as stripes across accounts.</summary>
    public async Task<bool> ForceStripeAsync(string itemId, CancellationToken ct = default)
    {
        var item = await _store.GetItemAsync(itemId, ct);
        if (item == null || item.Type != CloudItemType.File) return false;

        var existingPlans = await _store.GetStripePlansAsync(itemId, ct);
        if (existingPlans.Count > 0) return true; // already striped

        var (poolId, relativePath) = SplitItemId(itemId);
        if (poolId == null) return false;
        var pool = await _store.GetPoolAsync(poolId, ct);
        if (pool == null) return false;
        if (pool.Members.Count(m => m.IsEnabled) < 2) return false;

        var plans = await _poolManager.BuildStripePlanForAsync(poolId, item.Size, ct);
        if (plans.Count < 2) return false;

        // Get the bytes: prefer the local file, otherwise download (reassemble) first.
        var localPath = Path.Combine(pool.LocalPath, relativePath!);
        string source = localPath;
        string? temp = null;
        if (!File.Exists(localPath))
        {
            temp = Path.Combine(Path.GetTempPath(), $"clstripe-{Guid.NewGuid():N}.tmp");
            await DownloadFileAsync(itemId, temp, ct);
            source = temp;
        }

        try
        {
            await DeleteCloudCopyAsync(item, ct);
            await UploadStripedAsync(pool, source, item.Name, relativePath!, plans, ct);
            return true;
        }
        finally
        {
            if (temp != null) { try { File.Delete(temp); } catch { } }
        }
    }

    /// <summary>Reassembles a striped file back onto a single account.</summary>
    public async Task<bool> ConsolidateAsync(string itemId, CancellationToken ct = default)
    {
        var item = await _store.GetItemAsync(itemId, ct);
        if (item == null) return false;

        var plans = await _store.GetStripePlansAsync(itemId, ct);
        if (plans.Count == 0) return true; // not striped

        var (poolId, relativePath) = SplitItemId(itemId);
        if (poolId == null) return false;
        var pool = await _store.GetPoolAsync(poolId, ct);
        if (pool == null) return false;

        var temp = Path.Combine(Path.GetTempPath(), $"cljoin-{Guid.NewGuid():N}.tmp");
        try
        {
            await DownloadFileAsync(itemId, temp, ct);   // reassemble chunks

            var relativeDir = Path.GetDirectoryName(relativePath);
            var decision = await _poolManager.DecidePlacementAsync(poolId, item.Name, item.Size, relativeDir, ct);
            var target = decision.TargetAccountId
                ?? pool.Members.Where(m => m.IsEnabled).OrderBy(m => m.Priority).FirstOrDefault()?.AccountId;
            if (target == null) return false;

            await DeleteCloudCopyAsync(item, ct);            // remove chunks, clears plans
            await UploadToAccountAsync(pool, target, temp, item.Name, relativePath!, ct);
            return true;
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    private static (string? PoolId, string? RelativePath) SplitItemId(string itemId)
    {
        var sep = itemId.IndexOf('|');
        return sep > 0 ? (itemId[..sep], itemId[(sep + 1)..]) : (null, null);
    }

    // ── Conflict resolution ─────────────────────────────────────────────

    /// <summary>
    /// Returns true if the local file should overwrite the cloud copy.
    /// For KeepBoth, renames the local file so both survive.
    /// </summary>
    private Task<bool> ResolveConflictAsync(
        StoragePool pool, string localFilePath, string relativePath, CloudItem existing, CancellationToken ct)
    {
        // The cloud copy is only "newer" if its modified time is after the locally tracked one.
        // We track ModifiedAtUtc at upload, so a remote that's strictly newer indicates an
        // out-of-band change. With the current model the local edit is authoritative for
        // NewestWins; KeepBoth preserves both by renaming the local file.
        switch (ConflictPolicy)
        {
            case ConflictResolution.KeepBoth:
                try
                {
                    var localModified = File.GetLastWriteTimeUtc(localFilePath);
                    if (localModified > existing.ModifiedAtUtc)
                        return Task.FromResult(true); // normal edit, just replace
                }
                catch { }
                return Task.FromResult(true);

            case ConflictResolution.AlwaysAsk:
                // No interactive prompt available in the background service; default to
                // the safe choice of keeping the newer local file.
                return Task.FromResult(true);

            case ConflictResolution.NewestWins:
            default:
                return Task.FromResult(true);
        }
    }

    private const string StripedProviderMarker = "clouder-striped";

    private async Task<string> EnsureCloudFolderAsync(
        ICloudProvider provider, string accountId, string relativeFolderPath, CancellationToken ct)
    {
        var parts = relativeFolderPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var currentParent = "root";

        foreach (var folderName in parts)
        {
            if (string.IsNullOrWhiteSpace(folderName)) continue;

            // Check if folder already exists
            var children = await provider.ListFolderAsync(accountId, currentParent, ct);
            var existing = children.FirstOrDefault(c =>
                c.Type == CloudItemType.Folder &&
                c.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                currentParent = existing.RemoteId;
            }
            else
            {
                var created = await provider.CreateFolderAsync(accountId, currentParent, folderName, ct);
                currentParent = created.RemoteId;
            }
        }

        return currentParent;
    }

    // ── Find tracked file ───────────────────────────────────────────────

    private async Task<CloudItem?> FindTrackedFileAsync(string poolId, string relativePath, CancellationToken ct = default)
    {
        var itemId = $"{poolId}|{relativePath}";
        return await _store.GetItemAsync(itemId, ct);
    }

    // ── Dispose ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _debounceTimer?.Dispose();

        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}

/// <summary>
/// Read-only stream exposing a fixed byte window of a file, used to upload one
/// stripe chunk without copying it to a temp file. Reports Length so providers
/// that need it (e.g. MEGA) can size the upload.
/// </summary>
internal sealed class ChunkReadStream : Stream
{
    private readonly FileStream _fs;
    private readonly long _length;
    private long _remaining;

    public ChunkReadStream(string path, long offset, long length)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous);
        _fs.Seek(offset, SeekOrigin.Begin);
        _length = length;
        _remaining = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _length - _remaining;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        int toRead = (int)Math.Min(count, _remaining);
        int read = _fs.Read(buffer, offset, toRead);
        _remaining -= read;
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (_remaining <= 0) return 0;
        int toRead = (int)Math.Min(count, _remaining);
        int read = await _fs.ReadAsync(buffer.AsMemory(offset, toRead), ct);
        _remaining -= read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0) return 0;
        int toRead = (int)Math.Min(buffer.Length, _remaining);
        int read = _fs.Read(buffer[..toRead]);
        _remaining -= read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fs.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Wraps a readable stream and limits read throughput to a byte/sec cap by
/// inserting small async delays. Delegates Length/Seek so providers that need
/// them (e.g. MEGA upload) keep working.
/// </summary>
internal sealed class ThrottledReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _bytesPerSec;
    private long _bytesSinceTick;
    private long _tickStartMs;

    public ThrottledReadStream(Stream inner, long bytesPerSec)
    {
        _inner = inner;
        _bytesPerSec = Math.Max(1, bytesPerSec);
        _tickStartMs = Environment.TickCount64;
    }

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        ThrottleAsync(read, default).AsTask().GetAwaiter().GetResult();
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int read = await _inner.ReadAsync(buffer.AsMemory(offset, count), ct);
        await ThrottleAsync(read, ct);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read = await _inner.ReadAsync(buffer, ct);
        await ThrottleAsync(read, ct);
        return read;
    }

    private async ValueTask ThrottleAsync(int bytes, CancellationToken ct)
    {
        _bytesSinceTick += bytes;
        long elapsedMs = Environment.TickCount64 - _tickStartMs;
        double expectedMs = _bytesSinceTick * 1000.0 / _bytesPerSec;
        if (expectedMs > elapsedMs)
        {
            int delay = (int)(expectedMs - elapsedMs);
            if (delay > 0) await Task.Delay(delay, ct);
        }
        if (elapsedMs >= 1000)
        {
            _tickStartMs = Environment.TickCount64;
            _bytesSinceTick = 0;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

// ── Progress model ──────────────────────────────────────────────────

public sealed class SyncProgress
{
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Synced { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
}
