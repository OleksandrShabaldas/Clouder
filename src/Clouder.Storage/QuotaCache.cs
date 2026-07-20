using System.Collections.Concurrent;
using Clouder.Core.Models;
using Clouder.Core.Providers;

namespace Clouder.Storage;

/// <summary>
/// Short-lived cache of per-account storage quotas.
///
/// Placement decisions ask every pool member for its quota, so deciding where to put a
/// file meant one network round trip per account — for every file in a sync sweep.
/// Quotas change slowly, so a few seconds of caching removes almost all of that traffic
/// while keeping placement decisions accurate. Uploads invalidate the account they
/// touched, so free space never goes stale in a way that matters.
/// </summary>
public sealed class QuotaCache
{
    private readonly record struct Entry(StorageQuota Quota, long FetchedAtMs);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<long> _nowMs;

    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(30);

    public QuotaCache(Func<long>? nowMs = null) => _nowMs = nowMs ?? (() => Environment.TickCount64);

    public async Task<StorageQuota> GetAsync(
        ICloudProvider provider, string accountId, CancellationToken ct = default)
    {
        if (TryGet(accountId, out var cached))
            return cached;

        var quota = await provider.GetQuotaAsync(accountId, ct);
        Set(accountId, quota);
        return quota;
    }

    public bool TryGet(string accountId, out StorageQuota quota)
    {
        quota = default!;
        if (!_entries.TryGetValue(accountId, out var entry)) return false;

        if (_nowMs() - entry.FetchedAtMs > Ttl.TotalMilliseconds)
        {
            _entries.TryRemove(accountId, out _);
            return false;
        }

        quota = entry.Quota;
        return true;
    }

    public void Set(string accountId, StorageQuota quota) =>
        _entries[accountId] = new Entry(quota, _nowMs());

    /// <summary>Drops the cached quota for an account, e.g. right after an upload.</summary>
    public void Invalidate(string accountId) => _entries.TryRemove(accountId, out _);

    public void InvalidateAll() => _entries.Clear();
}
