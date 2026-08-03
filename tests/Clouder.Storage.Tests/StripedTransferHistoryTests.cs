using Clouder.Core.Models;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

/// <summary>
/// A striped upload has to show up in the history with the chunk count and every
/// account it touched — otherwise "was this file split, and where did the pieces go?"
/// has no answer. Striped uploads were previously not recorded at all.
/// </summary>
public class StripedTransferHistoryTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly string _poolDir;
    private readonly SqliteMetadataStore _store;

    public StripedTransferHistoryTests()
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

    /// <summary>Two accounts, each too small for the file on its own, forcing a split.</summary>
    private async Task<PoolSyncService> SetupStripingPoolAsync(InMemoryCloudProvider provider)
    {
        await _store.InitializeAsync();

        foreach (var id in new[] { "acc-1", "acc-2" })
        {
            await _store.UpsertAccountAsync(new ProviderAccount
            {
                AccountId = id, ProviderId = "fake", DisplayName = $"Drive {id[^1]}",
                ConnectedAtUtc = DateTime.UtcNow
            });
        }

        await _store.UpsertPoolAsync(new StoragePool
        {
            PoolId = "p1", Name = "Split Pool", LocalPath = _poolDir,
            Members =
            [
                new PoolMember { AccountId = "acc-1", ProviderId = "fake", Priority = 0, IsEnabled = true },
                new PoolMember { AccountId = "acc-2", ProviderId = "fake", Priority = 0, IsEnabled = true }
            ]
        });

        var svc = new PoolSyncService(_store, new SingleProviderRegistry(provider))
        {
            // Anything over 1 KB gets split, so a small test file exercises the path.
            StripeThresholdBytes = 1024
        };
        return svc;
    }

    [Fact]
    public async Task StripedUpload_IsRecordedWithChunkCountAndAccounts()
    {
        var provider = new InMemoryCloudProvider();
        using var svc = await SetupStripingPoolAsync(provider);

        var path = Path.Combine(_poolDir, "big.bin");
        await File.WriteAllBytesAsync(path, new byte[4096]);

        await svc.SyncPoolAsync("p1");

        var transfer = Assert.Single(await _store.GetRecentTransfersAsync());
        Assert.Equal("big.bin", transfer.FileName);
        Assert.Equal(TransferKind.Upload, transfer.Kind);
        Assert.Equal(TransferOutcome.Success, transfer.Outcome);

        // The whole logical file is recorded once, not once per chunk.
        Assert.True(transfer.IsStriped, "a split file should be marked as striped");
        Assert.Equal(2, transfer.ChunkCount);
        Assert.Equal(4096, transfer.Bytes);

        // Both accounts are named, so the UI can say where the pieces went.
        Assert.NotNull(transfer.AccountIds);
        Assert.Contains("acc-1", transfer.AccountIds);
        Assert.Contains("acc-2", transfer.AccountIds);

        // The item id links back to the stripe layout.
        Assert.Equal("p1|big.bin", transfer.ItemId);
        var plans = await _store.GetStripePlansAsync(transfer.ItemId!);
        Assert.Equal(2, plans.Count);
    }

    [Fact]
    public async Task WholeFileUpload_IsNotMarkedStriped()
    {
        var provider = new InMemoryCloudProvider();
        using var svc = await SetupStripingPoolAsync(provider);
        svc.StripeThresholdBytes = 0;   // never split

        await File.WriteAllTextAsync(Path.Combine(_poolDir, "note.txt"), "small");
        await svc.SyncPoolAsync("p1");

        var transfer = Assert.Single(await _store.GetRecentTransfersAsync());
        Assert.False(transfer.IsStriped);
        Assert.Equal(0, transfer.ChunkCount);
        Assert.Equal("acc-1", transfer.AccountIds);
    }

    [Fact]
    public async Task ClearHistory_CanKeepFailuresOrRemoveEverything()
    {
        await _store.InitializeAsync();

        TransferRecord Make(TransferOutcome outcome) => new()
        {
            TransferId = Guid.NewGuid().ToString("N"),
            PoolId = "p1",
            FileName = "f.bin",
            Kind = TransferKind.Upload,
            Outcome = outcome,
            TimestampUtc = DateTime.UtcNow
        };

        await _store.AddTransferAsync(Make(TransferOutcome.Success));
        await _store.AddTransferAsync(Make(TransferOutcome.Success));
        await _store.AddTransferAsync(Make(TransferOutcome.Failed));

        // Clearing completed leaves anything that still needs attention.
        await _store.ClearTransfersAsync(onlyCompleted: true);
        var left = await _store.GetRecentTransfersAsync();
        Assert.Equal(TransferOutcome.Failed, Assert.Single(left).Outcome);

        await _store.ClearTransfersAsync();
        Assert.Empty(await _store.GetRecentTransfersAsync());
    }

    [Theory]
    [InlineData("report.pdf", MediaKind.Document)]
    [InlineData("holiday.JPG", MediaKind.Image)]
    [InlineData("movie.mkv", MediaKind.Video)]
    [InlineData("song.flac", MediaKind.Audio)]
    [InlineData("backup.7z", MediaKind.Archive)]
    [InlineData("program.exe", MediaKind.Other)]
    [InlineData("noextension", MediaKind.Other)]
    public void MediaClassification_SortsFilesIntoCategories(string fileName, MediaKind expected)
    {
        Assert.Equal(expected, MediaKindClassifier.Classify(fileName));
    }
}
