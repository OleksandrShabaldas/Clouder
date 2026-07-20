namespace Clouder.Core.Models;

public enum PlacementStrategy
{
    FillFirst,
    RoundRobin,
    LargestFree,
    Custom
}

public enum PoolMode
{
    Auto,
    Manual
}

public sealed class StoragePool
{
    public required string PoolId { get; set; }
    public required string Name { get; set; }
    public required string LocalPath { get; set; }
    public List<PoolMember> Members { get; set; } = [];
    public PoolMode Mode { get; set; } = PoolMode.Auto;
    public PlacementStrategy DefaultStrategy { get; set; } = PlacementStrategy.FillFirst;
}

public sealed class PoolMember
{
    public required string AccountId { get; set; }
    public required string ProviderId { get; set; }

    /// <summary>
    /// Fill tier. Lower numbers are filled first, and members sharing a number form
    /// one tier that the pool's placement strategy distributes across. Only when no
    /// member of a tier can take a file does placement move to the next tier.
    /// e.g. A, B, C at 0 and D at 1: files spread across A/B/C by the strategy, and
    /// D is only used once A, B and C are all full.
    /// </summary>
    public int Priority { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>The remote folder this pool owns on the account (Clouder/{PoolName}).</summary>
    public string? RootFolderId { get; set; }

    /// <summary>
    /// Ceiling on how much this pool may store on the account, in bytes. 0 = no limit.
    /// Useful when the account is also used for things that have nothing to do with
    /// the pool: the pool will not grow past this even if the account has space.
    /// </summary>
    public long MaxUsageBytes { get; set; }

    /// <summary>
    /// Space to always leave free on the account, in bytes. 0 = none. Keeps headroom
    /// for whatever else lives in that cloud storage.
    /// </summary>
    public long ReserveBytes { get; set; }
}
