using System.Text.Json;
using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Providers;
using Clouder.Core.Storage;

namespace Clouder.Storage;

/// <summary>
/// Keeps previous copies of files so an edit can be undone.
///
/// Clouder replaces files rather than updating them in place: an edit uploads a new
/// object and retires the old one. That means a provider's own revision history never
/// accumulates for pool files, so version history has to be Clouder's own. Instead of
/// deleting the copy being replaced, this moves it into
/// <c>Clouder/.versions/{PoolName}</c> — outside the pool's sync root, so it can never
/// be mistaken for a new remote file — and records where it went.
/// </summary>
public sealed class FileVersionService
{
    private readonly IMetadataStore _store;
    private readonly IProviderRegistry _providers;
    private readonly RemoteRootResolver _roots;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public FileVersionService(IMetadataStore store, IProviderRegistry providers, RemoteRootResolver roots)
    {
        _store = store;
        _providers = providers;
        _roots = roots;
    }

    /// <summary>Keep previous copies at all. When false, replaced copies are simply deleted.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many versions to keep per file. 0 = unlimited.</summary>
    public int MaxVersionsPerFile { get; set; } = 5;

    /// <summary>Discard versions older than this many days. 0 = keep regardless of age.</summary>
    public int RetentionDays { get; set; }

    // ── Retaining ───────────────────────────────────────────────────────

    /// <summary>
    /// Archives the copy that is about to be replaced, following the pool's policy.
    /// Returns true if it was retained (the caller must then NOT delete it); false means
    /// the caller should delete as usual.
    /// </summary>
    public async Task<bool> TryRetainAsync(
        StoragePool pool, CloudItem oldItem, IReadOnlyList<StripePlan> oldPlans, CancellationToken ct = default)
    {
        var policy = pool.VersionPolicy;
        if (!(policy.Enabled ?? Enabled)) return false;

        var relativePath = RelativePathOf(oldItem.Id);

        // Skip files the policy says are too big to be worth keeping history for.
        if (policy.MaxVersionSizeBytes > 0 && oldItem.Size > policy.MaxVersionSizeBytes)
        {
            ClouderLog.Debug(
                $"Not keeping a version of '{relativePath}': {FormatBytes(oldItem.Size)} exceeds the "
                + $"{FormatBytes(policy.MaxVersionSizeBytes)} limit");
            return false;
        }

        try
        {
            var existing = await _store.GetFileVersionsAsync(oldItem.Id, ct);

            // Throttle: applications that save constantly would otherwise fill the
            // history with near-identical copies within a minute.
            if (policy.MinIntervalMinutes > 0 && existing.Count > 0)
            {
                var newest = existing.Max(v => v.CreatedAtUtc);
                if (newest != DateTime.MinValue
                    && DateTime.UtcNow - newest < TimeSpan.FromMinutes(policy.MinIntervalMinutes))
                {
                    ClouderLog.Debug(
                        $"Not keeping a version of '{relativePath}': last one was under "
                        + $"{policy.MinIntervalMinutes} minute(s) ago");
                    return false;
                }
            }

            int nextNumber = existing.Count == 0 ? 1 : existing.Max(v => v.VersionNumber) + 1;

            // SameAccount + Inherit is the cheap path: the object is moved within its own
            // account and no bytes travel. Anything else has to copy the content.
            var version = policy.RequiresTransfer
                ? await RetainByTransferAsync(pool, oldItem, oldPlans, nextNumber, policy, ct)
                : oldPlans.Count > 0
                    ? await RetainStripedAsync(pool, oldItem, oldPlans, relativePath, nextNumber, ct)
                    : await RetainWholeAsync(pool, oldItem, relativePath, nextNumber, ct);

            if (version == null) return false;

            await _store.AddFileVersionAsync(version, ct);
            ClouderLog.Info($"Kept version {nextNumber} of '{relativePath}' ({FormatBytes(version.Size)})");

            await PruneAsync(oldItem.Id, policy, ct);
            await PrunePoolBySizeAsync(pool, ct);
            return true;
        }
        catch (Exception ex)
        {
            // Never block a sync over version keeping — fall back to deleting the old copy.
            ClouderLog.Error($"Could not keep a version of '{oldItem.Name}'", ex);
            return false;
        }
    }

    /// <summary>
    /// Archives a copy that has to be relocated — a different account, a different
    /// stripe layout, or both. Unlike the move-based path this reads the content and
    /// writes it to its new home, then removes the original.
    /// </summary>
    private async Task<FileVersion?> RetainByTransferAsync(
        StoragePool pool, CloudItem oldItem, IReadOnlyList<StripePlan> oldPlans,
        int number, VersionPolicy policy, CancellationToken ct)
    {
        var targets = ResolveTargets(pool, policy, oldItem.Size, oldItem.AccountId);
        if (targets.Count == 0)
        {
            ClouderLog.Warn(
                $"No account is available to store a version of '{oldItem.Name}' under this pool's "
                + "version settings — the old copy will be deleted instead.");
            return null;
        }

        // Pull the current content down once; it is the source for every target chunk.
        var temp = Path.Combine(Path.GetTempPath(), $"clverxfer-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (oldPlans.Count > 0)
                {
                    foreach (var plan in oldPlans.OrderBy(p => p.ChunkIndex))
                    {
                        if (string.IsNullOrEmpty(plan.RemoteId)) return null;
                        var chunkProvider = await ResolveProviderAsync(plan.AccountId, ct);
                        if (chunkProvider == null) return null;

                        await using var chunk = await chunkProvider.DownloadAsync(plan.AccountId, plan.RemoteId, ct);
                        await chunk.CopyToAsync(output, ct);
                    }
                }
                else
                {
                    var provider = await ResolveProviderAsync(oldItem.AccountId, ct);
                    if (provider == null) return null;

                    await using var source = await provider.DownloadAsync(oldItem.AccountId, oldItem.RemoteId, ct);
                    await source.CopyToAsync(output, ct);
                }
            }

            var chunks = new List<VersionChunk>();
            long offset = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                var (accountId, length) = targets[i];
                var member = pool.Members.First(m => m.AccountId == accountId);
                var provider = await ResolveProviderAsync(accountId, ct);
                if (provider == null) return null;

                var folder = await _roots.EnsureVersionsFolderAsync(provider, pool, member, ct);
                var name = targets.Count > 1
                    ? $"{VersionFileName(oldItem.Name, number, oldItem.ModifiedAtUtc)}.clpart{i:D3}"
                    : VersionFileName(oldItem.Name, number, oldItem.ModifiedAtUtc);

                await using var slice = new ChunkReadStream(temp, offset, length);
                var uploaded = await provider.UploadAsync(accountId, folder, name, slice, ct);

                chunks.Add(new VersionChunk
                {
                    ChunkIndex = i,
                    AccountId = accountId,
                    RemoteId = uploaded.RemoteId,
                    Offset = offset,
                    Length = length
                });
                offset += length;
            }

            // The content is safely in its new home, so the original can go.
            await DeleteOriginalAsync(oldItem, oldPlans, ct);

            return new FileVersion
            {
                VersionId = Guid.NewGuid().ToString("N"),
                RemoteVersionId = chunks.Count > 1 ? $"striped:{chunks.Count}" : chunks[0].RemoteId,
                FileId = oldItem.Id,
                Size = oldItem.Size,
                ModifiedAtUtc = oldItem.ModifiedAtUtc,
                AccountId = chunks[0].AccountId,
                ProviderId = oldItem.ProviderId,
                VersionNumber = number,
                CreatedAtUtc = DateTime.UtcNow,
                ChunkManifest = chunks.Count > 1
                    ? JsonSerializer.Serialize(chunks, JsonOpts)
                    : null
            };
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    /// <summary>
    /// Decides which accounts hold the version and how many bytes each takes, from the
    /// pool's placement and striping settings.
    /// </summary>
    private static List<(string AccountId, long Length)> ResolveTargets(
        StoragePool pool, VersionPolicy policy, long size, string currentAccountId)
    {
        var candidates = policy.Placement switch
        {
            VersionPlacement.DedicatedAccounts =>
                pool.Members.Where(m => m.IsEnabled && m.IsVersionStore).ToList(),

            // Balanced spreads history over everything that can hold files, plus any
            // account set aside for versions.
            VersionPlacement.Balanced =>
                pool.Members.Where(m => m.IsEnabled && (!m.ExcludeFromFilePlacement || m.IsVersionStore)).ToList(),

            _ => pool.Members.Where(m => m.IsEnabled && m.AccountId == currentAccountId).ToList()
        };

        if (candidates.Count == 0) return [];

        if (policy.Striping == VersionStriping.Always && candidates.Count > 1)
        {
            // Split evenly; the last slice absorbs the rounding remainder.
            long each = size / candidates.Count;
            var split = new List<(string, long)>();
            long assigned = 0;

            for (int i = 0; i < candidates.Count; i++)
            {
                long length = i == candidates.Count - 1 ? size - assigned : each;
                if (length <= 0) continue;
                split.Add((candidates[i].AccountId, length));
                assigned += length;
            }
            return split;
        }

        // One target. Under Balanced, pick by the configured strategy; the pool's own
        // strategy is the default so versions follow the same habit as files.
        var strategy = policy.PlacementStrategy ?? pool.DefaultStrategy;
        var chosen = policy.Placement == VersionPlacement.SameAccount
            ? candidates[0]
            : PickByStrategy(candidates, strategy);

        return [(chosen.AccountId, size)];
    }

    private static PoolMember PickByStrategy(List<PoolMember> candidates, PlacementStrategy strategy) =>
        strategy switch
        {
            // Without live quota here, priority order is the meaningful tiebreak; the
            // hourly prune keeps any one account from running away with the history.
            Clouder.Core.Models.PlacementStrategy.FillFirst => candidates.OrderBy(m => m.Priority).First(),
            _ => candidates.OrderBy(m => Guid.NewGuid()).First()   // spread round-robin style
        };

    private async Task DeleteOriginalAsync(
        CloudItem oldItem, IReadOnlyList<StripePlan> oldPlans, CancellationToken ct)
    {
        try
        {
            if (oldPlans.Count > 0)
            {
                foreach (var plan in oldPlans)
                {
                    if (string.IsNullOrEmpty(plan.RemoteId)) continue;
                    var provider = await ResolveProviderAsync(plan.AccountId, ct);
                    if (provider != null)
                        await provider.DeleteAsync(plan.AccountId, plan.RemoteId, ct);
                }
            }
            else
            {
                var provider = await ResolveProviderAsync(oldItem.AccountId, ct);
                if (provider != null)
                    await provider.DeleteAsync(oldItem.AccountId, oldItem.RemoteId, ct);
            }
        }
        catch (Exception ex)
        {
            ClouderLog.Warn($"Kept a version of '{oldItem.Name}' but could not remove the original: {ex.Message}");
        }
    }

    /// <summary>
    /// Drops the oldest versions in a pool until the total size is under its cap.
    /// </summary>
    public async Task PrunePoolBySizeAsync(StoragePool pool, CancellationToken ct = default)
    {
        long cap = pool.VersionPolicy.MaxTotalBytes;
        if (cap <= 0) return;

        var prefix = pool.PoolId + "|";
        var versions = (await _store.GetAllFileVersionsAsync(ct))
            .Where(v => v.FileId.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(v => v.CreatedAtUtc)
            .ToList();

        long total = versions.Sum(v => v.Size);
        if (total <= cap) return;

        foreach (var version in versions)
        {
            if (total <= cap) break;

            await DeleteRemoteCopyAsync(version, ct);
            await _store.DeleteFileVersionAsync(version.VersionId, ct);
            total -= version.Size;

            ClouderLog.Debug($"Discarded version {version.VersionNumber} of '{version.FileId}' (pool version cap)");
        }

        ClouderLog.Info($"Version history for '{pool.Name}' trimmed to {FormatBytes(total)}");
    }

    private async Task<FileVersion?> RetainWholeAsync(
        StoragePool pool, CloudItem oldItem, string relativePath, int number, CancellationToken ct)
    {
        var account = await _store.GetAccountAsync(oldItem.AccountId, ct);
        var provider = account != null ? _providers.GetProvider(account.ProviderId) : null;
        if (account == null || provider == null) return null;

        var member = pool.Members.FirstOrDefault(m => m.AccountId == oldItem.AccountId);
        if (member == null) return null;

        var versionsFolder = await _roots.EnsureVersionsFolderAsync(provider, pool, member, ct);
        var moved = await provider.MoveAsync(oldItem.AccountId, oldItem.RemoteId, versionsFolder, ct);

        // Cosmetic: makes the versions folder readable if the user browses it directly.
        await TryRenameAsync(provider, oldItem.AccountId, moved.RemoteId,
            VersionFileName(oldItem.Name, number, oldItem.ModifiedAtUtc), ct);

        return new FileVersion
        {
            VersionId = Guid.NewGuid().ToString("N"),
            RemoteVersionId = moved.RemoteId,
            FileId = oldItem.Id,
            Size = oldItem.Size,
            ModifiedAtUtc = oldItem.ModifiedAtUtc,
            AccountId = oldItem.AccountId,
            ProviderId = account.ProviderId,
            VersionNumber = number,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private async Task<FileVersion?> RetainStripedAsync(
        StoragePool pool, CloudItem oldItem, IReadOnlyList<StripePlan> plans,
        string relativePath, int number, CancellationToken ct)
    {
        var chunks = new List<VersionChunk>();

        foreach (var plan in plans.OrderBy(p => p.ChunkIndex))
        {
            if (string.IsNullOrEmpty(plan.RemoteId)) return null;   // incomplete — can't archive it

            var account = await _store.GetAccountAsync(plan.AccountId, ct);
            var provider = account != null ? _providers.GetProvider(account.ProviderId) : null;
            var member = pool.Members.FirstOrDefault(m => m.AccountId == plan.AccountId);
            if (account == null || provider == null || member == null) return null;

            var versionsFolder = await _roots.EnsureVersionsFolderAsync(provider, pool, member, ct);
            var moved = await provider.MoveAsync(plan.AccountId, plan.RemoteId, versionsFolder, ct);

            await TryRenameAsync(provider, plan.AccountId, moved.RemoteId,
                $"{VersionFileName(oldItem.Name, number, oldItem.ModifiedAtUtc)}.clpart{plan.ChunkIndex:D3}", ct);

            chunks.Add(new VersionChunk
            {
                ChunkIndex = plan.ChunkIndex,
                AccountId = plan.AccountId,
                RemoteId = moved.RemoteId,
                Offset = plan.Offset,
                Length = plan.Length
            });
        }

        return new FileVersion
        {
            VersionId = Guid.NewGuid().ToString("N"),
            // A striped version has no single object; the manifest is the real location.
            RemoteVersionId = $"striped:{chunks.Count}",
            FileId = oldItem.Id,
            Size = oldItem.Size,
            ModifiedAtUtc = oldItem.ModifiedAtUtc,
            AccountId = chunks[0].AccountId,
            ProviderId = oldItem.ProviderId,
            VersionNumber = number,
            CreatedAtUtc = DateTime.UtcNow,
            ChunkManifest = JsonSerializer.Serialize(chunks, JsonOpts)
        };
    }

    // ── Reading a version back ──────────────────────────────────────────

    /// <summary>
    /// Opens the contents of a retained version, reassembling it if it was striped.
    /// The caller disposes the stream.
    /// </summary>
    public async Task<Stream> OpenVersionAsync(string versionId, CancellationToken ct = default)
    {
        var version = await _store.GetFileVersionAsync(versionId, ct)
            ?? throw new InvalidOperationException("That version no longer exists.");

        if (version.IsStriped)
        {
            var chunks = ReadManifest(version);
            var temp = Path.Combine(Path.GetTempPath(), $"clver-{Guid.NewGuid():N}.tmp");

            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var chunk in chunks.OrderBy(c => c.ChunkIndex))
                {
                    var provider = await ResolveProviderAsync(chunk.AccountId, ct)
                        ?? throw new InvalidOperationException(
                            $"The account holding chunk {chunk.ChunkIndex} of this version isn't connected.");

                    await using var source = await provider.DownloadAsync(chunk.AccountId, chunk.RemoteId, ct);
                    await source.CopyToAsync(output, ct);
                }
            }

            return new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None, 4096,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        }

        var single = await ResolveProviderAsync(version.AccountId, ct)
            ?? throw new InvalidOperationException("The account holding this version isn't connected.");

        return await single.DownloadAsync(version.AccountId!, version.RemoteVersionId, ct);
    }

    /// <summary>Writes a version's contents to a local path (e.g. "download a copy").</summary>
    public async Task SaveVersionAsAsync(string versionId, string destinationPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var source = await OpenVersionAsync(versionId, ct);
        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(output, ct);
    }

    /// <summary>
    /// Puts a version's contents back as the file's current copy, by writing it into the
    /// pool folder. The normal sync then uploads it — which archives whatever is current
    /// now, so a restore is itself undoable.
    /// </summary>
    public async Task<string> RestoreAsync(string versionId, CancellationToken ct = default)
    {
        var version = await _store.GetFileVersionAsync(versionId, ct)
            ?? throw new InvalidOperationException("That version no longer exists.");

        var poolId = PoolIdOf(version.FileId);
        var relativePath = RelativePathOf(version.FileId);
        var pool = await _store.GetPoolAsync(poolId, ct)
            ?? throw new InvalidOperationException("The pool this file belongs to no longer exists.");

        var localPath = Path.Combine(pool.LocalPath, relativePath);
        await SaveVersionAsAsync(versionId, localPath, ct);

        // The uploader only replaces the cloud copy when the local file is strictly newer
        // than what it has tracked. "Now" isn't reliably newer — the tracked timestamp can
        // sit in the future after clock skew, a timestamp-preserving tool, or a file copied
        // from another machine — and the restore would then silently never upload. Stamp it
        // past the tracked time so a restore always takes effect.
        var tracked = await _store.GetItemAsync(version.FileId, ct);
        var stamp = DateTime.UtcNow;
        if (tracked != null && tracked.ModifiedAtUtc >= stamp)
            stamp = tracked.ModifiedAtUtc.AddSeconds(1);

        File.SetLastWriteTimeUtc(localPath, stamp);

        ClouderLog.Info($"Restored version {version.VersionNumber} of '{relativePath}'");
        return localPath;
    }

    // ── Deleting ────────────────────────────────────────────────────────

    public async Task<bool> DeleteVersionAsync(string versionId, CancellationToken ct = default)
    {
        var version = await _store.GetFileVersionAsync(versionId, ct);
        if (version == null) return false;

        await DeleteRemoteCopyAsync(version, ct);
        await _store.DeleteFileVersionAsync(versionId, ct);
        return true;
    }

    /// <summary>
    /// Removes every version of a file. Called when the file itself is deleted — the
    /// metadata rows cascade away with the item, but the remote objects would otherwise
    /// be orphaned in the versions folder forever.
    /// </summary>
    public async Task DeleteAllVersionsAsync(string fileId, CancellationToken ct = default)
    {
        var versions = await _store.GetFileVersionsAsync(fileId, ct);
        foreach (var version in versions)
        {
            await DeleteRemoteCopyAsync(version, ct);
            await _store.DeleteFileVersionAsync(version.VersionId, ct);
        }
    }

    private async Task DeleteRemoteCopyAsync(FileVersion version, CancellationToken ct)
    {
        try
        {
            if (version.IsStriped)
            {
                foreach (var chunk in ReadManifest(version))
                {
                    var provider = await ResolveProviderAsync(chunk.AccountId, ct);
                    if (provider != null)
                        await provider.DeleteAsync(chunk.AccountId, chunk.RemoteId, ct);
                }
            }
            else
            {
                var provider = await ResolveProviderAsync(version.AccountId, ct);
                if (provider != null && version.AccountId != null)
                    await provider.DeleteAsync(version.AccountId, version.RemoteVersionId, ct);
            }
        }
        catch (Exception ex)
        {
            // The metadata row still goes; a stray remote object is better than a stuck UI.
            ClouderLog.Warn($"Could not delete the stored copy of version {version.VersionNumber}: {ex.Message}");
        }
    }

    // ── Retention ───────────────────────────────────────────────────────

    /// <summary>
    /// Applies the count and age limits to one file's history. The pool's policy wins
    /// where it sets a value; anything it leaves unset falls back to the global setting.
    /// </summary>
    public async Task PruneAsync(string fileId, VersionPolicy? policy = null, CancellationToken ct = default)
    {
        int maxVersions = policy?.MaxVersionsPerFile ?? MaxVersionsPerFile;
        int retentionDays = policy?.RetentionDays ?? RetentionDays;

        var versions = (await _store.GetFileVersionsAsync(fileId, ct))
            .OrderByDescending(v => v.VersionNumber)
            .ToList();

        var doomed = new List<FileVersion>();

        if (maxVersions > 0 && versions.Count > maxVersions)
            doomed.AddRange(versions.Skip(maxVersions));

        if (retentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            doomed.AddRange(versions
                .Where(v => v.CreatedAtUtc != DateTime.MinValue && v.CreatedAtUtc < cutoff)
                .Where(v => !doomed.Contains(v)));
        }

        foreach (var version in doomed)
        {
            await DeleteRemoteCopyAsync(version, ct);
            await _store.DeleteFileVersionAsync(version.VersionId, ct);
            ClouderLog.Debug($"Discarded version {version.VersionNumber} of '{fileId}' (retention policy)");
        }
    }

    /// <summary>
    /// Housekeeping across every pool: applies each pool's own age limit and total-size
    /// cap. Runs on the hourly tick.
    /// </summary>
    public async Task<int> PruneAllAsync(CancellationToken ct = default)
    {
        int removed = 0;
        var pools = await _store.GetAllPoolsAsync(ct);
        var all = await _store.GetAllFileVersionsAsync(ct);

        foreach (var pool in pools)
        {
            var policy = pool.VersionPolicy;
            int retentionDays = policy.RetentionDays ?? RetentionDays;
            var prefix = pool.PoolId + "|";

            if (retentionDays > 0)
            {
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                var stale = all
                    .Where(v => v.FileId.StartsWith(prefix, StringComparison.Ordinal))
                    .Where(v => v.CreatedAtUtc != DateTime.MinValue && v.CreatedAtUtc < cutoff)
                    .ToList();

                foreach (var version in stale)
                {
                    await DeleteRemoteCopyAsync(version, ct);
                    await _store.DeleteFileVersionAsync(version.VersionId, ct);
                    removed++;
                }
            }

            await PrunePoolBySizeAsync(pool, ct);
        }

        if (removed > 0)
            ClouderLog.Info($"Discarded {removed} version(s) past their retention age");

        return removed;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<int> NextVersionNumberAsync(string fileId, CancellationToken ct)
    {
        var existing = await _store.GetFileVersionsAsync(fileId, ct);
        return existing.Count == 0 ? 1 : existing.Max(v => v.VersionNumber) + 1;
    }

    private async Task<ICloudProvider?> ResolveProviderAsync(string? accountId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(accountId)) return null;
        var account = await _store.GetAccountAsync(accountId, ct);
        return account != null ? _providers.GetProvider(account.ProviderId) : null;
    }

    private static List<VersionChunk> ReadManifest(FileVersion version) =>
        JsonSerializer.Deserialize<List<VersionChunk>>(version.ChunkManifest!) ?? [];

    private static async Task TryRenameAsync(
        ICloudProvider provider, string accountId, string remoteId, string newName, CancellationToken ct)
    {
        if (!provider.Capabilities.HasFlag(ProviderCapabilities.Rename)) return;

        try { await provider.RenameAsync(accountId, remoteId, newName, ct); }
        catch (Exception ex) { ClouderLog.Debug($"Could not rename a stored version: {ex.Message}"); }
    }

    private static string VersionFileName(string fileName, int number, DateTime modified)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        return $"{stem} (v{number} {modified.ToLocalTime():yyyy-MM-dd HHmm}){ext}";
    }

    private static string PoolIdOf(string itemId)
    {
        var sep = itemId.IndexOf('|');
        return sep > 0 ? itemId[..sep] : itemId;
    }

    private static string RelativePathOf(string itemId)
    {
        var sep = itemId.IndexOf('|');
        return sep > 0 ? itemId[(sep + 1)..] : itemId;
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
