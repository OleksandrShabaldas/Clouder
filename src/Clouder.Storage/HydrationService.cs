using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Providers;
using Clouder.Core.Storage;

namespace Clouder.Storage;

/// <summary>One chunk-local read that contributes to a requested byte range of a striped file.</summary>
public readonly record struct StripeRead(
    string AccountId, string RemoteId, int ChunkIndex, long ChunkOffset, long Length);

/// <summary>
/// Maps a byte range of a logical (striped) file onto reads of the individual chunks.
/// Pure function — the interesting arithmetic in on-demand hydration.
/// </summary>
public static class StripeRangeMapper
{
    public static List<StripeRead> Map(IReadOnlyList<StripePlan> plans, long offset, long length)
    {
        var reads = new List<StripeRead>();
        if (length <= 0 || plans.Count == 0) return reads;

        long requestStart = Math.Max(0, offset);
        long requestEnd = offset + length;

        foreach (var plan in plans.OrderBy(p => p.ChunkIndex))
        {
            long planStart = plan.Offset;
            long planEnd = plan.Offset + plan.Length;

            if (planEnd <= requestStart) continue;   // entirely before the range
            if (planStart >= requestEnd) break;      // past the range (plans are ordered)

            long start = Math.Max(planStart, requestStart);
            long end = Math.Min(planEnd, requestEnd);
            if (end <= start) continue;

            reads.Add(new StripeRead(
                plan.AccountId,
                plan.RemoteId ?? "",
                plan.ChunkIndex,
                start - planStart,
                end - start));
        }

        return reads;
    }
}

/// <summary>
/// Streams file content for on-demand hydration, transparently handling files that
/// were striped across several accounts. Used by the Explorer (CfApi) integration when
/// Windows asks for a byte range of a placeholder file.
/// </summary>
public sealed class HydrationService
{
    private readonly IMetadataStore _store;
    private readonly IProviderRegistry _providers;

    private const string StripedProviderMarker = "clouder-striped";

    public HydrationService(IMetadataStore store, IProviderRegistry providers)
    {
        _store = store;
        _providers = providers;
    }

    /// <summary>
    /// Opens a read-only stream over <paramref name="length"/> bytes starting at
    /// <paramref name="offset"/> of the tracked item. The caller disposes the stream.
    /// </summary>
    public async Task<Stream> OpenRangeAsync(
        string itemId, long offset, long length, CancellationToken ct = default)
    {
        var item = await _store.GetItemAsync(itemId, ct)
            ?? throw new InvalidOperationException($"Item '{itemId}' is not tracked; cannot hydrate.");

        var plans = await _store.GetStripePlansAsync(itemId, ct);

        if (plans.Count > 0)
        {
            var reads = StripeRangeMapper.Map(plans, offset, length);
            foreach (var read in reads)
            {
                if (string.IsNullOrEmpty(read.RemoteId))
                    throw new InvalidOperationException(
                        $"Chunk {read.ChunkIndex} of '{item.Name}' has no stored location; cannot hydrate.");
            }
            return new StripeRangeStream(reads, OpenChunkRangeAsync);
        }

        if (item.ProviderId == StripedProviderMarker)
            throw new InvalidOperationException(
                $"'{item.Name}' is marked as striped but has no chunk records; cannot hydrate.");

        var provider = _providers.GetProvider(item.ProviderId)
            ?? throw new InvalidOperationException($"Provider '{item.ProviderId}' is not connected.");

        return await OpenProviderRangeAsync(provider, item.AccountId, item.RemoteId, offset, length, ct);
    }

    private async Task<Stream> OpenChunkRangeAsync(StripeRead read, CancellationToken ct)
    {
        var account = await _store.GetAccountAsync(read.AccountId, ct)
            ?? throw new InvalidOperationException($"Account '{read.AccountId}' not found.");
        var provider = _providers.GetProvider(account.ProviderId)
            ?? throw new InvalidOperationException($"Provider '{account.ProviderId}' is not connected.");

        return await OpenProviderRangeAsync(provider, read.AccountId, read.RemoteId, read.ChunkOffset, read.Length, ct);
    }

    /// <summary>
    /// Range read from a provider, falling back to "download and skip" for providers
    /// without native range support (MEGA), so hydration still works there.
    /// </summary>
    private static async Task<Stream> OpenProviderRangeAsync(
        ICloudProvider provider, string accountId, string remoteId, long offset, long length, CancellationToken ct)
    {
        if (provider.Capabilities.HasFlag(ProviderCapabilities.RangeDownload))
        {
            try
            {
                return await provider.DownloadRangeAsync(accountId, remoteId, offset, length, ct);
            }
            catch (Exception ex)
            {
                ClouderLog.Warn($"Range download failed for '{remoteId}' ({ex.Message}); falling back to full download");
            }
        }

        var full = await provider.DownloadAsync(accountId, remoteId, ct);
        if (offset > 0)
        {
            if (full.CanSeek)
            {
                full.Seek(offset, SeekOrigin.Begin);
            }
            else
            {
                var skipBuffer = new byte[81920];
                long toSkip = offset;
                while (toSkip > 0)
                {
                    int read = await full.ReadAsync(skipBuffer.AsMemory(0, (int)Math.Min(toSkip, skipBuffer.Length)), ct);
                    if (read == 0) break;
                    toSkip -= read;
                }
            }
        }

        return new BoundedReadStream(full, length);
    }
}

/// <summary>
/// Concatenates a sequence of chunk reads into one continuous stream, opening each
/// chunk only when it's reached.
/// </summary>
internal sealed class StripeRangeStream : Stream
{
    private readonly IReadOnlyList<StripeRead> _reads;
    private readonly Func<StripeRead, CancellationToken, Task<Stream>> _open;
    private readonly long _length;

    private int _index = -1;
    private Stream? _current;
    private long _position;

    public StripeRangeStream(
        IReadOnlyList<StripeRead> reads, Func<StripeRead, CancellationToken, Task<Stream>> open)
    {
        _reads = reads;
        _open = open;
        _length = reads.Sum(r => r.Length);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (true)
        {
            if (_current == null)
            {
                if (++_index >= _reads.Count) return 0;
                _current = await _open(_reads[_index], ct);
            }

            int read = await _current.ReadAsync(buffer, ct);
            if (read > 0)
            {
                _position += read;
                return read;
            }

            await _current.DisposeAsync();
            _current = null;
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        await ReadAsync(buffer.AsMemory(offset, count), ct);

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _current?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Limits an underlying stream to a fixed number of bytes.</summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private long _remaining;

    public BoundedReadStream(Stream inner, long maxBytes)
    {
        _inner = inner;
        _remaining = maxBytes;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_remaining <= 0) return 0;
        int toRead = (int)Math.Min(buffer.Length, _remaining);
        int read = await _inner.ReadAsync(buffer[..toRead], ct);
        _remaining -= read;
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        await ReadAsync(buffer.AsMemory(offset, count), ct);

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        int toRead = (int)Math.Min(count, _remaining);
        int read = _inner.Read(buffer, offset, toRead);
        _remaining -= read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
