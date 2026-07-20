namespace Clouder.Core.Models;

public enum CloudItemType
{
    File,
    Folder
}

public enum SyncState
{
    /// <summary>Local and cloud copies match as of the last sync.</summary>
    Synced,
    /// <summary>Changed locally; waiting to upload.</summary>
    PendingUpload,
    /// <summary>Changed remotely; waiting to download.</summary>
    PendingDownload,
    /// <summary>Changed on both sides; awaiting resolution.</summary>
    Conflict
}

public sealed class CloudItem
{
    public required string Id { get; set; }
    public required string RemoteId { get; set; }
    public required string ProviderId { get; set; }
    public required string AccountId { get; set; }
    public required string Name { get; set; }

    /// <summary>
    /// The item's parent. Providers set this to the raw REMOTE parent id; the sync
    /// layer uses it to resolve an item's path under a pool member's root folder.
    /// </summary>
    public string? ParentId { get; set; }

    public CloudItemType Type { get; set; }
    public long Size { get; set; }
    public string? ContentHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
    public SyncState SyncState { get; set; } = SyncState.Synced;
}
