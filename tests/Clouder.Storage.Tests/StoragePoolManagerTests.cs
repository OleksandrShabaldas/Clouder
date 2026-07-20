using Clouder.Core.Models;
using Clouder.Core.Providers;
using Clouder.Core.Storage;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

public class StoragePoolManagerTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly SqliteMetadataStore _store;

    public StoragePoolManagerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"clouder_test_{Guid.NewGuid():N}.db");
        _store = new SqliteMetadataStore(_dbPath);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private StoragePoolManager CreateManager(Dictionary<string, StorageQuota> quotas)
    {
        var registry = new FakeProviderRegistry(quotas);
        return new StoragePoolManager(_store, registry);
    }

    /// <summary>
    /// Both members share fill tier 0 by default, so the placement strategy is what
    /// decides between them. Pass distinct priorities to test tier fallthrough.
    /// </summary>
    private async Task<(StoragePool Pool, Dictionary<string, StorageQuota> Quotas)> SetupTwoMemberPoolAsync(
        long member1Total, long member1Used,
        long member2Total, long member2Used,
        PlacementStrategy strategy = PlacementStrategy.FillFirst,
        int member1Priority = 0,
        int member2Priority = 0)
    {
        await _store.InitializeAsync();

        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-gd", ProviderId = "google-drive", DisplayName = "Google Drive",
            ConnectedAtUtc = DateTime.UtcNow
        });
        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-mega", ProviderId = "mega", DisplayName = "MEGA",
            ConnectedAtUtc = DateTime.UtcNow
        });

        var pool = new StoragePool
        {
            PoolId = "pool-1",
            Name = "Main",
            LocalPath = @"C:\Clouder",
            Mode = PoolMode.Auto,
            DefaultStrategy = strategy,
            Members =
            [
                new PoolMember { AccountId = "acc-gd", ProviderId = "google-drive", Priority = member1Priority, IsEnabled = true },
                new PoolMember { AccountId = "acc-mega", ProviderId = "mega", Priority = member2Priority, IsEnabled = true }
            ]
        };
        await _store.UpsertPoolAsync(pool);

        var quotas = new Dictionary<string, StorageQuota>
        {
            ["acc-gd"] = new() { TotalBytes = member1Total, UsedBytes = member1Used },
            ["acc-mega"] = new() { TotalBytes = member2Total, UsedBytes = member2Used }
        };

        return (pool, quotas);
    }

    // ── Direct placement ────────────────────────────────────────────────

    [Fact]
    public async Task DirectPlace_FillsLowerTierFirst()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(15), member1Used: GB(5),   // GD: 10GB free, tier 0
            member2Total: GB(20), member2Used: GB(5),   // MEGA: 15GB free, tier 1
            strategy: PlacementStrategy.FillFirst,
            member1Priority: 0, member2Priority: 1);

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "photo.jpg", GB(1));

        Assert.Equal(PlacementOutcome.DirectPlace, decision.Outcome);
        Assert.Equal("acc-gd", decision.TargetAccountId);
    }

    [Fact]
    public async Task HigherTierIsUsedOnlyWhenTheLowerTierCannotFit()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(15), member1Used: GB(14),  // GD: 1GB free, tier 0
            member2Total: GB(20), member2Used: GB(5),   // MEGA: 15GB free, tier 1
            strategy: PlacementStrategy.FillFirst,
            member1Priority: 0, member2Priority: 1);

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "movie.mkv", GB(5));

        // Tier 0 can't take 5GB, so placement falls through to tier 1.
        Assert.Equal(PlacementOutcome.DirectPlace, decision.Outcome);
        Assert.Equal("acc-mega", decision.TargetAccountId);
    }

    [Fact]
    public async Task StrategyDecidesWithinATier_NotAcrossTiers()
    {
        // MEGA has far more free space, but it's in a higher tier — the strategy
        // must not reach past tier 0 to get it.
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(15), member1Used: GB(5),   // GD: 10GB free, tier 0
            member2Total: GB(100), member2Used: GB(1),  // MEGA: 99GB free, tier 1
            strategy: PlacementStrategy.LargestFree,
            member1Priority: 0, member2Priority: 1);

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "doc.pdf", GB(1));

        Assert.Equal("acc-gd", decision.TargetAccountId);
    }

    [Fact]
    public async Task DirectPlace_LargestFree_PicksMostSpace()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(15), member1Used: GB(5),   // GD: 10GB free
            member2Total: GB(20), member2Used: GB(2),   // MEGA: 18GB free
            strategy: PlacementStrategy.LargestFree);

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "video.mp4", GB(1));

        Assert.Equal(PlacementOutcome.DirectPlace, decision.Outcome);
        Assert.Equal("acc-mega", decision.TargetAccountId);
    }

    [Fact]
    public async Task DirectPlace_RoundRobin_PicksLowestUsageRatio()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(10), member1Used: GB(8),   // GD: 80% used
            member2Total: GB(10), member2Used: GB(3),   // MEGA: 30% used
            strategy: PlacementStrategy.RoundRobin);

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "doc.pdf", GB(1));

        Assert.Equal(PlacementOutcome.DirectPlace, decision.Outcome);
        Assert.Equal("acc-mega", decision.TargetAccountId);
    }

    // ── Insufficient space ──────────────────────────────────────────────

    [Fact]
    public async Task InsufficientSpace_WhenTotalFreeNotEnough()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(10), member1Used: GB(9),   // 1GB free
            member2Total: GB(10), member2Used: GB(9));   // 1GB free

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "huge.zip", GB(5));

        Assert.Equal(PlacementOutcome.InsufficientSpace, decision.Outcome);
    }

    [Fact]
    public async Task InsufficientSpace_WhenNoEnabledMembers()
    {
        await _store.InitializeAsync();

        var pool = new StoragePool
        {
            PoolId = "pool-empty",
            Name = "Empty",
            LocalPath = @"C:\Empty",
            Members = []
        };
        await _store.UpsertPoolAsync(pool);

        var manager = CreateManager(new Dictionary<string, StorageQuota>());
        var decision = await manager.DecidePlacementAsync("pool-empty", "file.txt", 1024);

        Assert.Equal(PlacementOutcome.InsufficientSpace, decision.Outcome);
    }

    // ── Striping ────────────────────────────────────────────────────────

    [Fact]
    public async Task Striping_WhenFileLargerThanAnyMemberTotal()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(4), member1Used: GB(1),    // 3GB free, 4GB total
            member2Total: GB(4), member2Used: GB(1));    // 3GB free, 4GB total
        // File is 5GB — can't fit on any single member even if empty

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "bigfile.iso", GB(5));

        Assert.Equal(PlacementOutcome.StripingRequired, decision.Outcome);
        Assert.NotNull(decision.StripePlans);
        Assert.True(decision.StripePlans.Count >= 2);
        Assert.Equal(GB(5), decision.StripePlans.Sum(s => s.Length));
    }

    [Fact]
    public async Task Striping_ChunksAssignedByFreeSpace()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(3), member1Used: GB(1),    // 2GB free
            member2Total: GB(4), member2Used: GB(1));    // 3GB free

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "big.bin", GB(5));

        Assert.Equal(PlacementOutcome.StripingRequired, decision.Outcome);
        var plans = decision.StripePlans!;
        Assert.Equal(2, plans.Count);
        // Largest free first
        Assert.Equal("acc-mega", plans[0].AccountId);
        Assert.Equal(GB(3), plans[0].Length);
        Assert.Equal("acc-gd", plans[1].AccountId);
        Assert.Equal(GB(2), plans[1].Length);
    }

    // ── Reorganization ──────────────────────────────────────────────────

    [Fact]
    public async Task Reorg_WhenShufflingCanFreeSpace()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(10), member1Used: GB(7),   // GD: 3GB free
            member2Total: GB(10), member2Used: GB(7));   // MEGA: 3GB free
        // Want to place a 5GB file. Neither has 5GB free, total is 6GB free.
        // Move a 2GB file from GD to MEGA → GD gets 5GB free.

        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "file-a", RemoteId = "ra", ProviderId = "google-drive", AccountId = "acc-gd",
            Name = "a.dat", Type = CloudItemType.File, Size = GB(2),
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });
        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "file-b", RemoteId = "rb", ProviderId = "google-drive", AccountId = "acc-gd",
            Name = "b.dat", Type = CloudItemType.File, Size = GB(1),
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "needed.zip", GB(5));

        Assert.Equal(PlacementOutcome.ReorgRequired, decision.Outcome);
        Assert.NotNull(decision.ReorgPlan);
        Assert.True(decision.ReorgPlan.Moves.Count > 0);
        Assert.True(decision.ReorgPlan.FreedBytes >= GB(2));
        Assert.All(decision.ReorgPlan.Moves, m =>
        {
            Assert.Equal("acc-gd", m.FromAccountId);
            Assert.Equal("acc-mega", m.ToAccountId);
        });
    }

    [Fact]
    public async Task Reorg_PlanDirectly_FreesRequestedSpace()
    {
        // Neither member can take 6GB as-is, so a reorg is genuinely needed:
        // moving GD's 3GB file to MEGA gets GD to 6GB free.
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(10), member1Used: GB(7),   // GD: 3GB free
            member2Total: GB(10), member2Used: GB(6));   // MEGA: 4GB free

        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "movable", RemoteId = "rm", ProviderId = "google-drive", AccountId = "acc-gd",
            Name = "movable.dat", Type = CloudItemType.File, Size = GB(3),
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });

        var manager = CreateManager(quotas);
        var plan = await manager.PlanReorganizationAsync("pool-1", GB(6));

        Assert.True(plan.Moves.Count > 0);
        Assert.True(plan.FreedBytes >= GB(3));
    }

    [Fact]
    public async Task Reorg_NotNeededWhenAMemberAlreadyHasRoom()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(10), member1Used: GB(9),   // 1GB free
            member2Total: GB(10), member2Used: GB(3));   // 7GB free — already fits

        var manager = CreateManager(quotas);
        var plan = await manager.PlanReorganizationAsync("pool-1", GB(5));

        Assert.Empty(plan.Moves);
    }

    // ── Per-member limits ───────────────────────────────────────────────

    [Fact]
    public async Task ReserveKeepsSpaceFreeOnTheAccount()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(15), member1Used: GB(10),  // 5GB free
            member2Total: GB(15), member2Used: GB(10),  // 5GB free
            strategy: PlacementStrategy.LargestFree);

        // Keep 4GB free on GD for things that aren't the pool's business.
        pool.Members.First(m => m.AccountId == "acc-gd").ReserveBytes = GB(4);
        await _store.UpsertPoolAsync(pool);

        var manager = CreateManager(quotas);

        // 3GB won't fit in GD's remaining 1GB allowance, so it goes to MEGA.
        var decision = await manager.DecidePlacementAsync("pool-1", "big.bin", GB(3));
        Assert.Equal("acc-mega", decision.TargetAccountId);
    }

    [Fact]
    public async Task MaxUsageCapsWhatThePoolStoresOnAnAccount()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(15), member1Used: GB(1),   // plenty of raw space
            member2Total: GB(15), member2Used: GB(10),
            strategy: PlacementStrategy.LargestFree);

        // This pool may only ever use 5GB of GD, and 4GB of that is already spent.
        pool.Members.First(m => m.AccountId == "acc-gd").MaxUsageBytes = GB(5);
        await _store.UpsertPoolAsync(pool);

        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "pool-1|existing.dat", RemoteId = "r1", ProviderId = "google-drive", AccountId = "acc-gd",
            Name = "existing.dat", Type = CloudItemType.File, Size = GB(4),
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });

        var manager = CreateManager(quotas);

        // Only 1GB of allowance left on GD despite 14GB actually being free there.
        var decision = await manager.DecidePlacementAsync("pool-1", "big.bin", GB(3));
        Assert.Equal("acc-mega", decision.TargetAccountId);
    }

    [Fact]
    public async Task RemainingAllowanceIsStillUsable()
    {
        // FillFirst packs the fullest member that still fits, so the capped account
        // is chosen — proving the leftover allowance is spendable, not written off.
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(15), member1Used: GB(1),
            member2Total: GB(15), member2Used: GB(5),
            strategy: PlacementStrategy.FillFirst);

        pool.Members.First(m => m.AccountId == "acc-gd").MaxUsageBytes = GB(5);
        await _store.UpsertPoolAsync(pool);

        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "pool-1|existing.dat", RemoteId = "r1", ProviderId = "google-drive", AccountId = "acc-gd",
            Name = "existing.dat", Type = CloudItemType.File, Size = GB(4),
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });

        var manager = CreateManager(quotas);

        // GD: 1GB of allowance left. MEGA: 10GB free. FillFirst takes the tighter fit.
        var decision = await manager.DecidePlacementAsync("pool-1", "small.bin", GB(1));
        Assert.Equal("acc-gd", decision.TargetAccountId);
    }

    [Fact]
    public async Task UsageCapCountsOnlyThisPoolsFiles()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(15), member1Used: GB(1),
            member2Total: GB(15), member2Used: GB(14),
            strategy: PlacementStrategy.LargestFree);

        pool.Members.First(m => m.AccountId == "acc-gd").MaxUsageBytes = GB(5);
        await _store.UpsertPoolAsync(pool);

        // A file belonging to a DIFFERENT pool on the same account must not count
        // against this pool's allowance.
        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "other-pool|huge.dat", RemoteId = "r9", ProviderId = "google-drive", AccountId = "acc-gd",
            Name = "huge.dat", Type = CloudItemType.File, Size = GB(4),
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "file.bin", GB(4));

        Assert.Equal("acc-gd", decision.TargetAccountId);
    }

    // ── Pool status ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetPoolStatus_ReturnsAggregated()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(15), member1Used: GB(5),
            member2Total: GB(20), member2Used: GB(10));

        var manager = CreateManager(quotas);
        var status = await manager.GetPoolStatusAsync("pool-1");

        Assert.Equal("pool-1", status.PoolId);
        Assert.Equal(GB(35), status.TotalBytes);
        Assert.Equal(GB(15), status.UsedBytes);
        Assert.Equal(GB(20), status.FreeBytes);
        Assert.Equal(2, status.Members.Count);
    }

    // ── File rule integration ───────────────────────────────────────────

    [Fact]
    public async Task Rules_ExcludePattern_ReturnsExcluded()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(15), member1Used: GB(5),
            member2Total: GB(15), member2Used: GB(5));

        await _store.UpsertFileRuleAsync(new Clouder.Core.Models.FileRule
        {
            RuleId = "exclude-tmp", Name = "Exclude temp files",
            Type = Clouder.Core.Models.FileRuleType.ExcludePattern,
            Pattern = "*.tmp, *.bak",
            Action = Clouder.Core.Models.FileRuleAction.Exclude,
            Priority = 100, IsEnabled = true
        });

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "cache.tmp", 1024);

        Assert.Equal(PlacementOutcome.Excluded, decision.Outcome);
    }

    [Fact]
    public async Task Rules_RouteToAccount_OverridesStrategy()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(15), member1Used: GB(5),   // GD: 10GB free, priority 1
            member2Total: GB(15), member2Used: GB(5),   // MEGA: 10GB free, priority 2
            strategy: PlacementStrategy.FillFirst);

        // FillFirst would pick GD (priority 1), but the rule routes .mp4 to MEGA
        await _store.UpsertFileRuleAsync(new Clouder.Core.Models.FileRule
        {
            RuleId = "video-to-mega", Name = "Videos to MEGA",
            Type = Clouder.Core.Models.FileRuleType.FileExtension,
            Pattern = "*.mp4, *.mkv",
            Action = Clouder.Core.Models.FileRuleAction.RouteToAccount,
            TargetAccountId = "acc-mega",
            Priority = 50, IsEnabled = true
        });

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "movie.mp4", GB(1));

        Assert.Equal(PlacementOutcome.DirectPlace, decision.Outcome);
        Assert.Equal("acc-mega", decision.TargetAccountId);
    }

    [Fact]
    public async Task Rules_DisabledRule_FallsThrough()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(15), member1Used: GB(5),
            member2Total: GB(15), member2Used: GB(5),
            strategy: PlacementStrategy.FillFirst);

        await _store.UpsertFileRuleAsync(new Clouder.Core.Models.FileRule
        {
            RuleId = "disabled-rule", Name = "Disabled",
            Type = Clouder.Core.Models.FileRuleType.ExcludePattern,
            Pattern = "*.mp4",
            Action = Clouder.Core.Models.FileRuleAction.Exclude,
            Priority = 100, IsEnabled = false
        });

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "movie.mp4", GB(1));

        Assert.Equal(PlacementOutcome.DirectPlace, decision.Outcome);
    }

    [Fact]
    public async Task Rules_RouteToFullAccount_FallsThrough()
    {
        var (pool, quotas) = await SetupTwoMemberPoolAsync(
            member1Total: GB(15), member1Used: GB(5),   // GD: 10GB free
            member2Total: GB(15), member2Used: GB(14),  // MEGA: 1GB free
            strategy: PlacementStrategy.FillFirst);

        // Rule routes to MEGA, but MEGA only has 1GB free and file is 5GB
        await _store.UpsertFileRuleAsync(new Clouder.Core.Models.FileRule
        {
            RuleId = "to-mega", Name = "To MEGA",
            Type = Clouder.Core.Models.FileRuleType.FileExtension,
            Pattern = "*.dat",
            Action = Clouder.Core.Models.FileRuleAction.RouteToAccount,
            TargetAccountId = "acc-mega",
            Priority = 50, IsEnabled = true
        });

        var manager = CreateManager(quotas);
        var decision = await manager.DecidePlacementAsync("pool-1", "big.dat", GB(5));

        // Should fall through to default strategy (GD has space)
        Assert.Equal(PlacementOutcome.DirectPlace, decision.Outcome);
        Assert.Equal("acc-gd", decision.TargetAccountId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static long GB(long gb) => gb * 1024L * 1024L * 1024L;
}

// ── Test fakes ──────────────────────────────────────────────────────────

file class FakeProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, ICloudProvider> _providers = new();
    private readonly Dictionary<string, StorageQuota> _quotas;

    public FakeProviderRegistry(Dictionary<string, StorageQuota> quotas)
    {
        _quotas = quotas;
        Register(new FakeCloudProvider("google-drive", "Google Drive", quotas));
        Register(new FakeCloudProvider("mega", "MEGA", quotas));
    }

    public ICloudProvider? GetProvider(string providerId) =>
        _providers.GetValueOrDefault(providerId);

    public IReadOnlyList<ICloudProvider> GetAllProviders() =>
        _providers.Values.ToList();

    public void Register(ICloudProvider provider) =>
        _providers[provider.ProviderId] = provider;
}

file class FakeCloudProvider : ICloudProvider
{
    private readonly Dictionary<string, StorageQuota> _quotas;

    public FakeCloudProvider(string id, string name, Dictionary<string, StorageQuota> quotas)
    {
        ProviderId = id;
        DisplayName = name;
        _quotas = quotas;
    }

    public string ProviderId { get; }
    public string DisplayName { get; }
    public ProviderCapabilities Capabilities => ProviderCapabilities.Full;

    public Task<StorageQuota> GetQuotaAsync(string accountId, CancellationToken ct = default) =>
        Task.FromResult(_quotas[accountId]);

    public Task<ProviderAccount> ConnectAccountAsync(CancellationToken ct) => throw new NotImplementedException();
    public Task DisconnectAccountAsync(string accountId, CancellationToken ct) => throw new NotImplementedException();
    public Task<CloudItem?> GetItemAsync(string accountId, string remoteId, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<CloudItem>> ListFolderAsync(string accountId, string remoteFolderId, CancellationToken ct) => throw new NotImplementedException();
    public Task<Stream> DownloadAsync(string accountId, string remoteId, CancellationToken ct) => throw new NotImplementedException();
    public Task<Stream> DownloadRangeAsync(string accountId, string remoteId, long offset, long length, CancellationToken ct) => throw new NotImplementedException();
    public Task<CloudItem> UploadAsync(string accountId, string remoteFolderId, string fileName, Stream content, CancellationToken ct) => throw new NotImplementedException();
    public Task DeleteAsync(string accountId, string remoteId, CancellationToken ct) => throw new NotImplementedException();
    public Task<CloudItem> CreateFolderAsync(string accountId, string parentRemoteId, string name, CancellationToken ct) => throw new NotImplementedException();
    public Task<CloudItem> MoveAsync(string accountId, string remoteId, string newParentRemoteId, CancellationToken ct) => throw new NotImplementedException();
    public Task<CloudItem> RenameAsync(string accountId, string remoteId, string newName, CancellationToken ct) => throw new NotImplementedException();
    public Task<IReadOnlyList<FileVersion>> GetVersionsAsync(string accountId, string remoteId, CancellationToken ct) => throw new NotImplementedException();
    public Task<Stream> DownloadVersionAsync(string accountId, string remoteId, string versionId, CancellationToken ct) => throw new NotImplementedException();
    public Task<RemoteChangeSet> GetChangesAsync(string accountId, string rootFolderId, string? cursor, CancellationToken ct) =>
        Task.FromResult(new RemoteChangeSet());
}
