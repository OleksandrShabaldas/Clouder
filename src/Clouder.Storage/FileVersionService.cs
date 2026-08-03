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
    /// Archives the copy that is about to be replaced. Returns true if it was retained
    /// (the caller must then NOT delete it); false means the caller should delete as usual.
    /// </summary>
    public async Task<bool> TryRetainAsync(
        StoragePool pool, CloudItem oldItem, IReadOnlyList<StripePlan> oldPlans, CancellationToken ct = default)
    {
        if (!Enabled) return false;

        try
        {
            int nextNumber = await NextVersionNumberAsync(oldItem.Id, ct);
            var relativePath = RelativePathOf(oldItem.Id);

            var version = oldPlans.Count > 0
                ? await RetainStripedAsync(pool, oldItem, oldPlans, relativePath, nextNumber, ct)
                : await RetainWholeAsync(pool, oldItem, relativePath, nextNumber, ct);

            if (version == null) return false;

            await _store.AddFileVersionAsync(version, ct);
            ClouderLog.Info($"Kept version {nextNumber} of '{relativePath}' ({FormatBytes(version.Size)})");

            await PruneAsync(oldItem.Id, ct);
            return true;
        }
        catch (Exception ex)
        {
            // Never block a sync over version keeping — fall back to deleting the old copy.
            ClouderLog.Error($"Could not keep a version of '{oldItem.Name}'", ex);
            return false;
        }
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

    /// <summary>Applies the count and age limits to one file's history.</summary>
    public async Task PruneAsync(string fileId, CancellationToken ct = default)
    {
        var versions = (await _store.GetFileVersionsAsync(fileId, ct))
            .OrderByDescending(v => v.VersionNumber)
            .ToList();

        var doomed = new List<FileVersion>();

        if (MaxVersionsPerFile > 0 && versions.Count > MaxVersionsPerFile)
            doomed.AddRange(versions.Skip(MaxVersionsPerFile));

        if (RetentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
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

    /// <summary>Applies the age limit across every file. Runs on the housekeeping tick.</summary>
    public async Task<int> PruneAllAsync(CancellationToken ct = default)
    {
        if (RetentionDays <= 0) return 0;

        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        var stale = (await _store.GetAllFileVersionsAsync(ct))
            .Where(v => v.CreatedAtUtc != DateTime.MinValue && v.CreatedAtUtc < cutoff)
            .ToList();

        foreach (var version in stale)
        {
            await DeleteRemoteCopyAsync(version, ct);
            await _store.DeleteFileVersionAsync(version.VersionId, ct);
        }

        if (stale.Count > 0)
            ClouderLog.Info($"Discarded {stale.Count} version(s) older than {RetentionDays} days");

        return stale.Count;
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
