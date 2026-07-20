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
    public string? AccountId { get; set; }
    public required string FileName { get; set; }
    public string? RelativePath { get; set; }
    public TransferKind Kind { get; set; }
    public TransferOutcome Outcome { get; set; }
    public long Bytes { get; set; }
    public long DurationMs { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string? Error { get; set; }
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
