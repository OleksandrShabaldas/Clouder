namespace Clouder.Core.Models;

/// <summary>
/// A previous copy of a file, kept when the file was replaced by a newer one.
///
/// Clouder replaces files rather than updating them in place — an edit uploads a new
/// object and the old one is retired — so a provider's own revision history never
/// accumulates for pool files. Versions are therefore whole retained copies that
/// Clouder moves aside into its own versions folder, not provider revisions.
/// </summary>
public sealed class FileVersion
{
    public required string VersionId { get; set; }

    /// <summary>Remote object holding this version's bytes. For striped versions see <see cref="ChunkManifest"/>.</summary>
    public required string RemoteVersionId { get; set; }

    /// <summary>The logical file this is a version of ("{poolId}|{relativePath}").</summary>
    public required string FileId { get; set; }

    public long Size { get; set; }

    /// <summary>When this content was last modified — i.e. the age of the content itself.</summary>
    public DateTime ModifiedAtUtc { get; set; }

    public string? ModifiedBy { get; set; }

    /// <summary>Account holding the retained copy.</summary>
    public string? AccountId { get; set; }

    public string? ProviderId { get; set; }

    /// <summary>1 for the oldest retained copy, increasing with each replacement.</summary>
    public int VersionNumber { get; set; }

    /// <summary>When Clouder archived this copy.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// For a version of a striped file: the chunk layout as JSON, since its bytes are
    /// spread over several accounts rather than sitting in one object.
    /// </summary>
    public string? ChunkManifest { get; set; }

    public bool IsStriped => !string.IsNullOrEmpty(ChunkManifest);
}

/// <summary>One chunk of a retained striped version.</summary>
public sealed class VersionChunk
{
    public int ChunkIndex { get; set; }
    public required string AccountId { get; set; }
    public required string RemoteId { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
}
