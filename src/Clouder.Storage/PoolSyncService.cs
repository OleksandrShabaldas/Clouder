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
    private readonly ConcurrentDictionary<string, int> _retryAttempts = [];
    private readonly ConcurrentDictionary<string, DateTime> _suppressed = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(2);
    private const int MaxRetryAttempts = 5;

    private readonly RemoteRootResolver _roots;
    private readonly ConflictHandler _conflicts;

    /// <summary>
    /// When set and active, uploaded files are converted to cloud-backed placeholders so
    /// Explorer shows the "in sync" overlay and their space can later be freed.
    /// </summary>
    public Clouder.Core.Sync.IPlaceholderSink? Placeholders { get; set; }

    /// <summary>
    /// When set, the copy an edit replaces is kept as a version instead of being deleted.
    /// </summary>
    public FileVersionService? Versions { get; set; }
    private Timer? _debounceTimer;
    private bool _disposed;

    /// <summary>When true, file changes are queued but not uploaded until resumed.</summary>
    public bool Paused { get; set; }

    /// <summary>How to resolve a local file that conflicts with a newer cloud copy.</summary>
    public ConflictResolution ConflictPolicy
    {
        get => _conflicts.Policy;
        set => _conflicts.Policy = value;
    }

    /// <summary>Size (bytes) above which a file is split across accounts. 0 = never.</summary>
    public long StripeThresholdBytes { get; set; }

    /// <summary>Speed limits, shared with the other sync services so caps are global.</summary>
    public TransferBudget Budget { get; }

    /// <summary>Upload throughput cap in bytes/sec. 0 = unlimited.</summary>
    public long MaxUploadBytesPerSec
    {
        get => Budget.Upload.BytesPerSecond;
        set => Budget.Upload.BytesPerSecond = value;
    }

    /// <summary>Download throughput cap in bytes/sec. 0 = unlimited.</summary>
    public long MaxDownloadBytesPerSec
    {
        get => Budget.Download.BytesPerSecond;
        set => Budget.Download.BytesPerSecond = value;
    }

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

    public PoolSyncService(
        IMetadataStore store,
        IProviderRegistry providers,
        ConflictHandler? conflicts = null,
        RemoteRootResolver? roots = null,
        TransferBudget? budget = null)
    {
        _store = store;
        _providers = providers;
        _poolManager = new StoragePoolManager(store, providers);
        _conflicts = conflicts ?? new ConflictHandler(store);
        _roots = roots ?? new RemoteRootResolver(store);
        Budget = budget ?? new TransferBudget();
    }

    /// <summary>
    /// Appends to the transfer history that backs the dashboard's activity feed.
    /// Never throws: history is diagnostics, and losing a row must not fail a sync.
    /// </summary>
    private async Task RecordTransferAsync(
        string poolId, string? accountId, string fileName, string? relativePath,
        TransferKind kind, TransferOutcome outcome, long bytes,
        long startedAtMs, string? error = null, CancellationToken ct = default,
        int chunkCount = 0, string? accountIds = null)
    {
        try
        {
            await _store.AddTransferAsync(new TransferRecord
            {
                TransferId = Guid.NewGuid().ToString("N"),
                PoolId = poolId,
                AccountId = accountId,
                FileName = fileName,
                RelativePath = relativePath,
                Kind = kind,
                Outcome = outcome,
                Bytes = bytes,
                DurationMs = Math.Max(0, Environment.TickCount64 - startedAtMs),
                TimestampUtc = DateTime.UtcNow,
                Error = error,
                ItemId = relativePath != null ? $"{poolId}|{relativePath}" : null,
                ChunkCount = chunkCount,
                AccountIds = accountIds ?? accountId
            }, ct);
        }
        catch (Exception ex)
        {
            ClouderLog.Debug($"Could not record transfer history for '{fileName}': {ex.Message}");
        }
    }

    // ── Watcher suppression (used while the downloader writes locally) ──

    /// <summary>
    /// Ignore file-watcher events for a path until the window expires. The cloud→local
    /// downloader calls this so writing a downloaded file doesn't immediately queue it
    /// for upload again.
    /// </summary>
    public void SuppressLocalWrites(string fullPath, TimeSpan window)
    {
        _suppressed[Path.GetFullPath(fullPath)] = DateTime.UtcNow + window;
    }

    private bool IsSuppressed(string fullPath)
    {
        string key;
        try { key = Path.GetFullPath(fullPath); }
        catch { return false; }

        if (!_suppressed.TryGetValue(key, out var until)) return false;
        if (DateTime.UtcNow <= until) return true;

        _suppressed.TryRemove(key, out _);
        return false;
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

            // A renamed directory fires a single event for the directory itself;
            // the files inside get no events of their own, so enqueue each one.
            try
            {
                if (Directory.Exists(e.FullPath))
                {
                    foreach (var file in Directory.EnumerateFiles(e.FullPath, "*", SearchOption.AllDirectories))
                        EnqueueSync(pool.PoolId, file);
                }
                else
                {
                    EnqueueSync(pool.PoolId, e.FullPath);
                }
            }
            catch (Exception ex)
            {
                ClouderLog.Warn($"Failed to enumerate renamed path '{e.FullPath}': {ex.Message}");
            }
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
            var fileName = Path.GetFileName(filePath);

            try
            {
                // Skip hidden/system files and temp files. A file can vanish
                // mid-scan, so even the attribute check belongs inside the try.
                var attrs = File.GetAttributes(filePath);
                if (attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System))
                {
                    skipped++;
                    continue;
                }

                if (fileName.StartsWith('.') || fileName.StartsWith('~') || fileName.EndsWith(".tmp"))
                {
                    skipped++;
                    continue;
                }

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
                var gate = _uploadGate;
                await gate.WaitAsync(ct);
                UploadOutcome outcome;
                try { outcome = await UploadFileAsync(pool, filePath, ct); }
                finally { gate.Release(); }

                if (outcome == UploadOutcome.Uploaded)
                    synced++;
                else
                    skipped++; // excluded, no provider, or no space — not an upload
            }
            catch (FileNotFoundException) { skipped++; }
            catch (DirectoryNotFoundException) { skipped++; }
            catch (Exception ex)
            {
                ClouderLog.Error($"Failed to sync file '{filePath}'", ex);
                failed++;
            }
            finally
            {
                // In a finally so that the `continue`s above (skipped files) still
                // report — otherwise a sweep where everything is already in sync
                // would never report progress at all.
                progress?.Report(new SyncProgress
                {
                    Total = total,
                    Completed = i + 1,
                    Synced = synced,
                    Skipped = skipped,
                    Failed = failed
                });
            }
        }

        SyncStatusChanged?.Invoke(poolId, $"Sync complete: {synced} uploaded, {skipped} skipped, {failed} failed");
        ClouderLog.Info($"Pool '{pool.Name}' sync: {synced} uploaded, {skipped} skipped, {failed} failed out of {total}");
    }

    /// <summary>
    /// Brings already-synced local files under Explorer's cloud-file model: converts them
    /// to placeholders and marks them in sync.
    ///
    /// Without this, any file uploaded before Explorer integration was switched on sits at
    /// "Sync pending" forever — it's already up to date, so it never re-uploads, and
    /// <see cref="Clouder.Core.Sync.IPlaceholderSink.OnUploaded"/> only fires on upload.
    /// Called when a pool's sync root connects.
    /// </summary>
    public async Task<int> ReconcilePlaceholdersAsync(string poolId, CancellationToken ct = default)
    {
        var sink = Placeholders;
        if (sink == null || !sink.IsActiveFor(poolId)) return 0;

        var pool = await _store.GetPoolAsync(poolId, ct);
        if (pool == null) return 0;

        var prefix = poolId + "|";
        var tracked = await _store.GetItemsByIdPrefixAsync(prefix, ct);
        int reconciled = 0;

        foreach (var item in tracked)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Type != CloudItemType.File) continue;

            var relativePath = item.Id[prefix.Length..];
            var localPath = Path.Combine(pool.LocalPath, relativePath);
            if (!File.Exists(localPath)) continue;

            try
            {
                SuppressLocalWrites(localPath, TimeSpan.FromSeconds(30));
                sink.OnUploaded(poolId, localPath, item.Id);
                reconciled++;
            }
            catch (Exception ex)
            {
                ClouderLog.Debug($"Could not reconcile '{relativePath}' with Explorer: {ex.Message}");
            }
        }

        if (reconciled > 0)
            ClouderLog.Info($"Marked {reconciled} existing file(s) as cloud-backed in pool '{pool.Name}'");

        return reconciled;
    }

    // ── Debounced file sync ────────────────────────────────────────────

    private void EnqueueSync(string poolId, string filePath)
    {
        if (IsSuppressed(filePath)) return;
        var key = $"sync|{poolId}|{filePath}";
        _debounce[key] = DateTime.UtcNow;
    }

    private void EnqueueDelete(string poolId, string filePath)
    {
        if (IsSuppressed(filePath)) return;
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
                        await HandleLocalDeletionAsync(poolId, filePath);
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
        if (IsSuppressed(filePath)) return; // queued before the downloader claimed this path

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

        // The watcher fires for any touch — including our own download writing the
        // file. Only upload when the file is genuinely newer than what we last synced.
        var relativePathForCheck = Path.GetRelativePath(pool.LocalPath, filePath);
        var trackedForCheck = await FindTrackedFileAsync(poolId, relativePathForCheck);
        if (trackedForCheck != null)
        {
            DateTime localModified;
            try { localModified = File.GetLastWriteTimeUtc(filePath); }
            catch { return; }

            if (localModified <= trackedForCheck.ModifiedAtUtc) return;

            if (!await ResolveConflictAsync(pool, filePath, relativePathForCheck, trackedForCheck, default))
                return;
        }

        var retryKey = $"sync|{poolId}|{filePath}";
        var gate = _uploadGate;
        try
        {
            SyncStatusChanged?.Invoke(poolId, $"Uploading {fileName}...");
            await gate.WaitAsync();
            UploadOutcome outcome;
            try { outcome = await UploadFileAsync(pool, filePath); }
            finally { gate.Release(); }

            if (outcome == UploadOutcome.Uploaded)
            {
                SyncStatusChanged?.Invoke(poolId, $"Uploaded {fileName}");
                ClouderLog.Info($"Auto-synced: {fileName} → pool '{pool.Name}'");
            }
            _retryAttempts.TryRemove(retryKey, out _);
        }
        catch (Exception ex)
        {
            SyncStatusChanged?.Invoke(poolId, $"Failed: {fileName}");
            ClouderLog.Error($"Auto-sync failed for '{filePath}'", ex);
            await RecordTransferAsync(poolId, null, fileName, relativePathForCheck,
                TransferKind.Upload, TransferOutcome.Failed, 0, Environment.TickCount64, ex.Message);
            ScheduleRetry(retryKey, fileName);
        }
    }

    /// <summary>
    /// Re-enqueues a failed upload with exponential backoff (15s, 1m, 4m, capped at 10m).
    /// After <see cref="MaxRetryAttempts"/> failures the periodic full sweep remains the backstop.
    /// </summary>
    private void ScheduleRetry(string key, string fileName)
    {
        var attempt = _retryAttempts.AddOrUpdate(key, 1, (_, v) => v + 1);
        if (attempt > MaxRetryAttempts)
        {
            _retryAttempts.TryRemove(key, out _);
            ClouderLog.Warn($"Giving up on '{fileName}' after {MaxRetryAttempts} retries; the periodic sync will try again.");
            return;
        }

        var delaySeconds = Math.Min(600, 15 * Math.Pow(4, attempt - 1));
        // The debounce queue processes entries once "now - value > debounceDelay",
        // so a future timestamp delays processing by exactly the backoff.
        _debounce[key] = DateTime.UtcNow + TimeSpan.FromSeconds(delaySeconds);
        ClouderLog.Warn($"Upload of '{fileName}' failed — retry {attempt}/{MaxRetryAttempts} in {delaySeconds:F0}s");
    }

    /// <summary>
    /// Propagates a local deletion to the cloud. Handles both a single file and a
    /// deleted directory (whose children never get their own watcher events).
    /// </summary>
    public async Task HandleLocalDeletionAsync(string poolId, string filePath, CancellationToken ct = default)
    {
        var pool = await _store.GetPoolAsync(poolId, ct);
        if (pool == null) return;

        var relativePath = Path.GetRelativePath(pool.LocalPath, filePath);

        var tracked = await FindTrackedFileAsync(poolId, relativePath, ct);
        if (tracked != null)
        {
            long deleteStartedMs = Environment.TickCount64;
            try
            {
                await DeleteCloudCopyAsync(tracked, ct);
                // Retained versions live outside the pool folder, so nothing else would
                // ever clean them up: the metadata rows cascade away with the item but
                // the stored copies would be orphaned forever.
                if (Versions != null) await Versions.DeleteAllVersionsAsync(tracked.Id, ct);
                await _store.DeleteItemAsync(tracked.Id, ct);
                ClouderLog.Info($"Deleted from cloud: {tracked.Name}");
                SyncStatusChanged?.Invoke(poolId, $"Deleted {tracked.Name}");
                await RecordTransferAsync(poolId, tracked.AccountId, tracked.Name, relativePath,
                    TransferKind.Delete, TransferOutcome.Success, tracked.Size, deleteStartedMs, ct: ct);
            }
            catch (Exception ex)
            {
                ClouderLog.Error($"Failed to delete cloud file '{tracked.Name}'", ex);
                await RecordTransferAsync(poolId, tracked.AccountId, tracked.Name, relativePath,
                    TransferKind.Delete, TransferOutcome.Failed, 0, deleteStartedMs, ex.Message, ct);
            }
        }

        // The deleted path may have been a folder: remove everything tracked beneath it.
        var prefix = $"{poolId}|{relativePath}{Path.DirectorySeparatorChar}";
        var children = await _store.GetItemsByIdPrefixAsync(prefix, ct);
        foreach (var child in children)
        {
            try
            {
                await DeleteCloudCopyAsync(child, ct);
                if (Versions != null) await Versions.DeleteAllVersionsAsync(child.Id, ct);
                await _store.DeleteItemAsync(child.Id, ct);
                ClouderLog.Info($"Deleted from cloud (folder removal): {child.Name}");
            }
            catch (Exception ex)
            {
                ClouderLog.Error($"Failed to delete cloud file '{child.Name}'", ex);
            }
        }
        if (children.Count > 0)
            SyncStatusChanged?.Invoke(poolId, $"Deleted folder {relativePath} ({children.Count} file(s))");
    }

    // ── Upload logic ────────────────────────────────────────────────────

    private async Task<UploadOutcome> UploadFileAsync(StoragePool pool, string localFilePath, CancellationToken ct = default)
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
            return UploadOutcome.NoProvider;
        }

        // If this file was uploaded before, snapshot the old cloud location(s) now.
        // The old copy is deleted only AFTER the replacement upload succeeds, so a
        // failed upload can never destroy the sole cloud copy. (The snapshot matters:
        // the store rows are overwritten by the new upload before we delete.)
        var existing = await FindTrackedFileAsync(pool.PoolId, relativePath, ct);
        IReadOnlyList<StripePlan> existingPlans = existing != null
            ? await _store.GetStripePlansAsync(existing.Id, ct)
            : [];

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
                await DeleteReplacedCopyAsync(pool, existing, existingPlans, ct);
                return UploadOutcome.Uploaded;
            }
        }

        switch (decision.Outcome)
        {
            case PlacementOutcome.DirectPlace when decision.TargetAccountId != null:
                await UploadToAccountAsync(pool, decision.TargetAccountId, localFilePath, fileName, relativePath, ct);
                await DeleteReplacedCopyAsync(pool, existing, existingPlans, ct);
                return UploadOutcome.Uploaded;

            case PlacementOutcome.StripingRequired when decision.StripePlans is { Count: > 0 }:
                await UploadStripedAsync(pool, localFilePath, fileName, relativePath, decision.StripePlans, ct);
                await DeleteReplacedCopyAsync(pool, existing, existingPlans, ct);
                return UploadOutcome.Uploaded;

            case PlacementOutcome.InsufficientSpace:
                // The old copy of this same file may be what's occupying the space.
                // Delete it first (the data is safe in the local file) and re-decide.
                if (existing != null)
                {
                    await DeleteReplacedCopyAsync(pool, existing, existingPlans, ct);
                    existing = null;
                    existingPlans = [];
                    var redecide = await _poolManager.DecidePlacementAsync(pool.PoolId, fileName, fileSize, relativeDir, ct);
                    if (redecide is { Outcome: PlacementOutcome.DirectPlace, TargetAccountId: not null })
                    {
                        await UploadToAccountAsync(pool, redecide.TargetAccountId, localFilePath, fileName, relativePath, ct);
                        return UploadOutcome.Uploaded;
                    }
                    if (redecide is { Outcome: PlacementOutcome.StripingRequired, StripePlans: { Count: > 0 } rsp })
                    {
                        await UploadStripedAsync(pool, localFilePath, fileName, relativePath, rsp, ct);
                        return UploadOutcome.Uploaded;
                    }
                }
                if (await TryReorgAndUploadAsync(pool, localFilePath, fileName, fileSize, relativePath, relativeDir, ct))
                    return UploadOutcome.Uploaded;
                ClouderLog.Warn($"Insufficient space to upload '{fileName}' to pool '{pool.Name}'");
                SyncStatusChanged?.Invoke(pool.PoolId, $"No space for {fileName}");
                return UploadOutcome.NoSpace;

            case PlacementOutcome.Excluded:
                ClouderLog.Debug($"File '{fileName}' excluded by rules");
                return UploadOutcome.Excluded;

            case PlacementOutcome.ReorgRequired when decision.ReorgPlan is { Moves.Count: > 0 } && AutoReorganizeOnFull:
                // No single member can take the file as-is, but shuffling existing
                // files frees enough room. Execute the plan, then place normally.
                SyncStatusChanged?.Invoke(pool.PoolId, $"Reorganizing pool to fit {fileName}...");
                try
                {
                    await _poolManager.ExecuteReorganizationAsync(decision.ReorgPlan, null, ct);
                    var afterReorg = await _poolManager.DecidePlacementAsync(pool.PoolId, fileName, fileSize, relativeDir, ct);
                    if (afterReorg is { Outcome: PlacementOutcome.DirectPlace, TargetAccountId: not null })
                    {
                        await UploadToAccountAsync(pool, afterReorg.TargetAccountId, localFilePath, fileName, relativePath, ct);
                        await DeleteReplacedCopyAsync(pool, existing, existingPlans, ct);
                        return UploadOutcome.Uploaded;
                    }
                }
                catch (Exception ex)
                {
                    ClouderLog.Error($"Reorganization before uploading '{fileName}' failed", ex);
                }
                goto default;

            default:
                // Fallback — upload to the highest priority enabled member.
                var fallbackMember = pool.Members
                    .Where(m => m.IsEnabled)
                    .OrderBy(m => m.Priority)
                    .FirstOrDefault();
                if (fallbackMember != null)
                {
                    await UploadToAccountAsync(pool, fallbackMember.AccountId, localFilePath, fileName, relativePath, ct);
                    await DeleteReplacedCopyAsync(pool, existing, existingPlans, ct);
                    return UploadOutcome.Uploaded;
                }
                return UploadOutcome.NoSpace;
        }
    }

    /// <summary>Auto-reorganize to free space, then retry the placement once. True if uploaded.</summary>
    private async Task<bool> TryReorgAndUploadAsync(
        StoragePool pool, string localFilePath, string fileName, long fileSize,
        string relativePath, string? relativeDir, CancellationToken ct)
    {
        if (!AutoReorganizeOnFull) return false;

        SyncStatusChanged?.Invoke(pool.PoolId, $"Pool full — reorganizing to fit {fileName}...");
        try
        {
            var reorg = await _poolManager.PlanReorganizationAsync(pool.PoolId, fileSize, ct);
            if (reorg.Moves.Count == 0) return false;

            await _poolManager.ExecuteReorganizationAsync(reorg, null, ct);

            var retry = await _poolManager.DecidePlacementAsync(pool.PoolId, fileName, fileSize, relativeDir, ct);
            if (retry is { Outcome: PlacementOutcome.DirectPlace, TargetAccountId: not null })
            {
                await UploadToAccountAsync(pool, retry.TargetAccountId, localFilePath, fileName, relativePath, ct);
                return true;
            }
            if (retry is { Outcome: PlacementOutcome.StripingRequired, StripePlans: { Count: > 0 } sp })
            {
                await UploadStripedAsync(pool, localFilePath, fileName, relativePath, sp, ct);
                return true;
            }
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Auto-reorg before uploading '{fileName}' failed", ex);
        }
        return false;
    }

    /// <summary>
    /// Retires the pre-replacement cloud copy captured before an upload. With versioning
    /// on, the copy is moved into the pool's versions folder and kept; otherwise it's
    /// deleted. Uses only the in-memory snapshot — the store rows already describe the
    /// NEW copy by the time this runs, so nothing is re-read (and stripe plans in the
    /// store are not touched). Failures are logged, never thrown: worst case is a stray
    /// extra copy, not data loss.
    /// </summary>
    private async Task DeleteReplacedCopyAsync(
        StoragePool pool, CloudItem? oldItem, IReadOnlyList<StripePlan> oldPlans, CancellationToken ct = default)
    {
        if (oldItem == null) return;

        // Keeping the old copy as a version replaces deleting it entirely.
        if (Versions != null && await Versions.TryRetainAsync(pool, oldItem, oldPlans, ct))
            return;

        if (oldPlans.Count > 0)
        {
            foreach (var plan in oldPlans)
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
                    ClouderLog.Warn($"Could not delete replaced chunk {plan.ChunkIndex} of '{oldItem.Name}': {ex.Message}");
                }
            }
        }
        else
        {
            try
            {
                var provider = _providers.GetProvider(oldItem.ProviderId);
                if (provider != null)
                    await provider.DeleteAsync(oldItem.AccountId, oldItem.RemoteId, ct);
            }
            catch (Exception ex)
            {
                ClouderLog.Warn($"Could not delete replaced copy of '{oldItem.Name}': {ex.Message}");
            }
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

        long startedAtMs = Environment.TickCount64;

        // Everything this pool stores lives under its own remote folder.
        var targetFolderId = await ResolveMemberRootAsync(pool, accountId, provider, ct);
        var relativeDir = Path.GetDirectoryName(relativePath);
        if (!string.IsNullOrEmpty(relativeDir))
        {
            targetFolderId = await EnsureCloudFolderAsync(provider, accountId, targetFolderId, relativeDir, ct);
        }

        // Upload the file (throttled if a speed cap is configured).
        // Scoped so our read handle is closed before anything else touches the file:
        // converting it to an Explorer placeholder below fails with
        // ERROR_CLOUD_FILE_INVALID_REQUEST while this process still holds it open.
        CloudItem uploaded;
        await using (var fileStream = File.OpenRead(localFilePath))
        {
            Stream stream = MaxUploadBytesPerSec > 0
                ? new ThrottledReadStream(fileStream, Budget.Upload)
                : fileStream;
            uploaded = await provider.UploadAsync(accountId, targetFolderId, fileName, stream, ct);
        }

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

        // If a previous version of this file was striped, its plan rows would now
        // describe chunks that no longer back this item — clear them.
        await _store.SaveStripeePlansAsync(item.Id, [], ct);

        // Update quota after upload, and seed the cache with the fresh value so the
        // next placement decision sees the space this upload just consumed.
        try
        {
            var quota = await provider.GetQuotaAsync(accountId, ct);
            account.Quota = quota;
            await _store.UpsertAccountAsync(account, ct);
            _poolManager.Quotas.Set(accountId, quota);
        }
        catch { _poolManager.Quotas.Invalidate(accountId); }

        // Let Explorer know this file is now backed by the cloud.
        try { Placeholders?.OnUploaded(pool.PoolId, localFilePath, item.Id); }
        catch (Exception ex) { ClouderLog.Debug($"Placeholder update skipped for '{fileName}': {ex.Message}"); }

        await RecordTransferAsync(pool.PoolId, accountId, fileName, relativePath,
            TransferKind.Upload, TransferOutcome.Success, item.Size, startedAtMs, ct: ct);

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
        long startedAtMs = Environment.TickCount64;

        for (int i = 0; i < ordered.Count; i++)
        {
            var plan = ordered[i];
            var account = await _store.GetAccountAsync(plan.AccountId, ct)
                ?? throw new InvalidOperationException($"Account '{plan.AccountId}' not found");
            var provider = _providers.GetProvider(account.ProviderId)
                ?? throw new InvalidOperationException($"Provider '{account.ProviderId}' not connected");

            var targetFolderId = await ResolveMemberRootAsync(pool, plan.AccountId, provider, ct);
            var relativeDir = Path.GetDirectoryName(relativePath);
            if (!string.IsNullOrEmpty(relativeDir))
                targetFolderId = await EnsureCloudFolderAsync(provider, plan.AccountId, targetFolderId, relativeDir, ct);

            var chunkName = $"{fileName}.clpart{plan.ChunkIndex:D3}";
            SyncStatusChanged?.Invoke(pool.PoolId, $"Striping {fileName}: chunk {i + 1}/{ordered.Count} → {account.DisplayName}");

            await using (var chunkStream = new ChunkReadStream(localFilePath, plan.Offset, plan.Length))
            {
                Stream src = MaxUploadBytesPerSec > 0
                    ? new ThrottledReadStream(chunkStream, Budget.Upload)
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
                    _poolManager.Quotas.Set(accId, acc.Quota);
                }
            }
            catch { _poolManager.Quotas.Invalidate(accId); }
        }

        // Striped uploads were previously absent from the history entirely, so a split
        // file looked like it had never synced. Record the logical file once, carrying
        // the chunk count and every account involved.
        await RecordTransferAsync(pool.PoolId, saved[0].AccountId, fileName, relativePath,
            TransferKind.Upload, TransferOutcome.Success, totalSize, startedAtMs, ct: ct,
            chunkCount: saved.Count,
            accountIds: string.Join(",", saved.Select(p => p.AccountId).Distinct()));

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
                    ? new ThrottledReadStream(chunk, Budget.Download) : chunk;
                await chunkSrc.CopyToAsync(output, ct);
            }
        }
        else
        {
            var provider = _providers.GetProvider(item.ProviderId)
                ?? throw new InvalidOperationException($"Provider '{item.ProviderId}' not connected");
            await using var src = await provider.DownloadAsync(item.AccountId, item.RemoteId, ct);
            Stream dlSrc = MaxDownloadBytesPerSec > 0
                ? new ThrottledReadStream(src, Budget.Download) : src;
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
    /// Called before uploading a locally-changed file we've synced before. Checks whether
    /// the cloud copy also changed since that sync and, if so, applies the conflict policy.
    /// Returns true if the upload should proceed (the local copy wins).
    /// </summary>
    private async Task<bool> ResolveConflictAsync(
        StoragePool pool, string localFilePath, string relativePath, CloudItem existing, CancellationToken ct)
    {
        // Striped files have no single remote object to compare against.
        if (existing.ProviderId == StripedProviderMarker)
            return true;

        CloudItem? remote = null;
        try
        {
            var provider = _providers.GetProvider(existing.ProviderId);
            if (provider != null)
                remote = await provider.GetItemAsync(existing.AccountId, existing.RemoteId, ct);
        }
        catch (Exception ex)
        {
            ClouderLog.Warn($"Could not check the cloud copy of '{relativePath}': {ex.Message}");
            return true; // can't tell — proceed with the local edit
        }

        // Gone remotely, or unchanged since our last sync: no conflict.
        if (remote == null || remote.ModifiedAtUtc <= existing.ModifiedAtUtc)
            return true;

        long localSize;
        DateTime localModified;
        try
        {
            var info = new FileInfo(localFilePath);
            localSize = info.Length;
            localModified = info.LastWriteTimeUtc;
        }
        catch { return true; }

        var outcome = await _conflicts.HandleAsync(
            pool, relativePath, localFilePath, localModified, localSize, remote, existing.AccountId, ct);

        // UseRemote / KeptBothTakeRemote / Deferred all mean "don't upload now".
        // The remote sync pass brings the cloud copy down.
        return outcome == ConflictOutcome.UseLocal;
    }

    private const string StripedProviderMarker = "clouder-striped";

    /// <summary>The remote folder this pool owns on the given account (created on first use).</summary>
    private async Task<string> ResolveMemberRootAsync(
        StoragePool pool, string accountId, ICloudProvider provider, CancellationToken ct)
    {
        var member = pool.Members.FirstOrDefault(m => m.AccountId == accountId);
        if (member == null) return "root";
        return await _roots.EnsureAsync(provider, pool, member, ct);
    }

    private async Task<string> EnsureCloudFolderAsync(
        ICloudProvider provider, string accountId, string baseFolderId, string relativeFolderPath, CancellationToken ct)
    {
        var parts = relativeFolderPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var currentParent = baseFolderId;

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
/// Wraps a readable stream and charges every read against a shared
/// <see cref="BandwidthLimiter"/>, so a speed limit is a budget across all concurrent
/// transfers rather than per transfer. Delegates Length/Seek so providers that need
/// them (e.g. MEGA upload) keep working.
/// </summary>
internal sealed class ThrottledReadStream : Stream
{
    private readonly Stream _inner;
    private readonly BandwidthLimiter _limiter;

    public ThrottledReadStream(Stream inner, BandwidthLimiter limiter)
    {
        _inner = inner;
        _limiter = limiter;
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
        if (read > 0) _limiter.ConsumeAsync(read).AsTask().GetAwaiter().GetResult();
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int read = await _inner.ReadAsync(buffer.AsMemory(offset, count), ct);
        if (read > 0) await _limiter.ConsumeAsync(read, ct);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read = await _inner.ReadAsync(buffer, ct);
        if (read > 0) await _limiter.ConsumeAsync(read, ct);
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

// ── Progress model ──────────────────────────────────────────────────

/// <summary>What actually happened to a file handed to the upload pipeline.</summary>
public enum UploadOutcome
{
    /// <summary>The file (or all its stripe chunks) reached the cloud.</summary>
    Uploaded,
    /// <summary>No member account's provider is connected; nothing was attempted.</summary>
    NoProvider,
    /// <summary>A file rule excluded this file.</summary>
    Excluded,
    /// <summary>The pool has no room even after reorganization.</summary>
    NoSpace
}

public sealed class SyncProgress
{
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Synced { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
}
