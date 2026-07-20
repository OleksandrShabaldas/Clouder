namespace Clouder.Storage;

/// <summary>
/// Feeds a stream to a consumer in sector-aligned blocks.
///
/// The Windows Cloud Files API requires every hydration transfer except the final one
/// to be a multiple of 4096 bytes. A single <c>Stream.Read</c> on a network stream
/// routinely returns fewer bytes than asked for, so passing raw read results straight
/// to <c>CfExecute</c> produces unaligned mid-file transfers that Windows rejects —
/// which is why hydration failed before. This helper always fills a whole block
/// before emitting, so only the last block can be short.
/// </summary>
public static class AlignedTransfer
{
    public const int Alignment = 4096;
    public const int DefaultBlockSize = 4 * 1024 * 1024; // multiple of Alignment

    /// <summary>
    /// Reads up to <paramref name="totalLength"/> bytes from <paramref name="source"/> and
    /// invokes <paramref name="emit"/> with (fileOffset, buffer, count) for each block.
    /// Every emitted block is a multiple of <see cref="Alignment"/> except possibly the
    /// final one. The buffer passed to <paramref name="emit"/> is reused — consumers must
    /// not retain it past the call.
    /// </summary>
    public static async Task<long> RunAsync(
        Stream source,
        long startOffset,
        long totalLength,
        Func<long, byte[], int, CancellationToken, Task> emit,
        int blockSize = DefaultBlockSize,
        CancellationToken ct = default)
    {
        if (blockSize <= 0 || blockSize % Alignment != 0)
            throw new ArgumentException($"Block size must be a positive multiple of {Alignment}.", nameof(blockSize));

        var buffer = new byte[blockSize];
        long offset = startOffset;
        long remaining = totalLength;
        long transferred = 0;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();

            int want = (int)Math.Min(blockSize, remaining);

            // Fill the block completely; short reads are normal on network streams and
            // must not become unaligned transfers.
            int filled = await source.ReadAtLeastAsync(
                buffer.AsMemory(0, want), want, throwOnEndOfStream: false, ct);

            if (filled == 0) break;

            await emit(offset, buffer, filled, ct);

            offset += filled;
            transferred += filled;
            remaining -= filled;

            if (filled < want) break; // source ended early
        }

        return transferred;
    }
}
