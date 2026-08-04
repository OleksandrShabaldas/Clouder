using System.Text;
using Clouder.Core.Models;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

/// <summary>
/// Per-pool control over where versions live and how many survive: dedicated archive
/// accounts, spreading, splitting, and the size/age/interval limits.
/// </summary>
public class VersionPolicyTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _poolDir;
    private readonly SqliteMetadataStore _store;

    public VersionPolicyTests()
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

    private sealed record Harness(
        InMemoryCloudProvider Provider,
        PoolSyncService Sync,
        FileVersionService Versions,
        StoragePool Pool);

    private async Task<Harness> SetupAsync(int accounts, Action<StoragePool> configure)
    {
        await _store.InitializeAsync();

        var members = new List<PoolMember>();
        for (int i = 1; i <= accounts; i++)
        {
            await _store.UpsertAccountAsync(new ProviderAccount
            {
                AccountId = $"acc-{i}", ProviderId = "fake", DisplayName = $"Drive {i}",
                ConnectedAtUtc = DateTime.UtcNow
            });
            members.Add(new PoolMember
            {
                AccountId = $"acc-{i}", ProviderId = "fake", Priority = 0, IsEnabled = true
            });
        }

        var pool = new StoragePool
        {
            PoolId = "p1", Name = "Test Pool", LocalPath = _poolDir, Members = members
        };
        configure(pool);
        await _store.UpsertPoolAsync(pool);

        var provider = new InMemoryCloudProvider();
        var registry = new SingleProviderRegistry(provider);
        var roots = new RemoteRootResolver(_store);
        var versions = new FileVersionService(_store, registry, roots);
        var sync = new PoolSyncService(_store, registry, roots: roots) { Versions = versions };

        return new Harness(provider, sync, versions, pool);
    }

    private async Task WriteAndSyncAsync(Harness h, string name, string content, int minutesAhead)
    {
        var path = Path.Combine(_poolDir, name);
        await File.WriteAllTextAsync(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(minutesAhead));
        await h.Sync.SyncPoolAsync("p1");
    }

    private async Task<string> ReadVersionAsync(FileVersionService versions, string versionId)
    {
        await using var stream = await versions.OpenVersionAsync(versionId);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ── Placement ───────────────────────────────────────────────────────

    [Fact]
    public async Task DedicatedAccount_HoldsVersionsAndTakesNoLiveFiles()
    {
        var h = await SetupAsync(2, pool =>
        {
            // Drive 2 becomes a pure archive: versions only, never live files.
            pool.Members[1].IsVersionStore = true;
            pool.Members[1].ExcludeFromFilePlacement = true;
            pool.VersionPolicy.Placement = VersionPlacement.DedicatedAccounts;
        });

        await WriteAndSyncAsync(h, "doc.txt", "one", 0);
        await WriteAndSyncAsync(h, "doc.txt", "two", 5);

        // The live file went to the account that accepts files.
        var current = await _store.GetItemAsync("p1|doc.txt");
        Assert.Equal("acc-1", current!.AccountId);

        // The version went to the archive account, and is readable from there.
        var version = Assert.Single(await _store.GetFileVersionsAsync("p1|doc.txt"));
        Assert.Equal("acc-2", version.AccountId);
        Assert.Equal("one", await ReadVersionAsync(h.Versions, version.VersionId));
    }

    [Fact]
    public async Task ExcludedAccountNeverReceivesOrdinaryFiles()
    {
        var h = await SetupAsync(2, pool =>
        {
            pool.Members[0].ExcludeFromFilePlacement = true;   // only Drive 2 takes files
        });

        for (int i = 0; i < 5; i++)
            await WriteAndSyncAsync(h, $"file{i}.txt", $"content {i}", i);

        var items = await _store.GetItemsByIdPrefixAsync("p1|");
        Assert.Equal(5, items.Count);
        Assert.All(items, item => Assert.Equal("acc-2", item.AccountId));
    }

    [Fact]
    public async Task SameAccountPlacement_MovesWithoutCopying()
    {
        var h = await SetupAsync(2, pool =>
        {
            pool.VersionPolicy.Placement = VersionPlacement.SameAccount;
            pool.VersionPolicy.Striping = VersionStriping.Inherit;
        });

        await WriteAndSyncAsync(h, "doc.txt", "one", 0);
        var firstRemoteId = (await _store.GetItemAsync("p1|doc.txt"))!.RemoteId;

        await WriteAndSyncAsync(h, "doc.txt", "two", 5);

        // The very same remote object is kept as the version — proof it was moved
        // rather than re-uploaded, which is what makes this mode free.
        var version = Assert.Single(await _store.GetFileVersionsAsync("p1|doc.txt"));
        Assert.Equal(firstRemoteId, version.RemoteVersionId);
    }

    [Fact]
    public async Task AlwaysSplit_SpreadsAVersionAcrossAccounts()
    {
        var h = await SetupAsync(2, pool =>
        {
            pool.VersionPolicy.Placement = VersionPlacement.Balanced;
            pool.VersionPolicy.Striping = VersionStriping.Always;
        });

        await WriteAndSyncAsync(h, "doc.txt", "ABCDEFGH", 0);
        await WriteAndSyncAsync(h, "doc.txt", "12345678", 5);

        var version = Assert.Single(await _store.GetFileVersionsAsync("p1|doc.txt"));
        Assert.True(version.IsStriped, "the version should have been split across accounts");

        // Split or not, the old content still reads back whole.
        Assert.Equal("ABCDEFGH", await ReadVersionAsync(h.Versions, version.VersionId));
    }

    [Fact]
    public async Task NeverSplit_StoresAPreviouslySplitFileAsOnePiece()
    {
        var h = await SetupAsync(2, pool =>
        {
            pool.VersionPolicy.Placement = VersionPlacement.SameAccount;
            pool.VersionPolicy.Striping = VersionStriping.Never;
        });
        h.Sync.StripeThresholdBytes = 4;   // force the live file to be split

        await WriteAndSyncAsync(h, "doc.txt", "ABCDEFGH", 0);
        await WriteAndSyncAsync(h, "doc.txt", "12345678", 5);

        var version = Assert.Single(await _store.GetFileVersionsAsync("p1|doc.txt"));
        Assert.False(version.IsStriped, "the version should have been joined into one object");
        Assert.Equal("ABCDEFGH", await ReadVersionAsync(h.Versions, version.VersionId));
    }

    // ── Limits ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FilesOverTheSizeLimitAreNotVersioned()
    {
        var h = await SetupAsync(1, pool =>
        {
            pool.VersionPolicy.MaxVersionSizeBytes = 10;   // bytes
        });

        await WriteAndSyncAsync(h, "big.txt", new string('x', 50), 0);
        var firstRemoteId = (await _store.GetItemAsync("p1|big.txt"))!.RemoteId;

        await WriteAndSyncAsync(h, "big.txt", new string('y', 50), 5);

        Assert.Empty(await _store.GetFileVersionsAsync("p1|big.txt"));
        Assert.False(h.Provider.Exists(firstRemoteId), "the old copy should be deleted, not kept");
    }

    [Fact]
    public async Task MinimumIntervalSkipsRapidEdits()
    {
        var h = await SetupAsync(1, pool =>
        {
            pool.VersionPolicy.MinIntervalMinutes = 60;
        });

        await WriteAndSyncAsync(h, "doc.txt", "one", 0);
        await WriteAndSyncAsync(h, "doc.txt", "two", 5);
        Assert.Single(await _store.GetFileVersionsAsync("p1|doc.txt"));

        // A second edit moments later must not add another version.
        await WriteAndSyncAsync(h, "doc.txt", "three", 10);
        var versions = await _store.GetFileVersionsAsync("p1|doc.txt");
        Assert.Single(versions);
        Assert.Equal("one", await ReadVersionAsync(h.Versions, versions[0].VersionId));
    }

    [Fact]
    public async Task PerPoolCountLimitOverridesTheGlobalOne()
    {
        var h = await SetupAsync(1, pool =>
        {
            pool.VersionPolicy.MaxVersionsPerFile = 1;   // stricter than the global default
        });
        h.Versions.MaxVersionsPerFile = 10;

        await WriteAndSyncAsync(h, "doc.txt", "one", 0);
        await WriteAndSyncAsync(h, "doc.txt", "two", 5);
        await WriteAndSyncAsync(h, "doc.txt", "three", 10);

        var version = Assert.Single(await _store.GetFileVersionsAsync("p1|doc.txt"));
        Assert.Equal("two", await ReadVersionAsync(h.Versions, version.VersionId));
    }

    [Fact]
    public async Task TotalSizeCapDropsTheOldestVersions()
    {
        var h = await SetupAsync(1, pool =>
        {
            pool.VersionPolicy.MaxTotalBytes = 25;   // room for roughly two 10-byte versions
            pool.VersionPolicy.MaxVersionsPerFile = 0;
        });

        await WriteAndSyncAsync(h, "doc.txt", new string('a', 10), 0);
        await WriteAndSyncAsync(h, "doc.txt", new string('b', 10), 5);
        await WriteAndSyncAsync(h, "doc.txt", new string('c', 10), 10);
        await WriteAndSyncAsync(h, "doc.txt", new string('d', 10), 15);

        var versions = await _store.GetFileVersionsAsync("p1|doc.txt");
        Assert.True(versions.Sum(v => v.Size) <= 25,
            $"version history should stay under the cap, was {versions.Sum(v => v.Size)} bytes");

        // The survivors are the newest ones.
        Assert.DoesNotContain(versions, v => v.VersionNumber == 1);
    }

    [Fact]
    public async Task PoolCanTurnVersioningOffIndependently()
    {
        var h = await SetupAsync(1, pool =>
        {
            pool.VersionPolicy.Enabled = false;   // off for this pool only
        });
        h.Versions.Enabled = true;                 // on globally

        await WriteAndSyncAsync(h, "doc.txt", "one", 0);
        await WriteAndSyncAsync(h, "doc.txt", "two", 5);

        Assert.Empty(await _store.GetFileVersionsAsync("p1|doc.txt"));
    }

    [Fact]
    public async Task PolicySurvivesAReload()
    {
        var h = await SetupAsync(2, pool =>
        {
            pool.VersionPolicy.Placement = VersionPlacement.DedicatedAccounts;
            pool.VersionPolicy.Striping = VersionStriping.Always;
            pool.VersionPolicy.MaxTotalBytes = 1234;
            pool.VersionPolicy.MinIntervalMinutes = 7;
            pool.VersionPolicy.MaxVersionsPerFile = 3;
            pool.Members[1].IsVersionStore = true;
            pool.Members[1].ExcludeFromFilePlacement = true;
        });

        var reloaded = await _store.GetPoolAsync("p1");
        Assert.NotNull(reloaded);

        Assert.Equal(VersionPlacement.DedicatedAccounts, reloaded.VersionPolicy.Placement);
        Assert.Equal(VersionStriping.Always, reloaded.VersionPolicy.Striping);
        Assert.Equal(1234, reloaded.VersionPolicy.MaxTotalBytes);
        Assert.Equal(7, reloaded.VersionPolicy.MinIntervalMinutes);
        Assert.Equal(3, reloaded.VersionPolicy.MaxVersionsPerFile);

        var archive = reloaded.Members.First(m => m.AccountId == "acc-2");
        Assert.True(archive.IsVersionStore);
        Assert.True(archive.ExcludeFromFilePlacement);
    }
}
