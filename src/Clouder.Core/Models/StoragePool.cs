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
    public int Priority { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? RootFolderId { get; set; }
}
