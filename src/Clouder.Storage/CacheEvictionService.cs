using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Storage;
using Clouder.Core.Sync;

namespace Clouder.Storage;

/// <summary>
/// Keeps the local footprint of a pool in check by dehydrating files — discarding the
/// local copy of a file that is safely in the cloud, so it stays visible in Explorer
/// but stops occupying disk. This is what makes the "cache size limit" and
/// "dehydrate after N days" settings mean something.
///
/// Only ever touches files that are tracked, in sync, and physically present. A file
/// with unsaved local changes is never a candidate, because dehydrating it would
/// discard those changes.
/// </summary>
public sealed class CacheEvictionService
{
    private readonly IMetadataStore _store;
    private readonly IPlaceholderSink? _placeholders;

    /// <summary>Local cache ceiling in bytes. 0 disables size-based eviction.</summary>
    public long CacheLimitBytes { get; set; }

    /// <summary>Dehydrate files untouched for this many days. 0 disables age-based eviction.</summary>
    public int DehydrateAfterDays { get; set; }

    public CacheEvictionService(IMetadataStore store, IPlaceholderSink? placeholders)
    {
        _store = store;
        _placeholders = placeholders;
    }

    /// <summary>Runs eviction across every pool. Returns the number of bytes freed.</summary>
    public async Task<long> RunAsync(CancellationToken ct = default)
    {
        if (_placeholders == null) return 0;
        if (CacheLimitBytes <= 0 && DehydrateAfterDays <= 0) return 0;

        long freed = 0;
        var pools = await _store.GetAllPoolsAsync(ct);

        foreach (var pool in pools)
        {
            if (!_placeholders.IsActiveFor(pool.PoolId)) continue; // needs Explorer integration

            try
            {
                freed += await EvictPoolAsync(pool, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ClouderLog.Error($"Cache eviction failed for pool '{pool.Name}'", ex);
            }
        }

        if (freed > 0)
            ClouderLog.Info($"Freed {freed / (1024 * 1024)} MB of local disk by making files online-only");

        return freed;
    }

    private async Task<long> EvictPoolAsync(StoragePool pool, CancellationToken ct)
    {
        var prefix = pool.PoolId + "|";
        var tracked = await _store.GetItemsByIdPrefixAsync(prefix, ct);

        var candidates = new List<(CloudItem Item, string Path, long Size, DateTime LastAccess)>();
        long localBytes = 0;

        foreach (var item in tracked)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Type != CloudItemType.File) continue;

            // Anything not settled is off limits — dehydrating a pending upload or an
            // unresolved conflict would throw away the only good copy.
            if (item.SyncState != Clouder.Core.Models.SyncState.Synced) continue;

            var relativePath = item.Id[prefix.Length..];
            var localPath = Path.Combine(pool.LocalPath, relativePath);

            FileInfo info;
            try
            {
                info = new FileInfo(localPath);
                if (!info.Exists) continue;

                // Already online-only: Windows reports these as sparse/offline, so they
                // occupy no disk and are not worth evicting again.
                if (info.Attributes.HasFlag(FileAttributes.Offline)) continue;

                // A local edit that hasn't synced yet must not be discarded.
                if (info.LastWriteTimeUtc > item.ModifiedAtUtc) continue;
            }
            catch { continue; }

            localBytes += info.Length;
            candidates.Add((item, localPath, info.Length, info.LastAccessTimeUtc));
        }

        long freed = 0;

        // Age-based: anything untouched for long enough goes, regardless of total size.
        if (DehydrateAfterDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-DehydrateAfterDays);
            foreach (var candidate in candidates.Where(c => c.LastAccess < cutoff).ToList())
            {
                if (_placeholders!.TryFreeSpace(pool.PoolId, candidate.Path))
                {
                    freed += candidate.Size;
                    localBytes -= candidate.Size;
                    candidates.Remove(candidate);
                }
            }
        }

        // Size-based: if still over the ceiling, evict least-recently-used first.
        if (CacheLimitBytes > 0 && localBytes > CacheLimitBytes)
        {
            foreach (var candidate in candidates.OrderBy(c => c.LastAccess))
            {
                if (localBytes <= CacheLimitBytes) break;
                ct.ThrowIfCancellationRequested();

                if (_placeholders!.TryFreeSpace(pool.PoolId, candidate.Path))
                {
                    freed += candidate.Size;
                    localBytes -= candidate.Size;
                }
            }
        }

        return freed;
    }
}
