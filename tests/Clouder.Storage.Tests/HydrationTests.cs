using System.Text;
using Clouder.Core.Models;
using Clouder.Storage;

namespace Clouder.Storage.Tests;

/// <summary>
/// The logic behind on-demand file hydration (Explorer / CfApi): mapping a requested
/// byte range onto stripe chunks, keeping transfers sector-aligned despite short reads,
/// and streaming the right bytes back for both plain and striped files.
/// </summary>
public class StripeRangeMapperTests
{
    private static List<StripePlan> Plans() =>
    [
        new() { AccountId = "a", Offset = 0,   Length = 100, ChunkIndex = 0, RemoteId = "c0" },
        new() { AccountId = "b", Offset = 100, Length = 100, ChunkIndex = 1, RemoteId = "c1" },
        new() { AccountId = "c", Offset = 200, Length = 50,  ChunkIndex = 2, RemoteId = "c2" }
    ];

    [Fact]
    public void WholeFile_CoversEveryChunkCompletely()
    {
        var reads = StripeRangeMapper.Map(Plans(), 0, 250);

        Assert.Equal(3, reads.Count);
        Assert.All(reads, r => Assert.Equal(0, r.ChunkOffset));
        Assert.Equal(250, reads.Sum(r => r.Length));
    }

    [Fact]
    public void RangeInsideSingleChunk_TouchesOnlyThatChunk()
    {
        var reads = StripeRangeMapper.Map(Plans(), 120, 30);

        var read = Assert.Single(reads);
        Assert.Equal(1, read.ChunkIndex);
        Assert.Equal(20, read.ChunkOffset);   // 120 - 100
        Assert.Equal(30, read.Length);
    }

    [Fact]
    public void RangeSpanningChunks_SplitsAtTheBoundary()
    {
        var reads = StripeRangeMapper.Map(Plans(), 80, 60); // 80..140

        Assert.Equal(2, reads.Count);
        Assert.Equal(0, reads[0].ChunkIndex);
        Assert.Equal(80, reads[0].ChunkOffset);
        Assert.Equal(20, reads[0].Length);
        Assert.Equal(1, reads[1].ChunkIndex);
        Assert.Equal(0, reads[1].ChunkOffset);
        Assert.Equal(40, reads[1].Length);
    }

    [Fact]
    public void RangeBeyondEnd_IsClamped()
    {
        var reads = StripeRangeMapper.Map(Plans(), 240, 999);

        var read = Assert.Single(reads);
        Assert.Equal(2, read.ChunkIndex);
        Assert.Equal(40, read.ChunkOffset);
        Assert.Equal(10, read.Length); // only 10 bytes exist past offset 240
    }

    [Fact]
    public void EmptyRequest_ReturnsNothing()
    {
        Assert.Empty(StripeRangeMapper.Map(Plans(), 50, 0));
        Assert.Empty(StripeRangeMapper.Map([], 0, 100));
    }
}

public class AlignedTransferTests
{
    /// <summary>A stream that returns at most <c>maxRead</c> bytes per call, like a network stream.</summary>
    private sealed class ChoppyStream(byte[] data, int maxRead) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(maxRead, count), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ShortReads_StillProduceAlignedBlocks()
    {
        // 3.5 blocks worth of data, delivered 7 bytes at a time.
        const int blockSize = AlignedTransfer.Alignment * 2;
        var data = new byte[blockSize * 3 + 1234];
        Random.Shared.NextBytes(data);

        var emitted = new List<(long Offset, int Count)>();
        var received = new MemoryStream();

        await AlignedTransfer.RunAsync(
            new ChoppyStream(data, maxRead: 7),
            startOffset: 0,
            totalLength: data.Length,
            emit: (offset, buffer, count, _) =>
            {
                emitted.Add((offset, count));
                received.Write(buffer, 0, count);
                return Task.CompletedTask;
            },
            blockSize: blockSize);

        // Every block but the last must be sector-aligned — this is what CfApi requires
        // and what the old "emit whatever Read returned" code violated.
        foreach (var (_, count) in emitted.SkipLast(1))
            Assert.True(count % AlignedTransfer.Alignment == 0, $"unaligned mid-file block of {count} bytes");

        // Offsets are contiguous and the bytes survive intact.
        long expectedOffset = 0;
        foreach (var (offset, count) in emitted)
        {
            Assert.Equal(expectedOffset, offset);
            expectedOffset += count;
        }
        Assert.Equal(data, received.ToArray());
    }

    [Fact]
    public async Task PartialRange_StartsAtTheRequestedOffset()
    {
        var data = new byte[10000];
        Random.Shared.NextBytes(data);

        var emitted = new List<(long Offset, int Count)>();
        var received = new MemoryStream();

        await AlignedTransfer.RunAsync(
            new ChoppyStream(data[4096..8192], maxRead: 100),
            startOffset: 4096,
            totalLength: 4096,
            emit: (offset, buffer, count, _) =>
            {
                emitted.Add((offset, count));
                received.Write(buffer, 0, count);
                return Task.CompletedTask;
            },
            blockSize: AlignedTransfer.Alignment);

        Assert.Equal(4096, emitted[0].Offset);
        Assert.Equal(data[4096..8192], received.ToArray());
    }

    [Fact]
    public async Task RejectsUnalignedBlockSize()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AlignedTransfer.RunAsync(new MemoryStream([1, 2, 3]), 0, 3,
                (_, _, _, _) => Task.CompletedTask, blockSize: 1000));
    }
}

public class HydrationServiceTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly SqliteMetadataStore _store;

    public HydrationServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"clouder_test_{Guid.NewGuid():N}.db");
        _store = new SqliteMetadataStore(_dbPath);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private async Task<(HydrationService Hydration, InMemoryCloudProvider Provider)> SetupAsync()
    {
        await _store.InitializeAsync();
        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-1", ProviderId = "fake", DisplayName = "One", ConnectedAtUtc = DateTime.UtcNow
        });
        await _store.UpsertAccountAsync(new ProviderAccount
        {
            AccountId = "acc-2", ProviderId = "fake", DisplayName = "Two", ConnectedAtUtc = DateTime.UtcNow
        });

        var provider = new InMemoryCloudProvider();
        return (new HydrationService(_store, new SingleProviderRegistry(provider)), provider);
    }

    private static async Task<string> ReadAllAsync(Stream s)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        await s.DisposeAsync();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task PlainFile_HydratesRequestedRange()
    {
        var (hydration, provider) = await SetupAsync();
        const string content = "abcdefghijklmnopqrstuvwxyz";
        var remoteId = provider.PutRemoteFile(InMemoryCloudProvider.RootId, "alpha.txt", content);

        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "p1|alpha.txt", RemoteId = remoteId, ProviderId = "fake", AccountId = "acc-1",
            Name = "alpha.txt", Type = CloudItemType.File, Size = content.Length,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });

        Assert.Equal(content, await ReadAllAsync(await hydration.OpenRangeAsync("p1|alpha.txt", 0, content.Length)));
        Assert.Equal("fghij", await ReadAllAsync(await hydration.OpenRangeAsync("p1|alpha.txt", 5, 5)));
    }

    [Fact]
    public async Task StripedFile_ReassemblesAcrossChunks()
    {
        var (hydration, provider) = await SetupAsync();

        // "HELLO" on account 1, "WORLD!" on account 2 — one logical 11-byte file.
        var c0 = provider.PutRemoteFile(InMemoryCloudProvider.RootId, "big.bin.clpart000", "HELLO");
        var c1 = provider.PutRemoteFile(InMemoryCloudProvider.RootId, "big.bin.clpart001", "WORLD!");

        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "p1|big.bin", RemoteId = "striped:2", ProviderId = "clouder-striped", AccountId = "acc-1",
            Name = "big.bin", Type = CloudItemType.File, Size = 11,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });
        await _store.SaveStripeePlansAsync("p1|big.bin",
        [
            new StripePlan { AccountId = "acc-1", Offset = 0, Length = 5, ChunkIndex = 0, RemoteId = c0 },
            new StripePlan { AccountId = "acc-2", Offset = 5, Length = 6, ChunkIndex = 1, RemoteId = c1 }
        ]);

        // Whole file, and ranges that span the chunk boundary.
        Assert.Equal("HELLOWORLD!", await ReadAllAsync(await hydration.OpenRangeAsync("p1|big.bin", 0, 11)));
        Assert.Equal("LOWOR", await ReadAllAsync(await hydration.OpenRangeAsync("p1|big.bin", 3, 5)));
        Assert.Equal("D!", await ReadAllAsync(await hydration.OpenRangeAsync("p1|big.bin", 9, 2)));
    }

    [Fact]
    public async Task UntrackedItem_FailsClearly()
    {
        var (hydration, _) = await SetupAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => hydration.OpenRangeAsync("p1|ghost.txt", 0, 10));
        Assert.Contains("not tracked", ex.Message);
    }

    [Fact]
    public async Task StripedFileMissingChunkLocation_FailsClearly()
    {
        var (hydration, _) = await SetupAsync();

        await _store.UpsertItemAsync(new CloudItem
        {
            Id = "p1|broken.bin", RemoteId = "striped:1", ProviderId = "clouder-striped", AccountId = "acc-1",
            Name = "broken.bin", Type = CloudItemType.File, Size = 10,
            CreatedAtUtc = DateTime.UtcNow, ModifiedAtUtc = DateTime.UtcNow
        });
        await _store.SaveStripeePlansAsync("p1|broken.bin",
        [
            new StripePlan { AccountId = "acc-1", Offset = 0, Length = 10, ChunkIndex = 0, RemoteId = null }
        ]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => hydration.OpenRangeAsync("p1|broken.bin", 0, 10));
        Assert.Contains("no stored location", ex.Message);
    }
}
