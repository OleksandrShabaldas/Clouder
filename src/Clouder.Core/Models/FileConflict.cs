namespace Clouder.Core.Models;

public enum ConflictResolutionChoice
{
    /// <summary>Upload the local file, overwriting the cloud copy.</summary>
    KeepLocal,
    /// <summary>Download the cloud copy, overwriting the local file.</summary>
    KeepRemote,
    /// <summary>Rename the local file aside and download the cloud copy.</summary>
    KeepBoth
}

/// <summary>
/// A file that changed both locally and remotely since the last sync, recorded
/// when the conflict policy is AlwaysAsk so the user can decide.
/// </summary>
public sealed class FileConflict
{
    /// <summary>Same value as <see cref="ItemId"/> — one open conflict per file.</summary>
    public required string ConflictId { get; set; }
    public required string PoolId { get; set; }
    public required string ItemId { get; set; }
    public required string RelativePath { get; set; }
    public required string AccountId { get; set; }
    public required string RemoteId { get; set; }

    public DateTime LocalModifiedUtc { get; set; }
    public DateTime RemoteModifiedUtc { get; set; }
    public long LocalSize { get; set; }
    public long RemoteSize { get; set; }
    public DateTime DetectedAtUtc { get; set; }
}
