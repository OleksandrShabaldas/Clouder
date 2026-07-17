using Clouder.Core.Models;

namespace Clouder.Core.Storage;

public interface IStoragePoolManager
{
    Task<PlacementDecision> DecidePlacementAsync(
        string poolId,
        string fileName,
        long fileSize,
        string? folderPath = null,
        CancellationToken ct = default);

    Task<ReorganizationPlan> PlanReorganizationAsync(
        string poolId,
        long requiredFreeBytes,
        CancellationToken ct = default);

    Task ExecuteReorganizationAsync(
        ReorganizationPlan plan,
        IProgress<ReorgProgress>? progress = null,
        CancellationToken ct = default);

    Task<PoolStatus> GetPoolStatusAsync(string poolId, CancellationToken ct = default);
}

public sealed class PoolStatus
{
    public required string PoolId { get; set; }
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long FreeBytes => TotalBytes - UsedBytes;
    public List<MemberStatus> Members { get; set; } = [];
}

public sealed class MemberStatus
{
    public required string AccountId { get; set; }
    public required string ProviderId { get; set; }
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long FreeBytes => TotalBytes - UsedBytes;
    public bool IsOnline { get; set; }
}

public sealed class ReorgProgress
{
    public int TotalMoves { get; set; }
    public int CompletedMoves { get; set; }
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
}
