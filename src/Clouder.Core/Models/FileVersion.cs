namespace Clouder.Core.Models;

public sealed class FileVersion
{
    public required string VersionId { get; set; }
    public required string RemoteVersionId { get; set; }
    public required string FileId { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }
}
