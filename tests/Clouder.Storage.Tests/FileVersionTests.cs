using System.Text;
using Clouder.Core.Models;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

/// <summary>
/// Keeping previous copies when a file is replaced. The properties that matter: the old
/// content is genuinely recoverable, retained copies live outside the pool's sync root
/// so they never sync back as new files, and nothing is orphaned when a file is deleted.
/// </summary>
public class FileVersionTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _poolDir;
    private readonly SqliteMetadataStore _store;

    public FileVersionTests()
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
        FileVersionService Versions);

    private async Task<Harness> SetupAsync(bool versioning = true, int maxVersions = 5, int accounts = 1)
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

        await _store.UpsertPoolAsync(new StoragePool
        {
            PoolId = "p1", Name = "Test Pool", LocalPath = _poolDir, Members = members
        });

        var provider = new InMemoryCloudProvider();
        var registry = new SingleProviderRegistry(provider);
        var roots = new RemoteRootResolver(_store);
        var versions = new FileVersionService(_store, registry, roots)
        {
            Enabled = versioning,
            MaxVersionsPerFile = maxVersions
        };
        var sync = new PoolSyncService(_store, registry, roots: roots) { Versions = versions };

        return new Harness(provider, sync, versions);
    }

    /// <summary>Writes the file with a timestamp newer than the tracked copy, then syncs.</summary>
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

    // ── Retaining ───────────────────────────────────────────────────────

    [Fact]
    public async Task ReplacingAFile_KeepsThePreviousCopy()
    {
        var h = await SetupAsync();

        await WriteAndSyncAsync(h, "doc.txt", "version one", 0);
        await WriteAndSyncAsync(h, "doc.txt", "version two", 5);

        var versions = await _store.GetFileVersionsAsync("p1|doc.txt");
        var kept = Assert.Single(versions);
        Assert.Equal(1, kept.VersionNumber);

        // The old content is genuinely recoverable, not just recorded.
        Assert.Equal("version one", await ReadVersionAsync(h.Versions, kept.VersionId));

        // …and the current file is the new one.
        var current = await _store.GetItemAsync("p1|doc.txt");
        Assert.NotNull(current);
        Assert.Equal("version two", h.Provider.ReadContent(current.RemoteId));
    }

    [Fact]
    public async Task VersionsAreKeptOutsideThePoolsSyncRoot()
    {
        var h = await SetupAsync();

        await WriteAndSyncAsync(h, "doc.txt", "one", 0);
        await WriteAndSyncAsync(h, "doc.txt", "two", 5);

        var pool = await _store.GetPoolAsync("p1");
        var syncRoot = pool!.Members[0].RootFolderId!;

        // Only the current copy is under the pool's sync root; the retained one is not,
        // so remote change detection can never pull it back in as a new file.
        Assert.Equal(1, h.Provider.FileCountUnder(syncRoot));

        var version = Assert.Single(await _store.GetFileVersionsAsync("p1|doc.txt"));
        Assert.True(h.Provider.Exists(version.RemoteVersionId), "the kept copy should still exist");

        // It lives under Clouder/.versions/{Pool}, a sibling of the sync root.
        Assert.NotNull(h.Provider.FindByPath(InMemoryCloudProvider.RootId, "Clouder/.versions/Test Pool"));
    }

    [Fact]
    public async Task VersioningOff_DeletesTheReplacedCopyAsBefore()
    {
        var h = await SetupAsync(versioning: false);

        await WriteAndSyncAsync(h, "doc.txt", "one", 0);
        var first = (await _store.GetItemAsync("p1|doc.txt"))!.RemoteId;

        await WriteAndSyncAsync(h, "doc.txt", "two", 5);

        Assert.Empty(await _store.GetFileVersionsAsync("p1|doc.txt"));
        Assert.False(h.Provider.Exists(first), "with versioning off the old copy is deleted");
    }

    [Fact]
    public async Task EachReplacementAddsAVersionInOrder()
    {
        var h = await SetupAsync();

        await WriteAndSyncAsync(h, "doc.txt", "one", 0);
        await WriteAndSyncAsync(h, "doc.txt", "two", 5);
        await WriteAndSyncAsync(h, "doc.txt", "three", 10);

        var versions = (await _store.GetFileVersionsAsync("p1|doc.txt"))
            .OrderBy(v => v.VersionNumber).ToList();

        Assert.Equal(2, versions.Count);
        Assert.Equal("one", await ReadVersionAsync(h.Versions, versions[0].VersionId));
        Assert.Equal("two", await ReadVersionAsync(h.Versions, versions[1].VersionId));
    }

    // ── Retention ───────────────────────────────────────────────────────

    [Fact]
    public async Task OldestVersionsAreDiscardedBeyondTheLimit()
    {
        var h = await SetupAsync(maxVersions: 2);

        await WriteAndSyncAsync(h, "doc.txt", "one", 0);
        await WriteAndSyncAsync(h, "doc.txt", "two", 5);
        await WriteAndSyncAsync(h, "doc.txt", "three", 10);
        await WriteAndSyncAsync(h, "doc.txt", "four", 15);

        var versions = (await _store.GetFileVersionsAsync("p1|doc.txt"))
            .OrderBy(v => v.VersionNumber).ToList();

        // Three replacements happened, but only the two most recent are kept.
        Assert.Equal(2, versions.Count);
        Assert.Equal("two", await ReadVersionAsync(h.Versions, versions[0].VersionId));
        Assert.Equal("three", await ReadVersionAsync(h.Versions, versions[1].VersionId));
    }

    [Fact]
    public async Task DiscardedVersionsDoNotLeaveStoredCopiesBehind()
    {
        var h = await SetupAsync(maxVersions: 1);

        await WriteAndSyncAsync(h, "doc.txt", "one", 0);
        await WriteAndSyncAsync(h, "doc.txt", "two", 5);

        var firstVersion = Assert.Single(await _store.GetFileVersionsAsync("p1|doc.txt"));
        var firstRemoteId = firstVersion.RemoteVersionId;

        await WriteAndSyncAsync(h, "doc.txt", "three", 10);

        // The pruned version's stored copy is gone, not just its database row.
        Assert.False(h.Provider.Exists(firstRemoteId),
            "pruning must delete the stored copy, otherwise versions leak quota forever");
    }

    // ── Deleting the file ───────────────────────────────────────────────

    [Fact]
    public async Task DeletingAFileAlsoRemovesItsVersions()
    {
        var h = await SetupAsync();

        await WriteAndSyncAsync(h, "doc.txt", "one", 0);
        await WriteAndSyncAsync(h, "doc.txt", "two", 5);

        var version = Assert.Single(await _store.GetFileVersionsAsync("p1|doc.txt"));

        File.Delete(Path.Combine(_poolDir, "doc.txt"));
        await h.Sync.HandleLocalDeletionAsync("p1", Path.Combine(_poolDir, "doc.txt"));

        Assert.Empty(await _store.GetFileVersionsAsync("p1|doc.txt"));
        Assert.False(h.Provider.Exists(version.RemoteVersionId),
            "versions of a deleted file would otherwise be orphaned in the cloud");
        Assert.Equal(0, h.Provider.FileCountUnder(InMemoryCloudProvider.RootId));
    }

    // ── Restoring ───────────────────────────────────────────────────────

    [Fact]
    public async Task RestoringPutsTheOldContentBackAndKeepsTheCurrentOne()
    {
        var h = await SetupAsync();

        await WriteAndSyncAsync(h, "doc.txt", "original", 0);
        await WriteAndSyncAsync(h, "doc.txt", "edited", 5);

        var version = Assert.Single(await _store.GetFileVersionsAsync("p1|doc.txt"));

        var restoredPath = await h.Versions.RestoreAsync(version.VersionId);
        Assert.Equal("original", await File.ReadAllTextAsync(restoredPath));

        // Sync it up: the edited copy should now itself become a version.
        await h.Sync.SyncPoolAsync("p1");

        var current = await _store.GetItemAsync("p1|doc.txt");
        Assert.Equal("original", h.Provider.ReadContent(current!.RemoteId));

        var afterRestore = await _store.GetFileVersionsAsync("p1|doc.txt");
        Assert.Contains(afterRestore, v => v.VersionNumber == 2);
        Assert.Equal("edited", await ReadVersionAsync(h.Versions,
            afterRestore.First(v => v.VersionNumber == 2).VersionId));
    }

    [Fact]
    public async Task SavingACopyWritesTheVersionContent()
    {
        var h = await SetupAsync();

        await WriteAndSyncAsync(h, "doc.txt", "the old text", 0);
        await WriteAndSyncAsync(h, "doc.txt", "the new text", 5);

        var version = Assert.Single(await _store.GetFileVersionsAsync("p1|doc.txt"));
        var target = Path.Combine(Path.GetTempPath(), $"clouder_copy_{Guid.NewGuid():N}.txt");

        try
        {
            await h.Versions.SaveVersionAsAsync(version.VersionId, target);
            Assert.Equal("the old text", await File.ReadAllTextAsync(target));

            // Saving a copy must not disturb the current file.
            Assert.Equal("the new text", await File.ReadAllTextAsync(Path.Combine(_poolDir, "doc.txt")));
        }
        finally
        {
            if (File.Exists(target)) File.Delete(target);
        }
    }

    [Fact]
    public async Task DeletingOneVersionLeavesTheOthersIntact()
    {
        var h = await SetupAsync();

        await WriteAndSyncAsync(h, "doc.txt", "one", 0);
        await WriteAndSyncAsync(h, "doc.txt", "two", 5);
        await WriteAndSyncAsync(h, "doc.txt", "three", 10);

        var versions = (await _store.GetFileVersionsAsync("p1|doc.txt"))
            .OrderBy(v => v.VersionNumber).ToList();

        Assert.True(await h.Versions.DeleteVersionAsync(versions[0].VersionId));

        var left = Assert.Single(await _store.GetFileVersionsAsync("p1|doc.txt"));
        Assert.Equal(2, left.VersionNumber);
        Assert.Equal("two", await ReadVersionAsync(h.Versions, left.VersionId));
        Assert.False(h.Provider.Exists(versions[0].RemoteVersionId));
    }

    // ── Striped files ───────────────────────────────────────────────────

    [Fact]
    public async Task StripedFilesAreVersionedViaTheirChunks()
    {
        var h = await SetupAsync(accounts: 2);
        h.Sync.StripeThresholdBytes = 8;   // anything bigger gets split

        await WriteAndSyncAsync(h, "big.bin", "AAAABBBBCCCC", 0);
        await WriteAndSyncAsync(h, "big.bin", "DDDDEEEEFFFF", 5);

        var version = Assert.Single(await _store.GetFileVersionsAsync("p1|big.bin"));
        Assert.True(version.IsStriped, "a split file's version needs a chunk manifest");

        // The old content reassembles from chunks spread across both accounts.
        Assert.Equal("AAAABBBBCCCC", await ReadVersionAsync(h.Versions, version.VersionId));
    }
}
