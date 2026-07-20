using Clouder.Core.Models;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

/// <summary>
/// Regression tests for PoolSyncService (local → cloud): a failed replacement upload
/// must never destroy the existing cloud copy, deleting a local folder must propagate
/// to every tracked file inside it, and sync counters must report what actually happened.
/// </summary>
public class PoolSyncServiceTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _poolDir;
    private readonly SqliteMetadataStore _store;

    public PoolSyncServiceTests()
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

    private async Task SetupPoolAsync()
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
    }

    [Fact]
    public async Task ReplacementUploadFailure_KeepsOldCloudCopy()
    {
        await SetupPoolAsync();
        var provider = new InMemoryCloudProvider();
        using var svc = new PoolSyncService(_store, new SingleProviderRegistry(provider));

        // First sync uploads the file.
        var filePath = Path.Combine(_poolDir, "a.txt");
        await File.WriteAllTextAsync(filePath, "version one");
        await svc.SyncPoolAsync("p1");

        var item = await _store.GetItemAsync("p1|a.txt");
        Assert.NotNull(item);
        var oldRemote = item.RemoteId;
        Assert.True(provider.Exists(oldRemote));

        // Edit the file, then make the replacement upload fail.
        await File.WriteAllTextAsync(filePath, "version two — longer");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(1));
        provider.FailNextUpload = true;

        var progress = new CollectingProgress<SyncProgress>();
        await svc.SyncPoolAsync("p1", progress);

        // The upload failed — but the old cloud copy must still exist and still be tracked.
        Assert.True(provider.Exists(oldRemote),
            "old cloud copy was deleted even though the replacement upload failed");
        var itemAfterFailure = await _store.GetItemAsync("p1|a.txt");
        Assert.NotNull(itemAfterFailure);
        Assert.Equal(oldRemote, itemAfterFailure.RemoteId);
        Assert.Equal(1, progress.Items[^1].Failed);
        Assert.Equal(0, progress.Items[^1].Synced);

        // Next sync succeeds: new copy uploaded, old copy cleaned up.
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(2));
        await svc.SyncPoolAsync("p1");

        var itemAfterSuccess = await _store.GetItemAsync("p1|a.txt");
        Assert.NotNull(itemAfterSuccess);
        Assert.NotEqual(oldRemote, itemAfterSuccess.RemoteId);
        Assert.True(provider.Exists(itemAfterSuccess.RemoteId));
        Assert.False(provider.Exists(oldRemote),
            "replaced copy should be deleted after a successful re-upload");
    }

    [Fact]
    public async Task FolderDeletion_RemovesAllTrackedChildrenFromCloud()
    {
        await SetupPoolAsync();
        var provider = new InMemoryCloudProvider();
        using var svc = new PoolSyncService(_store, new SingleProviderRegistry(provider));

        var docsDir = Path.Combine(_poolDir, "docs");
        Directory.CreateDirectory(docsDir);
        await File.WriteAllTextAsync(Path.Combine(docsDir, "x.txt"), "xx");
        await File.WriteAllTextAsync(Path.Combine(docsDir, "y.txt"), "yy");
        await svc.SyncPoolAsync("p1");

        var sep = Path.DirectorySeparatorChar;
        Assert.NotNull(await _store.GetItemAsync($"p1|docs{sep}x.txt"));
        Assert.NotNull(await _store.GetItemAsync($"p1|docs{sep}y.txt"));
        Assert.Equal(2, provider.FileCountUnder(InMemoryCloudProvider.RootId));

        // Delete the folder locally and propagate.
        Directory.Delete(docsDir, true);
        await svc.HandleLocalDeletionAsync("p1", docsDir);

        Assert.Null(await _store.GetItemAsync($"p1|docs{sep}x.txt"));
        Assert.Null(await _store.GetItemAsync($"p1|docs{sep}y.txt"));
        Assert.Equal(0, provider.FileCountUnder(InMemoryCloudProvider.RootId));
    }

    [Fact]
    public async Task NoConnectedProvider_CountsAsSkipped_NotUploaded()
    {
        await SetupPoolAsync();
        using var svc = new PoolSyncService(_store, new SingleProviderRegistry(null));

        await File.WriteAllTextAsync(Path.Combine(_poolDir, "orphan.txt"), "data");

        var progress = new CollectingProgress<SyncProgress>();
        await svc.SyncPoolAsync("p1", progress);

        var last = progress.Items[^1];
        Assert.Equal(0, last.Synced);   // the old code reported this as 1 uploaded
        Assert.Equal(1, last.Skipped);
        Assert.Equal(0, last.Failed);
    }

    [Fact]
    public async Task ReconcilePlaceholders_MarksFilesSyncedBeforeExplorerWasEnabled()
    {
        await SetupPoolAsync();
        var provider = new InMemoryCloudProvider();
        using var svc = new PoolSyncService(_store, new SingleProviderRegistry(provider));

        // Upload while Explorer integration is OFF — the file is tracked and up to date,
        // so it will never re-upload and OnUploaded will never fire for it again.
        await File.WriteAllTextAsync(Path.Combine(_poolDir, "early.txt"), "synced before");
        await svc.SyncPoolAsync("p1");
        Assert.NotNull(await _store.GetItemAsync("p1|early.txt"));

        // Now switch Explorer integration on.
        var sink = new FakePlaceholderSink { ActivePool = "p1" };
        svc.Placeholders = sink;

        var reconciled = await svc.ReconcilePlaceholdersAsync("p1");

        Assert.Equal(1, reconciled);
        var marked = Assert.Single(sink.Uploaded);
        Assert.Equal(Path.Combine(_poolDir, "early.txt"), marked.LocalPath);
        Assert.Equal("p1|early.txt", marked.ItemId);
    }

    [Fact]
    public async Task ReconcilePlaceholders_SkipsWhenExplorerIsOff()
    {
        await SetupPoolAsync();
        var provider = new InMemoryCloudProvider();
        using var svc = new PoolSyncService(_store, new SingleProviderRegistry(provider));

        await File.WriteAllTextAsync(Path.Combine(_poolDir, "early.txt"), "synced");
        await svc.SyncPoolAsync("p1");

        // No sink at all, and a sink bound to a different pool: both are no-ops.
        Assert.Equal(0, await svc.ReconcilePlaceholdersAsync("p1"));

        svc.Placeholders = new FakePlaceholderSink { ActivePool = "other-pool" };
        Assert.Equal(0, await svc.ReconcilePlaceholdersAsync("p1"));
    }

    [Fact]
    public async Task Uploads_LandUnderThePoolsOwnRemoteFolder()
    {
        await SetupPoolAsync();
        var provider = new InMemoryCloudProvider();
        using var svc = new PoolSyncService(_store, new SingleProviderRegistry(provider));

        await File.WriteAllTextAsync(Path.Combine(_poolDir, "note.txt"), "hello");
        await svc.SyncPoolAsync("p1");

        // Not at the drive root — inside Clouder/Test Pool.
        Assert.Null(provider.FindByPath(InMemoryCloudProvider.RootId, "note.txt"));
        Assert.NotNull(provider.FindByPath(InMemoryCloudProvider.RootId, "Clouder/Test Pool/note.txt"));

        // And the member now remembers that folder.
        var pool = await _store.GetPoolAsync("p1");
        Assert.NotNull(pool);
        Assert.False(string.IsNullOrEmpty(pool.Members[0].RootFolderId));
    }
}

internal sealed class CollectingProgress<T> : IProgress<T>
{
    public List<T> Items { get; } = [];
    public void Report(T value) => Items.Add(value);
}
