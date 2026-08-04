namespace Clouder.Core.Models;

/// <summary>Where a pool keeps the previous copies of its files.</summary>
public enum VersionPlacement
{
    /// <summary>
    /// Beside the file they came from. Cheapest by far: archiving is a move within the
    /// same account, so no bytes cross the network.
    /// </summary>
    SameAccount,

    /// <summary>
    /// Spread over the pool's accounts using <see cref="VersionPolicy.PlacementStrategy"/>.
    /// Balances where history lives, but each version has to be copied to its new home.
    /// </summary>
    Balanced,

    /// <summary>
    /// Only on accounts flagged as version stores. Keeps history off the accounts holding
    /// live files, at the cost of copying every version there.
    /// </summary>
    DedicatedAccounts
}

/// <summary>Whether stored versions are split across accounts.</summary>
public enum VersionStriping
{
    /// <summary>Keep whatever layout the file already had — the only option that needs no data transfer.</summary>
    Inherit,

    /// <summary>Always store a version as one whole object, joining a split file back together.</summary>
    Never,

    /// <summary>Always split a version across the accounts available to it.</summary>
    Always
}

/// <summary>
/// How one pool handles previous copies of its files. Values left null fall back to the
/// application-wide settings, so a pool only overrides what it cares about.
/// </summary>
public sealed class VersionPolicy
{
    /// <summary>Keep versions at all. Null = follow the global setting.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Versions to keep per file; 0 = unlimited. Null = follow the global setting.</summary>
    public int? MaxVersionsPerFile { get; set; }

    /// <summary>Discard versions older than this many days; 0 = never. Null = follow the global setting.</summary>
    public int? RetentionDays { get; set; }

    /// <summary>Cap on the total size of all versions in this pool. 0 = no cap.</summary>
    public long MaxTotalBytes { get; set; }

    /// <summary>Don't keep versions of files larger than this. 0 = no limit.</summary>
    public long MaxVersionSizeBytes { get; set; }

    /// <summary>
    /// Minimum gap between versions of the same file. Stops an application that saves
    /// every few seconds from filling the history with near-identical copies. 0 = keep every change.
    /// </summary>
    public int MinIntervalMinutes { get; set; }

    public VersionPlacement Placement { get; set; } = VersionPlacement.SameAccount;

    /// <summary>Which account to choose under <see cref="VersionPlacement.Balanced"/>. Null = the pool's own strategy.</summary>
    public PlacementStrategy? PlacementStrategy { get; set; }

    public VersionStriping Striping { get; set; } = VersionStriping.Inherit;

    /// <summary>True when archiving needs to copy bytes rather than just move an object.</summary>
    public bool RequiresTransfer =>
        Placement != VersionPlacement.SameAccount || Striping != VersionStriping.Inherit;
}
