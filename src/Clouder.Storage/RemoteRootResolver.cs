using System.Collections.Concurrent;
using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Providers;
using Clouder.Core.Storage;

namespace Clouder.Storage;

/// <summary>
/// Resolves (and creates on first use) the dedicated remote folder that a pool owns
/// on each member account: <c>Clouder/{PoolName}</c>.
///
/// Everything the pool uploads lives under this folder, and remote change detection
/// only looks inside it. Without a dedicated root, uploads would land in the account's
/// drive root and every unrelated file in the user's cloud storage would look like a
/// pool member — and get downloaded into the local pool folder.
/// </summary>
public sealed class RemoteRootResolver
{
    private const string ContainerFolderName = "Clouder";

    private readonly IMetadataStore _store;
    private readonly ConcurrentDictionary<string, string> _verified = new();

    public RemoteRootResolver(IMetadataStore store) => _store = store;

    /// <summary>
    /// Returns the remote folder id this pool uses on the member's account, creating
    /// the folder (and persisting its id on the member) the first time.
    /// </summary>
    public async Task<string> EnsureAsync(
        ICloudProvider provider, StoragePool pool, PoolMember member, CancellationToken ct = default)
    {
        var cacheKey = $"{pool.PoolId}|{member.AccountId}";
        if (_verified.TryGetValue(cacheKey, out var cached))
            return cached;

        // Trust a stored id once we've confirmed the folder still exists.
        if (!string.IsNullOrEmpty(member.RootFolderId))
        {
            try
            {
                var existing = await provider.GetItemAsync(member.AccountId, member.RootFolderId, ct);
                if (existing != null)
                {
                    _verified[cacheKey] = member.RootFolderId;
                    return member.RootFolderId;
                }
                ClouderLog.Warn($"Remote root for pool '{pool.Name}' on {member.AccountId} is gone — recreating");
            }
            catch (Exception ex)
            {
                ClouderLog.Warn($"Could not verify remote root for {member.AccountId}: {ex.Message}");
            }
        }

        var containerId = await FindOrCreateFolderAsync(provider, member.AccountId, "root", ContainerFolderName, ct);
        var poolFolderId = await FindOrCreateFolderAsync(provider, member.AccountId, containerId, Sanitize(pool.Name), ct);

        member.RootFolderId = poolFolderId;
        await _store.UpsertPoolAsync(pool, ct);

        _verified[cacheKey] = poolFolderId;
        ClouderLog.Info($"Pool '{pool.Name}' uses remote folder {ContainerFolderName}/{Sanitize(pool.Name)} on {member.AccountId}");
        return poolFolderId;
    }

    /// <summary>Forgets cached verifications (e.g. after a pool is renamed or deleted).</summary>
    public void Invalidate(string poolId)
    {
        foreach (var key in _verified.Keys.Where(k => k.StartsWith(poolId + "|", StringComparison.Ordinal)).ToList())
            _verified.TryRemove(key, out _);
    }

    private static async Task<string> FindOrCreateFolderAsync(
        ICloudProvider provider, string accountId, string parentId, string name, CancellationToken ct)
    {
        var children = await provider.ListFolderAsync(accountId, parentId, ct);
        var match = children.FirstOrDefault(c =>
            c.Type == CloudItemType.Folder && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (match != null)
            return match.RemoteId;

        var created = await provider.CreateFolderAsync(accountId, parentId, name, ct);
        return created.RemoteId;
    }

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "Pool" : cleaned;
    }
}
