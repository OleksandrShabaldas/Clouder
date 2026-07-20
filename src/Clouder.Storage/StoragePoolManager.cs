using Clouder.Core.Models;
using Clouder.Core.Providers;
using Clouder.Core.Storage;

namespace Clouder.Storage;

public sealed class StoragePoolManager : IStoragePoolManager
{
    private readonly IMetadataStore _store;
    private readonly IProviderRegistry _providers;

    /// <summary>
    /// Quota lookups are cached because every placement decision queries every member;
    /// without this, syncing N files costs N × members network round trips.
    /// </summary>
    public QuotaCache Quotas { get; }

    public StoragePoolManager(IMetadataStore store, IProviderRegistry providers, QuotaCache? quotas = null)
    {
        _store = store;
        _providers = providers;
        Quotas = quotas ?? new QuotaCache();
    }

    public async Task<PlacementDecision> DecidePlacementAsync(
        string poolId, string fileName, long fileSize,
        string? folderPath = null, CancellationToken ct = default)
    {
        var (pool, members, quotas, effectiveFree) = await LoadPoolStateAsync(poolId, ct);

        if (members.Count == 0)
            return new PlacementDecision { Outcome = PlacementOutcome.InsufficientSpace };

        // ── Evaluate file rules ─────────────────────────────────────
        var rules = await _store.GetFileRulesAsync(poolId, ct);
        var matchedRule = FileRuleEvaluator.FindMatch(rules, fileName, fileSize, folderPath);

        if (matchedRule != null)
        {
            switch (matchedRule.Action)
            {
                case FileRuleAction.Exclude:
                    return new PlacementDecision { Outcome = PlacementOutcome.Excluded };

                case FileRuleAction.RouteToAccount when matchedRule.TargetAccountId != null:
                    if (effectiveFree.TryGetValue(matchedRule.TargetAccountId, out var targetFree)
                        && targetFree >= fileSize)
                        return new PlacementDecision
                        {
                            Outcome = PlacementOutcome.DirectPlace,
                            TargetAccountId = matchedRule.TargetAccountId
                        };
                    break; // Target full — fall through to default logic

                case FileRuleAction.RouteToProvider when matchedRule.TargetProviderId != null:
                    var providerTarget = members
                        .Where(m => m.ProviderId == matchedRule.TargetProviderId
                                    && effectiveFree[m.AccountId] >= fileSize)
                        .OrderByDescending(m => effectiveFree[m.AccountId])
                        .FirstOrDefault();
                    if (providerTarget != null)
                        return new PlacementDecision
                        {
                            Outcome = PlacementOutcome.DirectPlace,
                            TargetAccountId = providerTarget.AccountId
                        };
                    break; // No provider account with space — fall through

                case FileRuleAction.UseStrategy when matchedRule.OverrideStrategy.HasValue:
                    var overrideTarget = SelectTarget(matchedRule.OverrideStrategy.Value, members, effectiveFree, quotas, fileSize);
                    if (overrideTarget != null)
                        return new PlacementDecision
                        {
                            Outcome = PlacementOutcome.DirectPlace,
                            TargetAccountId = overrideTarget
                        };
                    break; // Strategy couldn't place — fall through
            }
        }

        // ── Default placement logic ─────────────────────────────────
        var targetAccountId = SelectTarget(pool.DefaultStrategy, members, effectiveFree, quotas, fileSize);
        if (targetAccountId != null)
            return new PlacementDecision { Outcome = PlacementOutcome.DirectPlace, TargetAccountId = targetAccountId };

        long totalFree = effectiveFree.Values.Sum();
        if (totalFree < fileSize)
            return new PlacementDecision { Outcome = PlacementOutcome.InsufficientSpace };

        long maxCapacity = quotas.Values.Max(q => q.TotalBytes);
        if (maxCapacity < fileSize)
        {
            return new PlacementDecision
            {
                Outcome = PlacementOutcome.StripingRequired,
                StripePlans = BuildStripePlan(members, effectiveFree, fileSize)
            };
        }

        var reorgPlan = await BuildReorgPlanAsync(pool, members, effectiveFree, fileSize, ct);
        if (reorgPlan.Moves.Count > 0)
            return new PlacementDecision { Outcome = PlacementOutcome.ReorgRequired, ReorgPlan = reorgPlan };

        return new PlacementDecision
        {
            Outcome = PlacementOutcome.StripingRequired,
            StripePlans = BuildStripePlan(members, effectiveFree, fileSize)
        };
    }

    public async Task<ReorganizationPlan> PlanReorganizationAsync(
        string poolId, long requiredFreeBytes, CancellationToken ct = default)
    {
        var (pool, members, quotas, effectiveFree) = await LoadPoolStateAsync(poolId, ct);
        return await BuildReorgPlanAsync(pool, members, effectiveFree, requiredFreeBytes, ct);
    }

    public async Task ExecuteReorganizationAsync(
        ReorganizationPlan plan, IProgress<ReorgProgress>? progress = null, CancellationToken ct = default)
    {
        long totalBytes = 0;
        foreach (var m in plan.Moves)
        {
            var it = await _store.GetItemAsync(m.FileId, ct);
            totalBytes += it?.Size ?? 0;
        }

        long transferred = 0;
        int failures = 0;

        for (int i = 0; i < plan.Moves.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var move = plan.Moves[i];

            try
            {
                var item = await _store.GetItemAsync(move.FileId, ct)
                    ?? throw new InvalidOperationException($"Item '{move.FileId}' not found during reorg");

                var sourceProvider = _providers.GetProvider(item.ProviderId)
                    ?? throw new InvalidOperationException($"Provider '{item.ProviderId}' not found");

                var destAccount = await _store.GetAccountAsync(move.ToAccountId, ct)
                    ?? throw new InvalidOperationException($"Account '{move.ToAccountId}' not found");
                var destProvider = _providers.GetProvider(destAccount.ProviderId)
                    ?? throw new InvalidOperationException($"Provider '{destAccount.ProviderId}' not found");

                // The item row is mutated below, so capture the source location first.
                var sourceRemoteId = item.RemoteId;

                await using (var stream = await sourceProvider.DownloadAsync(move.FromAccountId, sourceRemoteId, ct))
                {
                    var parentRemoteId = await ResolveDestinationFolderAsync(destProvider, move.ToAccountId, item, ct);
                    var uploaded = await destProvider.UploadAsync(move.ToAccountId, parentRemoteId, item.Name, stream, ct);

                    item.RemoteId = uploaded.RemoteId;
                    item.AccountId = move.ToAccountId;
                    item.ProviderId = destAccount.ProviderId;
                    await _store.UpsertItemAsync(item, ct);
                }

                // Delete the source copy last, so any failure above leaves a
                // duplicate (harmless) rather than a missing file.
                try
                {
                    await sourceProvider.DeleteAsync(move.FromAccountId, sourceRemoteId, ct);
                }
                catch (Exception ex)
                {
                    Clouder.Core.Logging.ClouderLog.Warn(
                        $"Reorg: moved '{item.Name}' but could not delete the source copy: {ex.Message}");
                }

                // Both accounts just changed size.
                Quotas.Invalidate(move.FromAccountId);
                Quotas.Invalidate(move.ToAccountId);

                transferred += item.Size;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures++;
                Clouder.Core.Logging.ClouderLog.Error($"Reorg move failed for '{move.FileId}'", ex);
            }

            progress?.Report(new ReorgProgress
            {
                TotalMoves = plan.Moves.Count,
                CompletedMoves = i + 1,
                TotalBytes = totalBytes,
                TransferredBytes = transferred
            });
        }

        if (failures > 0)
            Clouder.Core.Logging.ClouderLog.Warn(
                $"Reorganization finished with {failures} failed move(s) out of {plan.Moves.Count}");
    }

    /// <summary>
    /// Builds a stripe plan distributing <paramref name="fileSize"/> bytes across the pool's
    /// connected members (largest free space first). Used to force-split a file.
    /// </summary>
    public async Task<List<StripePlan>> BuildStripePlanForAsync(
        string poolId, long fileSize, CancellationToken ct = default)
    {
        var (_, members, quotas, effectiveFree) = await LoadPoolStateAsync(poolId, ct);
        if (members.Count == 0) return [];
        return BuildStripePlan(members, effectiveFree, fileSize);
    }

    public async Task<PoolStatus> GetPoolStatusAsync(string poolId, CancellationToken ct = default)
    {
        var (pool, members, quotas, effectiveFree) = await LoadPoolStateAsync(poolId, ct);

        var memberStatuses = members.Select(m =>
        {
            var q = quotas[m.AccountId];
            return new MemberStatus
            {
                AccountId = m.AccountId,
                ProviderId = m.ProviderId,
                TotalBytes = q.TotalBytes,
                UsedBytes = q.UsedBytes,
                IsOnline = true
            };
        }).ToList();

        return new PoolStatus
        {
            PoolId = poolId,
            TotalBytes = memberStatuses.Sum(m => m.TotalBytes),
            UsedBytes = memberStatuses.Sum(m => m.UsedBytes),
            Members = memberStatuses
        };
    }

    // ── Placement strategies ────────────────────────────────────────────

    /// <summary>
    /// Chooses where a file goes, honouring fill tiers.
    ///
    /// Members are grouped by <see cref="PoolMember.Priority"/>: the lowest tier that
    /// can take the file wins, and the strategy only decides *which* member within
    /// that tier. So tier 0 = {A, B, C} and tier 1 = {D} spreads files across A/B/C
    /// by the strategy and doesn't touch D until all three are full.
    /// </summary>
    private static string? SelectTarget(
        PlacementStrategy strategy,
        List<PoolMember> members,
        Dictionary<string, long> effectiveFree,
        Dictionary<string, StorageQuota> quotas,
        long fileSize)
    {
        var tiers = members
            .GroupBy(m => m.Priority)
            .OrderBy(g => g.Key);

        foreach (var tier in tiers)
        {
            var eligible = tier.Where(m => effectiveFree[m.AccountId] >= fileSize).ToList();
            if (eligible.Count == 0) continue; // tier exhausted — fall to the next one

            return SelectWithinTier(strategy, eligible, effectiveFree, quotas);
        }

        return null;
    }

    private static string SelectWithinTier(
        PlacementStrategy strategy,
        List<PoolMember> eligible,
        Dictionary<string, long> effectiveFree,
        Dictionary<string, StorageQuota> quotas) =>
        strategy switch
        {
            // Pack one member at a time: the fullest that still fits goes first, so
            // space is consolidated rather than smeared across the tier.
            PlacementStrategy.FillFirst => eligible
                .OrderBy(m => effectiveFree[m.AccountId])
                .ThenBy(m => m.AccountId, StringComparer.Ordinal)
                .First().AccountId,

            PlacementStrategy.LargestFree => eligible
                .OrderByDescending(m => effectiveFree[m.AccountId])
                .ThenBy(m => m.AccountId, StringComparer.Ordinal)
                .First().AccountId,

            PlacementStrategy.RoundRobin => SelectRoundRobin(eligible, quotas),

            // Custom: rules already had their say; spread evenly as the fallback.
            _ => SelectRoundRobin(eligible, quotas)
        };

    private static string SelectRoundRobin(List<PoolMember> eligible, Dictionary<string, StorageQuota> quotas)
    {
        // Balance by usage percentage — pick the member with the lowest usage ratio
        return eligible
            .OrderBy(m =>
            {
                var q = quotas[m.AccountId];
                return q.TotalBytes == 0 ? 1.0 : (double)q.UsedBytes / q.TotalBytes;
            })
            .ThenBy(m => m.AccountId, StringComparer.Ordinal)
            .First().AccountId;
    }

    // ── Reorganization planner ──────────────────────────────────────────

    private async Task<ReorganizationPlan> BuildReorgPlanAsync(
        StoragePool pool,
        List<PoolMember> members,
        Dictionary<string, long> effectiveFree,
        long requiredFreeBytes,
        CancellationToken ct)
    {
        var plan = new ReorganizationPlan { PoolId = pool.PoolId };

        // Try each member as a potential target, largest capacity first
        var candidates = members
            .OrderByDescending(m => effectiveFree[m.AccountId]);

        foreach (var target in candidates)
        {
            long needToFree = requiredFreeBytes - effectiveFree[target.AccountId];

            if (needToFree <= 0)
            {
                // Already has enough space — shouldn't reach here from DecidePlacement,
                // but handle it for direct PlanReorganization calls
                return plan;
            }

            var moves = new List<FileMove>();
            long freed = 0;

            // Clone available free space for other members to track during planning
            var availableFree = members
                .Where(m => m.AccountId != target.AccountId)
                .ToDictionary(m => m.AccountId, m => effectiveFree[m.AccountId]);

            var itemsOnTarget = await _store.GetItemsByAccountAsync(target.AccountId, ct);
            var movableFiles = itemsOnTarget
                .Where(i => i.Type == CloudItemType.File)
                .OrderByDescending(i => i.Size);

            foreach (var file in movableFiles)
            {
                if (freed >= needToFree)
                    break;

                // Find a destination with room for this file
                var dest = availableFree
                    .Where(kv => kv.Value >= file.Size)
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .FirstOrDefault();

                if (dest == null)
                    continue;

                moves.Add(new FileMove
                {
                    FileId = file.Id,
                    FromAccountId = target.AccountId,
                    ToAccountId = dest
                });

                availableFree[dest] -= file.Size;
                freed += file.Size;
            }

            if (freed >= needToFree)
            {
                plan.Moves = moves;
                plan.FreedBytes = freed;
                return plan;
            }
        }

        return plan;
    }

    // ── Striping planner ────────────────────────────────────────────────

    private static List<StripePlan> BuildStripePlan(
        List<PoolMember> members,
        Dictionary<string, long> effectiveFree,
        long fileSize)
    {
        var plans = new List<StripePlan>();
        long remaining = fileSize;
        long offset = 0;
        int chunkIndex = 0;

        var sorted = members
            .OrderByDescending(m => effectiveFree[m.AccountId]);

        foreach (var member in sorted)
        {
            if (remaining <= 0)
                break;

            long chunkSize = Math.Min(effectiveFree[member.AccountId], remaining);
            if (chunkSize <= 0)
                continue;

            plans.Add(new StripePlan
            {
                AccountId = member.AccountId,
                ChunkIndex = chunkIndex++,
                Offset = offset,
                Length = chunkSize
            });

            offset += chunkSize;
            remaining -= chunkSize;
        }

        return plans;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// How much of an account this pool may still use: whatever the account has free,
    /// less the member's reserve, and never more than its remaining usage allowance.
    /// </summary>
    private async Task<long> ComputeEffectiveFreeAsync(
        string poolId, PoolMember member, StorageQuota quota, CancellationToken ct)
    {
        long free = quota.FreeBytes - member.ReserveBytes;

        if (member.MaxUsageBytes > 0)
        {
            long alreadyUsed = await _store.GetPoolUsageOnAccountAsync(poolId, member.AccountId, ct);
            free = Math.Min(free, member.MaxUsageBytes - alreadyUsed);
        }

        return Math.Max(0, free);
    }

    private async Task<(StoragePool Pool, List<PoolMember> Members,
                        Dictionary<string, StorageQuota> Quotas, Dictionary<string, long> EffectiveFree)>
        LoadPoolStateAsync(string poolId, CancellationToken ct)
    {
        var pool = await _store.GetPoolAsync(poolId, ct)
            ?? throw new InvalidOperationException($"Pool '{poolId}' not found");

        var enabled = pool.Members.Where(m => m.IsEnabled).ToList();
        var members = new List<PoolMember>();
        var quotas = new Dictionary<string, StorageQuota>();
        var effectiveFree = new Dictionary<string, long>();

        // Only include members whose provider is connected and reachable.
        // A disconnected account is skipped rather than aborting the whole operation.
        foreach (var member in enabled)
        {
            var provider = _providers.GetProvider(member.ProviderId);
            if (provider == null)
            {
                Clouder.Core.Logging.ClouderLog.Warn(
                    $"Skipping pool member {member.AccountId}: provider '{member.ProviderId}' not connected");
                continue;
            }

            try
            {
                var quota = await Quotas.GetAsync(provider, member.AccountId, ct);
                quotas[member.AccountId] = quota;
                effectiveFree[member.AccountId] = await ComputeEffectiveFreeAsync(poolId, member, quota, ct);
                members.Add(member);
            }
            catch (Exception ex)
            {
                Clouder.Core.Logging.ClouderLog.Warn(
                    $"Skipping pool member {member.AccountId}: quota unavailable ({ex.Message})");
            }
        }

        return (pool, members, quotas, effectiveFree);
    }

    private Task<string> ResolveDestinationFolderAsync(
        ICloudProvider destProvider, string accountId, CloudItem item, CancellationToken ct)
    {
        // For now, upload to root. In the future, mirror the folder structure.
        return Task.FromResult("root");
    }
}
