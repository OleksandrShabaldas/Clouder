namespace Clouder.Core.Models;

public enum PlacementOutcome
{
    DirectPlace,
    StripingRequired,
    ReorgRequired,
    InsufficientSpace,
    Excluded
}

public sealed class PlacementDecision
{
    public required PlacementOutcome Outcome { get; set; }
    public string? TargetAccountId { get; set; }
    public List<StripePlan>? StripePlans { get; set; }
    public ReorganizationPlan? ReorgPlan { get; set; }
}

public sealed class StripePlan
{
    public required string AccountId { get; set; }
    public required long Offset { get; set; }
    public required long Length { get; set; }
    public int ChunkIndex { get; set; }

    /// <summary>Remote id of the uploaded chunk file (set after the chunk is stored).</summary>
    public string? RemoteId { get; set; }
}

public sealed class ReorganizationPlan
{
    public required string PoolId { get; set; }
    public List<FileMove> Moves { get; set; } = [];
    public long FreedBytes { get; set; }
}

public sealed class FileMove
{
    public required string FileId { get; set; }
    public required string FromAccountId { get; set; }
    public required string ToAccountId { get; set; }
}
