namespace Clouder.Core.Models;

public enum TransferKind
{
    Upload,
    Download,
    Delete,
    /// <summary>A file made available on demand as an Explorer placeholder.</summary>
    Placeholder,
    /// <summary>A file moved between accounts during reorganization.</summary>
    Move
}

public enum TransferOutcome
{
    Success,
    Failed,
    Skipped
}

/// <summary>
/// One completed transfer, kept as history so the dashboard can show recent activity
/// and totals moved per account.
/// </summary>
public sealed class TransferRecord
{
    public required string TransferId { get; set; }
    public required string PoolId { get; set; }

    /// <summary>
    /// The account the file landed on. For a striped file this is the account holding
    /// the first chunk; <see cref="AccountIds"/> lists them all.
    /// </summary>
    public string? AccountId { get; set; }

    public required string FileName { get; set; }
    public string? RelativePath { get; set; }
    public TransferKind Kind { get; set; }
    public TransferOutcome Outcome { get; set; }
    public long Bytes { get; set; }
    public long DurationMs { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string? Error { get; set; }

    /// <summary>Tracked item id ("{poolId}|{relativePath}"), for looking up stripe layout.</summary>
    public string? ItemId { get; set; }

    /// <summary>Number of chunks a striped file was split into. 0 means a whole-file transfer.</summary>
    public int ChunkCount { get; set; }

    /// <summary>Every account involved, comma-separated. Multiple only for striped files.</summary>
    public string? AccountIds { get; set; }

    public bool IsStriped => ChunkCount > 1;

    /// <summary>Average throughput, or null when the transfer was too brief to measure.</summary>
    public double? BytesPerSecond =>
        DurationMs > 0 && Bytes > 0 ? Bytes / (DurationMs / 1000.0) : null;
}

/// <summary>Aggregated transfer activity over a window, for dashboard summaries.</summary>
public sealed class TransferStats
{
    public int Uploads { get; set; }
    public int Downloads { get; set; }
    public int Failures { get; set; }
    public long BytesUploaded { get; set; }
    public long BytesDownloaded { get; set; }
}
