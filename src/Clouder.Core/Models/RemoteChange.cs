namespace Clouder.Core.Models;

public enum RemoteChangeType
{
    /// <summary>The item was created or modified remotely.</summary>
    Upserted,
    /// <summary>The item was deleted or trashed remotely.</summary>
    Deleted
}

public sealed class RemoteChange
{
    public required string RemoteId { get; set; }
    public required RemoteChangeType Type { get; set; }

    /// <summary>The remote item. Null for deletions (the item no longer exists).</summary>
    public CloudItem? Item { get; set; }
}

/// <summary>
/// A batch of remote changes plus the cursor to resume from next time.
/// </summary>
public sealed class RemoteChangeSet
{
    public List<RemoteChange> Changes { get; set; } = [];

    /// <summary>Opaque provider-specific resume token. Persisted between polls.</summary>
    public string? Cursor { get; set; }

    /// <summary>
    /// True when <see cref="Changes"/> enumerates every item currently under the
    /// sync root rather than an incremental delta — the case for providers with no
    /// change feed (MEGA). The caller must infer deletions by comparing against
    /// what it already tracks, and must not treat absence as "unchanged".
    /// </summary>
    public bool IsFullListing { get; set; }
}
