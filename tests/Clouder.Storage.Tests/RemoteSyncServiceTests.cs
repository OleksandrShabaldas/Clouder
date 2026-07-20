using Clouder.Core.Models;
using Clouder.Core.Sync;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

/// <summary>Stands in for the CfApi placeholder sink, which needs real Explorer to exercise.</summary>
internal sealed class FakePlaceholderSink : IPlaceholderSink
{
    public string ActivePool { get; init; } = "";
    public bool FailCreation { get; init; }

    public List<(string PoolId, string LocalPath, CloudItem Item)> Created { get; } = [];
    public List<(string PoolId, string LocalPath, string ItemId)> Uploaded { get; } = [];

    public bool IsActiveFor(string poolId) => poolId == ActivePool;

    public bool TryCreatePlaceholder(string poolId, string localFilePath, CloudItem item)
    {
        if (FailCreation) return false;
        Created.Add((poolId, localFilePath, item));
        return true;
    }

    public void OnUploaded(string poolId, string localFilePath, string itemId) =>
        Uploaded.Add((poolId, localFilePath, itemId));
}

/// <summary>
/// Cloud → local sync: remote additions come down, remote deletions remove local
/// files, unrelated cloud files are ignored, downloads don't bounce back up, and
/// each conflict policy behaves distinctly.
/// </summary>
public class RemoteSyncServiceTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _poolDir;
    private readonly SqliteMetadataStore _store;

    public RemoteSyncServiceTests()
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
        PoolSyncService Local,
        RemoteSyncService Remote,
        ConflictHandler Conflicts);

    private async Task<Harness> SetupAsync(
        ConflictResolution policy = ConflictResolution.NewestWins,
        bool incremental = true)
    {
        await _store.InitializeAsync();
        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-1", ProviderId = "fake", DisplayName = "Fake One", ConnectedAtUtc = DateTime.UtcNow
        });
        await _store.UpsertPoolAsync(new StoragePool
        {
            PoolId = "p1", Name = "Test Pool", LocalPath = _poolDir,
            Members = [new PoolMember { AccountId = "acc-1", ProviderId = "fake", Priority = 0, IsEnabled = true }]
        });

        var provider = new InMemoryCloudProvider { SupportsIncremental = incremental };
        var registry = new SingleProviderRegistry(provider);
        var conflicts = new ConflictHandler(_store) { Policy = policy };
        var roots = new RemoteRootResolver(_store);
        var local = new PoolSyncService(_store, registry, conflicts, roots);
        var remote = new RemoteSyncService(_store, registry, conflicts, roots, local);

        return new Harness(provider, local, remote, conflicts);
    }

    /// <summary>Runs one upload pass so the pool's remote root exists, then returns its id.</summary>
    private async Task<string> EstablishRootAsync(Harness h)
    {
        var seed = Path.Combine(_poolDir, "seed.txt");
        await File.WriteAllTextAsync(seed, "seed");
        await h.Local.SyncPoolAsync("p1");

        var pool = await _store.GetPoolAsync("p1");
        return pool!.Members[0].RootFolderId!;
    }

    // ── Downloads ───────────────────────────────────────────────────────

    [Fact]
    public async Task NewRemoteFile_IsDownloadedIntoThePool()
    {
        var h = await SetupAsync();
        var rootId = await EstablishRootAsync(h);

        // Establish the cursor, then add a file directly in the cloud.
        await h.Remote.SyncPoolAsync("p1");
        h.Provider.PutRemoteFile(rootId, "from-cloud.txt", "hello from the cloud");

        var result = await h.Remote.SyncPoolAsync("p1");

        Assert.Equal(1, result.Downloaded);
        var localPath = Path.Combine(_poolDir, "from-cloud.txt");
        Assert.True(File.Exists(localPath));
        Assert.Equal("hello from the cloud", await File.ReadAllTextAsync(localPath));

        var tracked = await _store.GetItemAsync("p1|from-cloud.txt");
        Assert.NotNull(tracked);
        Assert.Equal(SyncState.Synced, tracked.SyncState);
    }

    [Fact]
    public async Task RemoteFileInSubfolder_IsDownloadedToMatchingLocalPath()
    {
        var h = await SetupAsync();
        var rootId = await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        var sub = h.Provider.PutRemoteFolder(rootId, "reports");
        h.Provider.PutRemoteFile(sub, "q3.txt", "numbers");

        await h.Remote.SyncPoolAsync("p1");

        var expected = Path.Combine(_poolDir, "reports", "q3.txt");
        Assert.True(File.Exists(expected));
        Assert.Equal("numbers", await File.ReadAllTextAsync(expected));
    }

    [Fact]
    public async Task DownloadedFile_IsNotUploadedBackUp()
    {
        var h = await SetupAsync();
        var rootId = await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        var remoteId = h.Provider.PutRemoteFile(rootId, "loop.txt", "downloaded content");
        await h.Remote.SyncPoolAsync("p1");

        int filesBefore = h.Provider.FileCountUnder(rootId);

        // A local sweep must treat the downloaded file as already in sync.
        var progress = new CollectingProgress<SyncProgress>();
        await h.Local.SyncPoolAsync("p1", progress);

        Assert.Equal(0, progress.Items[^1].Synced);
        Assert.Equal(filesBefore, h.Provider.FileCountUnder(rootId));
        Assert.True(h.Provider.Exists(remoteId), "the original cloud file should be untouched");
    }

    [Fact]
    public async Task FilesOutsideThePoolsRemoteFolder_AreIgnored()
    {
        var h = await SetupAsync();
        await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        // A file elsewhere in the user's cloud storage — not ours to sync.
        h.Provider.PutRemoteFile(InMemoryCloudProvider.RootId, "personal-taxes.pdf", "private");

        var result = await h.Remote.SyncPoolAsync("p1");

        Assert.Equal(0, result.Downloaded);
        Assert.False(File.Exists(Path.Combine(_poolDir, "personal-taxes.pdf")));
    }

    // ── Deletions ───────────────────────────────────────────────────────

    [Fact]
    public async Task RemoteDeletion_RemovesTheLocalFile()
    {
        var h = await SetupAsync();
        var rootId = await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        var remoteId = h.Provider.PutRemoteFile(rootId, "temp.txt", "delete me");
        await h.Remote.SyncPoolAsync("p1");
        var localPath = Path.Combine(_poolDir, "temp.txt");
        Assert.True(File.Exists(localPath));

        h.Provider.RemoveRemote(remoteId);
        var result = await h.Remote.SyncPoolAsync("p1");

        Assert.Equal(1, result.DeletedLocally);
        Assert.False(File.Exists(localPath));
        Assert.Null(await _store.GetItemAsync("p1|temp.txt"));
    }

    [Fact]
    public async Task RemoteDeletion_KeepsLocallyEditedFile()
    {
        var h = await SetupAsync();
        var rootId = await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        var remoteId = h.Provider.PutRemoteFile(rootId, "notes.txt", "cloud version");
        await h.Remote.SyncPoolAsync("p1");
        var localPath = Path.Combine(_poolDir, "notes.txt");

        // Edit locally, then delete remotely.
        await File.WriteAllTextAsync(localPath, "my important local edits");
        File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow.AddHours(1));
        h.Provider.RemoveRemote(remoteId);

        await h.Remote.SyncPoolAsync("p1");

        Assert.True(File.Exists(localPath), "a locally edited file must not be deleted by a remote delete");
        Assert.Equal("my important local edits", await File.ReadAllTextAsync(localPath));
    }

    [Fact]
    public async Task FullListingProvider_InfersDeletions()
    {
        var h = await SetupAsync(incremental: false);
        var rootId = await EstablishRootAsync(h);

        h.Provider.PutRemoteFile(rootId, "gone-later.txt", "here for now");
        await h.Remote.SyncPoolAsync("p1");
        var localPath = Path.Combine(_poolDir, "gone-later.txt");
        Assert.True(File.Exists(localPath));

        var remoteId = h.Provider.FindByPath(rootId, "gone-later.txt")!;
        h.Provider.RemoveRemote(remoteId);

        var result = await h.Remote.SyncPoolAsync("p1");

        Assert.Equal(1, result.DeletedLocally);
        Assert.False(File.Exists(localPath));
    }

    // ── Conflicts ───────────────────────────────────────────────────────

    [Fact]
    public async Task NewestWins_TakesTheNewerSide()
    {
        var h = await SetupAsync(ConflictResolution.NewestWins);
        var rootId = await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        h.Provider.PutRemoteFile(rootId, "doc.txt", "remote v1");
        await h.Remote.SyncPoolAsync("p1");
        var localPath = Path.Combine(_poolDir, "doc.txt");

        // Both sides change; the cloud copy is newer.
        await File.WriteAllTextAsync(localPath, "local edit");
        File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow.AddMinutes(5));
        h.Provider.PutRemoteFile(rootId, "doc.txt", "remote v2 (newest)", DateTime.UtcNow.AddMinutes(30));

        await h.Remote.SyncPoolAsync("p1");

        Assert.Equal("remote v2 (newest)", await File.ReadAllTextAsync(localPath));
    }

    [Fact]
    public async Task KeepBoth_RenamesLocalAsideAndTakesRemote()
    {
        var h = await SetupAsync(ConflictResolution.KeepBoth);
        var rootId = await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        h.Provider.PutRemoteFile(rootId, "doc.txt", "remote v1");
        await h.Remote.SyncPoolAsync("p1");
        var localPath = Path.Combine(_poolDir, "doc.txt");

        await File.WriteAllTextAsync(localPath, "my local edit");
        File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow.AddMinutes(5));
        h.Provider.PutRemoteFile(rootId, "doc.txt", "remote v2", DateTime.UtcNow.AddMinutes(10));

        await h.Remote.SyncPoolAsync("p1");

        // Remote copy sits at the original name…
        Assert.Equal("remote v2", await File.ReadAllTextAsync(localPath));
        // …and the local edit survives under a conflicted-copy name.
        var conflicted = Directory.GetFiles(_poolDir, "doc (conflicted copy*.txt");
        var kept = Assert.Single(conflicted);
        Assert.Equal("my local edit", await File.ReadAllTextAsync(kept));
    }

    [Fact]
    public async Task AlwaysAsk_RecordsConflictAndTouchesNeitherSide()
    {
        var h = await SetupAsync(ConflictResolution.AlwaysAsk);
        var rootId = await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        h.Provider.PutRemoteFile(rootId, "doc.txt", "remote v1");
        await h.Remote.SyncPoolAsync("p1");
        var localPath = Path.Combine(_poolDir, "doc.txt");

        await File.WriteAllTextAsync(localPath, "local edit");
        File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow.AddMinutes(5));
        h.Provider.PutRemoteFile(rootId, "doc.txt", "remote v2", DateTime.UtcNow.AddMinutes(10));

        var result = await h.Remote.SyncPoolAsync("p1");

        Assert.Equal(1, result.Conflicts);
        Assert.Equal(0, result.Downloaded);
        Assert.Equal("local edit", await File.ReadAllTextAsync(localPath)); // untouched

        var conflict = Assert.Single(await _store.GetConflictsAsync("p1"));
        Assert.Equal("doc.txt", conflict.RelativePath);

        var tracked = await _store.GetItemAsync("p1|doc.txt");
        Assert.NotNull(tracked);
        Assert.Equal(SyncState.Conflict, tracked.SyncState);
    }

    [Fact]
    public async Task ResolveConflict_KeepRemote_DownloadsAndClearsTheConflict()
    {
        var h = await SetupAsync(ConflictResolution.AlwaysAsk);
        var rootId = await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        h.Provider.PutRemoteFile(rootId, "doc.txt", "remote v1");
        await h.Remote.SyncPoolAsync("p1");
        var localPath = Path.Combine(_poolDir, "doc.txt");

        await File.WriteAllTextAsync(localPath, "local edit");
        File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow.AddMinutes(5));
        h.Provider.PutRemoteFile(rootId, "doc.txt", "remote v2", DateTime.UtcNow.AddMinutes(10));
        await h.Remote.SyncPoolAsync("p1");

        var ok = await h.Remote.ResolveConflictAsync("p1|doc.txt", ConflictResolutionChoice.KeepRemote);

        Assert.True(ok);
        Assert.Equal("remote v2", await File.ReadAllTextAsync(localPath));
        Assert.Empty(await _store.GetConflictsAsync("p1"));
    }

    [Fact]
    public async Task ResolveConflict_KeepBoth_PreservesLocalCopy()
    {
        var h = await SetupAsync(ConflictResolution.AlwaysAsk);
        var rootId = await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        h.Provider.PutRemoteFile(rootId, "doc.txt", "remote v1");
        await h.Remote.SyncPoolAsync("p1");
        var localPath = Path.Combine(_poolDir, "doc.txt");

        await File.WriteAllTextAsync(localPath, "local edit");
        File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow.AddMinutes(5));
        h.Provider.PutRemoteFile(rootId, "doc.txt", "remote v2", DateTime.UtcNow.AddMinutes(10));
        await h.Remote.SyncPoolAsync("p1");

        var ok = await h.Remote.ResolveConflictAsync("p1|doc.txt", ConflictResolutionChoice.KeepBoth);

        Assert.True(ok);
        Assert.Equal("remote v2", await File.ReadAllTextAsync(localPath));
        var kept = Assert.Single(Directory.GetFiles(_poolDir, "doc (conflicted copy*.txt"));
        Assert.Equal("local edit", await File.ReadAllTextAsync(kept));
        Assert.Empty(await _store.GetConflictsAsync("p1"));
    }

    // ── Explorer placeholder mode ───────────────────────────────────────

    [Fact]
    public async Task PlaceholderMode_RegistersFileWithoutDownloadingContent()
    {
        var h = await SetupAsync();
        var rootId = await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        var sink = new FakePlaceholderSink { ActivePool = "p1" };
        h.Remote.Placeholders = sink;

        h.Provider.PutRemoteFile(rootId, "movie.mkv", "pretend this is 4 GB");
        var result = await h.Remote.SyncPoolAsync("p1");

        // Made available on demand, not downloaded.
        Assert.Equal(1, result.Placeholders);
        Assert.Equal(0, result.Downloaded);
        Assert.False(File.Exists(Path.Combine(_poolDir, "movie.mkv")),
            "placeholder mode must not write the file's content locally");

        var placeheld = Assert.Single(sink.Created);
        Assert.Equal(Path.Combine(_poolDir, "movie.mkv"), placeheld.LocalPath);

        // The item is tracked under the id the placeholder carries, which is what
        // hydration looks it up by when the user opens the file.
        var tracked = await _store.GetItemAsync("p1|movie.mkv");
        Assert.NotNull(tracked);
        Assert.Equal("p1|movie.mkv", placeheld.Item.Id);
        Assert.Equal(tracked.RemoteId, placeheld.Item.RemoteId);
    }

    [Fact]
    public async Task PlaceholderCreationFailure_FallsBackToDownloading()
    {
        var h = await SetupAsync();
        var rootId = await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        h.Remote.Placeholders = new FakePlaceholderSink { ActivePool = "p1", FailCreation = true };

        h.Provider.PutRemoteFile(rootId, "notes.txt", "real content");
        var result = await h.Remote.SyncPoolAsync("p1");

        Assert.Equal(0, result.Placeholders);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal("real content", await File.ReadAllTextAsync(Path.Combine(_poolDir, "notes.txt")));
    }

    [Fact]
    public async Task InactivePool_DownloadsNormally()
    {
        var h = await SetupAsync();
        var rootId = await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        // Sink present but active for a different pool.
        h.Remote.Placeholders = new FakePlaceholderSink { ActivePool = "some-other-pool" };

        h.Provider.PutRemoteFile(rootId, "doc.txt", "content");
        var result = await h.Remote.SyncPoolAsync("p1");

        Assert.Equal(0, result.Placeholders);
        Assert.Equal(1, result.Downloaded);
    }

    [Fact]
    public async Task StripeChunks_AreNotTreatedAsUserFiles()
    {
        var h = await SetupAsync();
        var rootId = await EstablishRootAsync(h);
        await h.Remote.SyncPoolAsync("p1");

        h.Provider.PutRemoteFile(rootId, "big.iso.clpart000", "chunk data");

        var result = await h.Remote.SyncPoolAsync("p1");

        Assert.Equal(0, result.Downloaded);
        Assert.False(File.Exists(Path.Combine(_poolDir, "big.iso.clpart000")));
    }
}
