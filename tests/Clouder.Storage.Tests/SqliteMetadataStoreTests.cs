using Clouder.Core.Models;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

public class SqliteMetadataStoreTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly SqliteMetadataStore _store;

    public SqliteMetadataStoreTests()
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

    [Fact]
    public async Task Initialize_CreatesDatabase()
    {
        await _store.InitializeAsync();
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task Account_RoundTrip()
    {
        await _store.InitializeAsync();

        var account = new ProviderAccount
        {
            AccountId = "acc-1",
            ProviderId = "google-drive",
            DisplayName = "Test Account",
            Email = "test@gmail.com",
            IsEnabled = true,
            ConnectedAtUtc = DateTime.UtcNow
        };

        await _store.UpsertAccountAsync(account);

        var loaded = await _store.GetAccountAsync("acc-1");
        Assert.NotNull(loaded);
        Assert.Equal("google-drive", loaded.ProviderId);
        Assert.Equal("Test Account", loaded.DisplayName);
        Assert.Equal("test@gmail.com", loaded.Email);
        Assert.True(loaded.IsEnabled);
    }

    [Fact]
    public async Task Account_Update_PreservesRelated()
    {
        await _store.InitializeAsync();

        var account = new ProviderAccount
        {
            AccountId = "acc-1",
            ProviderId = "google-drive",
            DisplayName = "Original",
            ConnectedAtUtc = DateTime.UtcNow
        };
        await _store.UpsertAccountAsync(account);

        var item = new CloudItem
        {
            Id = "item-1",
            RemoteId = "remote-1",
            ProviderId = "google-drive",
            AccountId = "acc-1",
            Name = "test.txt",
            Type = CloudItemType.File,
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow
        };
        await _store.UpsertItemAsync(item);

        account.DisplayName = "Updated";
        await _store.UpsertAccountAsync(account);

        var loadedItem = await _store.GetItemAsync("item-1");
        Assert.NotNull(loadedItem);
    }

    [Fact]
    public async Task Account_GetAll_ReturnsOrdered()
    {
        await _store.InitializeAsync();

        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-z", ProviderId = "mega", DisplayName = "Zulu",
            ConnectedAtUtc = DateTime.UtcNow
        });
        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-a", ProviderId = "google-drive", DisplayName = "Alpha",
            ConnectedAtUtc = DateTime.UtcNow
        });

        var all = await _store.GetAllAccountsAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("Alpha", all[0].DisplayName);
        Assert.Equal("Zulu", all[1].DisplayName);
    }

    [Fact]
    public async Task Account_Delete_CascadesToItems()
    {
        await _store.InitializeAsync();

        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-1", ProviderId = "google-drive", DisplayName = "Test",
            ConnectedAtUtc = DateTime.UtcNow
        });

        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "item-1", RemoteId = "r1", ProviderId = "google-drive", AccountId = "acc-1",
            Name = "file.txt", Type = CloudItemType.File,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });

        await _store.DeleteAccountAsync("acc-1");

        var item = await _store.GetItemAsync("item-1");
        Assert.Null(item);
    }

    [Fact]
    public async Task Pool_WithMembers_RoundTrip()
    {
        await _store.InitializeAsync();

        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-1", ProviderId = "google-drive", DisplayName = "GDrive 1",
            ConnectedAtUtc = DateTime.UtcNow
        });
        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-2", ProviderId = "mega", DisplayName = "MEGA",
            ConnectedAtUtc = DateTime.UtcNow
        });

        var pool = new StoragePool
        {
            PoolId = "pool-1",
            Name = "Main Pool",
            LocalPath = @"C:\Users\Test\Clouder",
            Mode = PoolMode.Auto,
            DefaultStrategy = PlacementStrategy.LargestFree,
            Members =
            [
                new PoolMember { AccountId = "acc-1", ProviderId = "google-drive", Priority = 1, IsEnabled = true },
                new PoolMember { AccountId = "acc-2", ProviderId = "mega", Priority = 2, IsEnabled = true }
            ]
        };

        await _store.UpsertPoolAsync(pool);

        var loaded = await _store.GetPoolAsync("pool-1");
        Assert.NotNull(loaded);
        Assert.Equal("Main Pool", loaded.Name);
        Assert.Equal(PoolMode.Auto, loaded.Mode);
        Assert.Equal(PlacementStrategy.LargestFree, loaded.DefaultStrategy);
        Assert.Equal(2, loaded.Members.Count);
        Assert.Equal("acc-1", loaded.Members[0].AccountId);
        Assert.Equal(1, loaded.Members[0].Priority);
    }

    [Fact]
    public async Task Pool_Update_ReplacesMembers()
    {
        await _store.InitializeAsync();

        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-1", ProviderId = "google-drive", DisplayName = "Test",
            ConnectedAtUtc = DateTime.UtcNow
        });

        var pool = new StoragePool
        {
            PoolId = "pool-1",
            Name = "Pool",
            LocalPath = @"C:\Test",
            Members = [ new PoolMember { AccountId = "acc-1", ProviderId = "google-drive", Priority = 1 } ]
        };
        await _store.UpsertPoolAsync(pool);

        pool.Members = [];
        await _store.UpsertPoolAsync(pool);

        var loaded = await _store.GetPoolAsync("pool-1");
        Assert.NotNull(loaded);
        Assert.Empty(loaded.Members);
    }

    [Fact]
    public async Task Item_GetChildren_ReturnsSorted()
    {
        await _store.InitializeAsync();

        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-1", ProviderId = "google-drive", DisplayName = "Test",
            ConnectedAtUtc = DateTime.UtcNow
        });

        var folder = new CloudItem
        {
            Id = "folder-1", RemoteId = "rf1", ProviderId = "google-drive", AccountId = "acc-1",
            Name = "Documents", Type = CloudItemType.Folder,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        };
        await _store.UpsertItemAsync(folder);

        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "file-2", RemoteId = "r2", ProviderId = "google-drive", AccountId = "acc-1",
            Name = "zebra.txt", ParentId = "folder-1", Type = CloudItemType.File,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });
        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "folder-sub", RemoteId = "r3", ProviderId = "google-drive", AccountId = "acc-1",
            Name = "SubFolder", ParentId = "folder-1", Type = CloudItemType.Folder,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });
        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "file-1", RemoteId = "r1", ProviderId = "google-drive", AccountId = "acc-1",
            Name = "alpha.txt", ParentId = "folder-1", Type = CloudItemType.File,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });

        var children = await _store.GetChildrenAsync("folder-1");
        Assert.Equal(3, children.Count);
        Assert.Equal(CloudItemType.Folder, children[0].Type);
        Assert.Equal("alpha.txt", children[1].Name);
        Assert.Equal("zebra.txt", children[2].Name);
    }

    [Fact]
    public async Task FileVersion_RoundTrip()
    {
        await _store.InitializeAsync();

        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-1", ProviderId = "google-drive", DisplayName = "Test",
            ConnectedAtUtc = DateTime.UtcNow
        });

        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "file-1", RemoteId = "r1", ProviderId = "google-drive", AccountId = "acc-1",
            Name = "doc.txt", Type = CloudItemType.File,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });

        await _store.AddFileVersionAsync(new FileVersion
        {
            VersionId = "v1", RemoteVersionId = "rv1", FileId = "file-1",
            Size = 1024, ModifiedAtUtc = DateTime.UtcNow.AddHours(-1)
        });
        await _store.AddFileVersionAsync(new FileVersion
        {
            VersionId = "v2", RemoteVersionId = "rv2", FileId = "file-1",
            Size = 2048, ModifiedAtUtc = DateTime.UtcNow, ModifiedBy = "user@test.com"
        });

        var versions = await _store.GetFileVersionsAsync("file-1");
        Assert.Equal(2, versions.Count);
        Assert.Equal("v2", versions[0].VersionId);
        Assert.Equal("v1", versions[1].VersionId);
    }

    [Fact]
    public async Task StripePlans_RoundTrip()
    {
        await _store.InitializeAsync();

        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-1", ProviderId = "google-drive", DisplayName = "GD",
            ConnectedAtUtc = DateTime.UtcNow
        });
        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-2", ProviderId = "mega", DisplayName = "MEGA",
            ConnectedAtUtc = DateTime.UtcNow
        });

        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "big-file", RemoteId = "r1", ProviderId = "google-drive", AccountId = "acc-1",
            Name = "bigfile.zip", Type = CloudItemType.File, Size = 5_000_000_000,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });

        var plans = new List<StripePlan>
        {
            new() { AccountId = "acc-1", ChunkIndex = 0, Offset = 0, Length = 2_000_000_000 },
            new() { AccountId = "acc-2", ChunkIndex = 1, Offset = 2_000_000_000, Length = 3_000_000_000 }
        };
        await _store.SaveStripeePlansAsync("big-file", plans);

        var loaded = await _store.GetStripePlansAsync("big-file");
        Assert.Equal(2, loaded.Count);
        Assert.Equal(0, loaded[0].ChunkIndex);
        Assert.Equal(2_000_000_000, loaded[0].Length);
        Assert.Equal("acc-2", loaded[1].AccountId);
    }
}
