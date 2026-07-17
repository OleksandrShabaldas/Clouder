using Clouder.Core.Models;
using Clouder.Core.Providers;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

/// <summary>
/// Regression tests for PoolSyncService: a failed replacement upload must never
/// destroy the existing cloud copy, deleting a local folder must propagate to every
/// tracked file inside it, and sync counters must report what actually happened.
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
        var provider = new MemoryProvider();
        using var svc = new PoolSyncService(_store, new OneProviderRegistry(provider));

        // First sync uploads the file.
        var filePath = Path.Combine(_poolDir, "a.txt");
        await File.WriteAllTextAsync(filePath, "version one");
        await svc.SyncPoolAsync("p1");

        var item = await _store.GetItemAsync("p1|a.txt");
        Assert.NotNull(item);
        var oldRemote = item.RemoteId;
        Assert.True(provider.Files.ContainsKey($"acc-1:{oldRemote}"));

        // Edit the file, then make the replacement upload fail.
        await File.WriteAllTextAsync(filePath, "version two — longer");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(1));
        provider.FailNextUpload = true;

        var progress = new CollectingProgress<SyncProgress>();
        await svc.SyncPoolAsync("p1", progress);

        // The upload failed — but the old cloud copy must still exist and still be tracked.
        Assert.True(provider.Files.ContainsKey($"acc-1:{oldRemote}"),
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
        Assert.True(provider.Files.ContainsKey($"acc-1:{itemAfterSuccess.RemoteId}"));
        Assert.False(provider.Files.ContainsKey($"acc-1:{oldRemote}"),
            "replaced copy should be deleted after a successful re-upload");
    }

    [Fact]
    public async Task FolderDeletion_RemovesAllTrackedChildrenFromCloud()
    {
        await SetupPoolAsync();
        var provider = new MemoryProvider();
        using var svc = new PoolSyncService(_store, new OneProviderRegistry(provider));

        var docsDir = Path.Combine(_poolDir, "docs");
        Directory.CreateDirectory(docsDir);
        await File.WriteAllTextAsync(Path.Combine(docsDir, "x.txt"), "xx");
        await File.WriteAllTextAsync(Path.Combine(docsDir, "y.txt"), "yy");
        await svc.SyncPoolAsync("p1");

        Assert.NotNull(await _store.GetItemAsync($"p1|docs{Path.DirectorySeparatorChar}x.txt"));
        Assert.NotNull(await _store.GetItemAsync($"p1|docs{Path.DirectorySeparatorChar}y.txt"));
        Assert.Equal(2, provider.Files.Count);

        // Delete the folder locally and propagate.
        Directory.Delete(docsDir, true);
        await svc.HandleLocalDeletionAsync("p1", docsDir);

        Assert.Null(await _store.GetItemAsync($"p1|docs{Path.DirectorySeparatorChar}x.txt"));
        Assert.Null(await _store.GetItemAsync($"p1|docs{Path.DirectorySeparatorChar}y.txt"));
        Assert.Empty(provider.Files);
    }

    [Fact]
    public async Task NoConnectedProvider_CountsAsSkipped_NotUploaded()
    {
        await SetupPoolAsync();
        using var svc = new PoolSyncService(_store, new OneProviderRegistry(null));

        await File.WriteAllTextAsync(Path.Combine(_poolDir, "orphan.txt"), "data");

        var progress = new CollectingProgress<SyncProgress>();
        await svc.SyncPoolAsync("p1", progress);

        var last = progress.Items[^1];
        Assert.Equal(0, last.Synced);   // the old code reported this as 1 uploaded
        Assert.Equal(1, last.Skipped);
        Assert.Equal(0, last.Failed);
    }
}

// ── Test fakes ──────────────────────────────────────────────────────────

file sealed class OneProviderRegistry(ICloudProvider? provider) : IProviderRegistry
{
    public ICloudProvider? GetProvider(string providerId) =>
        provider != null && provider.ProviderId == providerId ? provider : null;
    public IReadOnlyList<ICloudProvider> GetAllProviders() => provider == null ? [] : [provider];
    public void Register(ICloudProvider p) { }
}

file sealed class CollectingProgress<T> : IProgress<T>
{
    public List<T> Items { get; } = [];
    public void Report(T value) => Items.Add(value);
}

file sealed class MemoryProvider : ICloudProvider
{
    public Dictionary<string, byte[]> Files { get; } = new(); // "{accountId}:{remoteId}"
    public bool FailNextUpload { get; set; }
    private int _nextId = 1;

    public string ProviderId => "fake";
    public string DisplayName => "Fake";
    public ProviderCapabilities Capabilities => ProviderCapabilities.Full;

    public Task<StorageQuota> GetQuotaAsync(string accountId, CancellationToken ct = default) =>
        Task.FromResult(new StorageQuota { TotalBytes = 1L << 40, UsedBytes = 0 });

    public Task<CloudItem> UploadAsync(string accountId, string remoteFolderId, string fileName, Stream content, CancellationToken ct = default)
    {
        if (FailNextUpload)
        {
            FailNextUpload = false;
            throw new IOException("simulated upload failure");
        }
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        var id = $"r{_nextId++}";
        Files[$"{accountId}:{id}"] = ms.ToArray();
        return Task.FromResult(new CloudItem
        {
            Id = id, RemoteId = id, ProviderId = ProviderId, AccountId = accountId,
            Name = fileName, Type = CloudItemType.File, Size = ms.Length,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });
    }

    public Task DeleteAsync(string accountId, string remoteId, CancellationToken ct = default)
    {
        Files.Remove($"{accountId}:{remoteId}");
        return Task.CompletedTask;
    }

    public Task<Stream> DownloadAsync(string accountId, string remoteId, CancellationToken ct = default)
    {
        if (!Files.TryGetValue($"{accountId}:{remoteId}", out var bytes))
            throw new FileNotFoundException(remoteId);
        return Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    public Task<IReadOnlyList<CloudItem>> ListFolderAsync(string accountId, string remoteFolderId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CloudItem>>([]);

    public Task<CloudItem> CreateFolderAsync(string accountId, string parentRemoteId, string name, CancellationToken ct = default)
    {
        var id = $"folder-{_nextId++}";
        return Task.FromResult(new CloudItem
        {
            Id = id, RemoteId = id, ProviderId = ProviderId, AccountId = accountId,
            Name = name, Type = CloudItemType.Folder,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });
    }

    public Task<ProviderAccount> ConnectAccountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task DisconnectAccountAsync(string accountId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CloudItem?> GetItemAsync(string accountId, string remoteId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Stream> DownloadRangeAsync(string accountId, string remoteId, long offset, long length, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CloudItem> MoveAsync(string accountId, string remoteId, string newParentRemoteId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CloudItem> RenameAsync(string accountId, string remoteId, string newName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<FileVersion>> GetVersionsAsync(string accountId, string remoteId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FileVersion>>([]);
    public Task<Stream> DownloadVersionAsync(string accountId, string remoteId, string versionId, CancellationToken ct = default) => throw new NotImplementedException();
}
