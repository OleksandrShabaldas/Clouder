using Clouder.Core.Models;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

public class TransferHistoryTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _poolDir;
    private readonly SqliteMetadataStore _store;

    public TransferHistoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"clouder_test_{Guid.NewGuid():N}.db");
        _poolDir = Path.Combine(Path.GetTempPath(), $"clouder_pool_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_poolDir);
        _store = new SqliteMetadataStore(_dbPath);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (Directory.Exists(_poolDir)) Directory.Delete(_poolDir, true);
    }

    private TransferRecord Record(TransferKind kind, TransferOutcome outcome, long bytes, DateTime when) => new()
    {
        TransferId = Guid.NewGuid().ToString("N"),
        PoolId = "p1",
        AccountId = "acc-1",
        FileName = "f.bin",
        Kind = kind,
        Outcome = outcome,
        Bytes = bytes,
        TimestampUtc = when
    };

    [Fact]
    public async Task StatsCountSuccessesAndFailuresSeparately()
    {
        await _store.InitializeAsync();
        var now = DateTime.UtcNow;

        await _store.AddTransferAsync(Record(TransferKind.Upload, TransferOutcome.Success, 100, now));
        await _store.AddTransferAsync(Record(TransferKind.Upload, TransferOutcome.Success, 250, now));
        await _store.AddTransferAsync(Record(TransferKind.Download, TransferOutcome.Success, 400, now));
        await _store.AddTransferAsync(Record(TransferKind.Upload, TransferOutcome.Failed, 0, now));
        // Skipped transfers count towards neither bytes nor failures.
        await _store.AddTransferAsync(Record(TransferKind.Upload, TransferOutcome.Skipped, 999, now));

        var stats = await _store.GetTransferStatsAsync(now.AddHours(-1));

        Assert.Equal(2, stats.Uploads);
        Assert.Equal(350, stats.BytesUploaded);
        Assert.Equal(1, stats.Downloads);
        Assert.Equal(400, stats.BytesDownloaded);
        Assert.Equal(1, stats.Failures);
    }

    [Fact]
    public async Task StatsRespectTheTimeWindow()
    {
        await _store.InitializeAsync();
        var now = DateTime.UtcNow;

        await _store.AddTransferAsync(Record(TransferKind.Upload, TransferOutcome.Success, 100, now));
        await _store.AddTransferAsync(Record(TransferKind.Upload, TransferOutcome.Success, 500, now.AddDays(-3)));

        var stats = await _store.GetTransferStatsAsync(now.AddDays(-1));

        Assert.Equal(1, stats.Uploads);
        Assert.Equal(100, stats.BytesUploaded);
    }

    [Fact]
    public async Task RecentTransfersAreNewestFirstAndFilterByPool()
    {
        await _store.InitializeAsync();
        var now = DateTime.UtcNow;

        var older = Record(TransferKind.Upload, TransferOutcome.Success, 1, now.AddMinutes(-10));
        older.FileName = "older.txt";
        var newer = Record(TransferKind.Upload, TransferOutcome.Success, 2, now);
        newer.FileName = "newer.txt";
        var otherPool = Record(TransferKind.Upload, TransferOutcome.Success, 3, now);
        otherPool.PoolId = "p2";

        await _store.AddTransferAsync(older);
        await _store.AddTransferAsync(newer);
        await _store.AddTransferAsync(otherPool);

        var all = await _store.GetRecentTransfersAsync();
        Assert.Equal(3, all.Count);
        Assert.Equal("newer.txt", all[0].FileName);

        var poolOnly = await _store.GetRecentTransfersAsync(poolId: "p1");
        Assert.Equal(2, poolOnly.Count);
        Assert.All(poolOnly, t => Assert.Equal("p1", t.PoolId));
    }

    [Fact]
    public async Task PruneKeepsOnlyTheMostRecent()
    {
        await _store.InitializeAsync();
        var now = DateTime.UtcNow;

        for (int i = 0; i < 20; i++)
            await _store.AddTransferAsync(Record(TransferKind.Upload, TransferOutcome.Success, i, now.AddMinutes(-i)));

        await _store.PruneTransfersAsync(keep: 5);

        var remaining = await _store.GetRecentTransfersAsync(limit: 100);
        Assert.Equal(5, remaining.Count);
        // The five most recent survive (offsets 0..4 minutes ago).
        Assert.All(remaining, t => Assert.True(t.Bytes < 5));
    }

    [Fact]
    public async Task SyncingAFileRecordsAnUpload()
    {
        await _store.InitializeAsync();
        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-1", ProviderId = "fake", DisplayName = "Fake", ConnectedAtUtc = DateTime.UtcNow
        });
        await _store.UpsertPoolAsync(new StoragePool
        {
            PoolId = "p1", Name = "Test Pool", LocalPath = _poolDir,
            Members = [new PoolMember { AccountId = "acc-1", ProviderId = "fake", Priority = 0, IsEnabled = true }]
        });

        var provider = new InMemoryCloudProvider();
        using var svc = new PoolSyncService(_store, new SingleProviderRegistry(provider));

        await File.WriteAllTextAsync(Path.Combine(_poolDir, "report.pdf"), "content here");
        await svc.SyncPoolAsync("p1");

        var transfers = await _store.GetRecentTransfersAsync();
        var upload = Assert.Single(transfers, t => t.Kind == TransferKind.Upload);
        Assert.Equal(TransferOutcome.Success, upload.Outcome);
        Assert.Equal("report.pdf", upload.FileName);
        Assert.Equal("acc-1", upload.AccountId);
        Assert.True(upload.Bytes > 0);
    }
}
