using Clouder.Core.Models;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

/// <summary>
/// Freeing local disk by making synced files online-only. The critical property is
/// what it refuses to touch: anything with unsynced local changes.
/// </summary>
public class CacheEvictionTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _poolDir;
    private readonly SqliteMetadataStore _store;

    public CacheEvictionTests()
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

    private async Task SetupAsync()
    {
        await _store.InitializeAsync();
        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-1", ProviderId = "fake", DisplayName = "Fake", ConnectedAtUtc = DateTime.UtcNow
        });
        await _store.UpsertPoolAsync(new StoragePool
        {
            PoolId = "p1", Name = "Pool", LocalPath = _poolDir,
            Members = [new PoolMember { AccountId = "acc-1", ProviderId = "fake", Priority = 0, IsEnabled = true }]
        });
    }

    /// <summary>Creates a local file and tracks it, controlling its access/modify times.</summary>
    private async Task AddFileAsync(
        string name, int sizeBytes, DateTime lastAccessUtc,
        SyncState state = SyncState.Synced, bool locallyEdited = false)
    {
        var path = Path.Combine(_poolDir, name);
        await File.WriteAllBytesAsync(path, new byte[sizeBytes]);

        var syncedAt = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(path, locallyEdited ? DateTime.UtcNow : syncedAt);
        File.SetLastAccessTimeUtc(path, lastAccessUtc);

        await _store.UpsertItemAsync(new CloudItem
        {
            Id = $"p1|{name}", RemoteId = $"r-{name}", ProviderId = "fake", AccountId = "acc-1",
            Name = name, Type = CloudItemType.File, Size = sizeBytes,
            CreatedAtUtc = syncedAt, ModifiedAtUtc = syncedAt, SyncState = state
        });
    }

    [Fact]
    public async Task EvictsLeastRecentlyUsedUntilUnderTheLimit()
    {
        await SetupAsync();
        var sink = new FakePlaceholderSink { ActivePool = "p1" };
        var service = new CacheEvictionService(_store, sink) { CacheLimitBytes = 1000 };

        await AddFileAsync("oldest.bin", 600, DateTime.UtcNow.AddDays(-10));
        await AddFileAsync("middle.bin", 600, DateTime.UtcNow.AddDays(-5));
        await AddFileAsync("newest.bin", 600, DateTime.UtcNow);

        var freed = await service.RunAsync();

        // 1800 bytes local, 1000 allowed → the two least-recently-used go.
        Assert.Equal(1200, freed);
        Assert.Equal(2, sink.Freed.Count);
        Assert.Contains(sink.Freed, p => p.EndsWith("oldest.bin"));
        Assert.Contains(sink.Freed, p => p.EndsWith("middle.bin"));
        Assert.DoesNotContain(sink.Freed, p => p.EndsWith("newest.bin"));
    }

    [Fact]
    public async Task EvictsByAgeRegardlessOfSize()
    {
        await SetupAsync();
        var sink = new FakePlaceholderSink { ActivePool = "p1" };
        var service = new CacheEvictionService(_store, sink) { DehydrateAfterDays = 30 };

        await AddFileAsync("stale.bin", 100, DateTime.UtcNow.AddDays(-45));
        await AddFileAsync("recent.bin", 100, DateTime.UtcNow.AddDays(-2));

        await service.RunAsync();

        var freed = Assert.Single(sink.Freed);
        Assert.EndsWith("stale.bin", freed);
    }

    [Fact]
    public async Task NeverEvictsAFileWithUnsyncedLocalChanges()
    {
        await SetupAsync();
        var sink = new FakePlaceholderSink { ActivePool = "p1" };
        var service = new CacheEvictionService(_store, sink) { CacheLimitBytes = 1 };

        // Old enough and big enough to be a prime candidate — but edited since syncing.
        await AddFileAsync("edited.bin", 5000, DateTime.UtcNow.AddDays(-90), locallyEdited: true);

        var freed = await service.RunAsync();

        Assert.Equal(0, freed);
        Assert.Empty(sink.Freed);
    }

    [Fact]
    public async Task SkipsFilesThatAreNotSettled()
    {
        await SetupAsync();
        var sink = new FakePlaceholderSink { ActivePool = "p1" };
        var service = new CacheEvictionService(_store, sink) { CacheLimitBytes = 1 };

        await AddFileAsync("pending.bin", 5000, DateTime.UtcNow.AddDays(-90), SyncState.PendingUpload);
        await AddFileAsync("conflicted.bin", 5000, DateTime.UtcNow.AddDays(-90), SyncState.Conflict);

        await service.RunAsync();

        Assert.Empty(sink.Freed);
    }

    [Fact]
    public async Task DoesNothingWhenExplorerIntegrationIsOff()
    {
        await SetupAsync();
        var sink = new FakePlaceholderSink { ActivePool = "some-other-pool" };
        var service = new CacheEvictionService(_store, sink) { CacheLimitBytes = 1 };

        await AddFileAsync("big.bin", 5000, DateTime.UtcNow.AddDays(-90));

        Assert.Equal(0, await service.RunAsync());
        Assert.Empty(sink.Freed);
    }

    [Fact]
    public async Task DoesNothingWhenBothLimitsAreDisabled()
    {
        await SetupAsync();
        var sink = new FakePlaceholderSink { ActivePool = "p1" };
        var service = new CacheEvictionService(_store, sink); // both settings 0

        await AddFileAsync("big.bin", 5000, DateTime.UtcNow.AddDays(-90));

        Assert.Equal(0, await service.RunAsync());
        Assert.Empty(sink.Freed);
    }
}
