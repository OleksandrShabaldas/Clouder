using Clouder.Core.Models;
using Clouder.Core.Providers;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

/// <summary>
/// Regression tests for ExecuteReorganizationAsync: the source copy must be deleted
/// by its REMOTE id (not the internal file id), only after the destination upload
/// succeeded, and one failed move must not abort the remaining moves.
/// </summary>
public class ReorgExecutionTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly SqliteMetadataStore _store;

    public ReorgExecutionTests()
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

    private async Task<(RecordingProvider Provider, StoragePoolManager Manager)> SetupAsync()
    {
        await _store.InitializeAsync();

        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-a", ProviderId = "fake", DisplayName = "A", ConnectedAtUtc = DateTime.UtcNow
        });
        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-b", ProviderId = "fake", DisplayName = "B", ConnectedAtUtc = DateTime.UtcNow
        });

        var provider = new RecordingProvider();
        var manager = new StoragePoolManager(_store, new SingleRegistry(provider));
        return (provider, manager);
    }

    [Fact]
    public async Task Move_DeletesSourceByRemoteId_AfterUpload()
    {
        var (provider, manager) = await SetupAsync();

        provider.Files["acc-a:orig-remote"] = [1, 2, 3];
        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "pool-1|a.dat", RemoteId = "orig-remote", ProviderId = "fake", AccountId = "acc-a",
            Name = "a.dat", Type = CloudItemType.File, Size = 3,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });

        var plan = new ReorganizationPlan
        {
            PoolId = "pool-1",
            Moves = [new FileMove { FileId = "pool-1|a.dat", FromAccountId = "acc-a", ToAccountId = "acc-b" }]
        };

        await manager.ExecuteReorganizationAsync(plan);

        // The delete must target the original remote id on the source account…
        var delete = Assert.Single(provider.Ops.Where(o => o.Kind == "delete"));
        Assert.Equal("acc-a", delete.AccountId);
        Assert.Equal("orig-remote", delete.RemoteId);

        // …and must come after the upload.
        var uploadIndex = provider.Ops.FindIndex(o => o.Kind == "upload");
        var deleteIndex = provider.Ops.FindIndex(o => o.Kind == "delete");
        Assert.True(uploadIndex >= 0 && deleteIndex > uploadIndex,
            $"expected upload before delete, got: {string.Join(", ", provider.Ops.Select(o => o.Kind))}");

        // Data lives on the destination now; metadata points at it.
        Assert.DoesNotContain("acc-a:orig-remote", provider.Files.Keys);
        Assert.Contains(provider.Files.Keys, k => k.StartsWith("acc-b:"));

        var item = await _store.GetItemAsync("pool-1|a.dat");
        Assert.NotNull(item);
        Assert.Equal("acc-b", item.AccountId);
        Assert.NotEqual("orig-remote", item.RemoteId);
    }

    [Fact]
    public async Task FailedMove_DoesNotAbortRemainingMoves()
    {
        var (provider, manager) = await SetupAsync();

        // First move's source data is missing → download will throw.
        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "pool-1|missing.dat", RemoteId = "gone", ProviderId = "fake", AccountId = "acc-a",
            Name = "missing.dat", Type = CloudItemType.File, Size = 1,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });

        provider.Files["acc-a:ok-remote"] = [9, 9];
        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "pool-1|ok.dat", RemoteId = "ok-remote", ProviderId = "fake", AccountId = "acc-a",
            Name = "ok.dat", Type = CloudItemType.File, Size = 2,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });

        var plan = new ReorganizationPlan
        {
            PoolId = "pool-1",
            Moves =
            [
                new FileMove { FileId = "pool-1|missing.dat", FromAccountId = "acc-a", ToAccountId = "acc-b" },
                new FileMove { FileId = "pool-1|ok.dat", FromAccountId = "acc-a", ToAccountId = "acc-b" }
            ]
        };

        await manager.ExecuteReorganizationAsync(plan);

        // The second (valid) move still went through.
        var item = await _store.GetItemAsync("pool-1|ok.dat");
        Assert.NotNull(item);
        Assert.Equal("acc-b", item.AccountId);
        Assert.DoesNotContain("acc-a:ok-remote", provider.Files.Keys);
    }
}

// ── Test fakes ──────────────────────────────────────────────────────────

internal sealed class SingleRegistry(ICloudProvider provider) : IProviderRegistry
{
    public ICloudProvider? GetProvider(string providerId) =>
        provider.ProviderId == providerId ? provider : null;
    public IReadOnlyList<ICloudProvider> GetAllProviders() => [provider];
    public void Register(ICloudProvider p) { }
}

internal sealed class RecordingProvider : ICloudProvider
{
    public sealed record Op(string Kind, string AccountId, string RemoteId);

    public List<Op> Ops { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = new(); // "{accountId}:{remoteId}"
    private int _nextId = 1;

    public string ProviderId => "fake";
    public string DisplayName => "Fake";
    public ProviderCapabilities Capabilities => ProviderCapabilities.Full;

    public Task<StorageQuota> GetQuotaAsync(string accountId, CancellationToken ct = default) =>
        Task.FromResult(new StorageQuota { TotalBytes = 1L << 40, UsedBytes = 0 });

    public Task<Stream> DownloadAsync(string accountId, string remoteId, CancellationToken ct = default)
    {
        Ops.Add(new Op("download", accountId, remoteId));
        if (!Files.TryGetValue($"{accountId}:{remoteId}", out var bytes))
            throw new FileNotFoundException($"remote '{remoteId}' missing for {accountId}");
        return Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    public Task<CloudItem> UploadAsync(string accountId, string remoteFolderId, string fileName, Stream content, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        var id = $"new-{_nextId++}";
        Files[$"{accountId}:{id}"] = ms.ToArray();
        Ops.Add(new Op("upload", accountId, id));
        return Task.FromResult(new CloudItem
        {
            Id = id, RemoteId = id, ProviderId = ProviderId, AccountId = accountId,
            Name = fileName, Type = CloudItemType.File, Size = ms.Length,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });
    }

    public Task DeleteAsync(string accountId, string remoteId, CancellationToken ct = default)
    {
        Ops.Add(new Op("delete", accountId, remoteId));
        if (!Files.Remove($"{accountId}:{remoteId}"))
            throw new FileNotFoundException($"remote '{remoteId}' missing for {accountId}");
        return Task.CompletedTask;
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
