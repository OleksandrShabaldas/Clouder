namespace Clouder.Core.Models;

public enum CloudItemType
{
    File,
    Folder
}

public sealed class CloudItem
{
    public required string Id { get; set; }
    public required string RemoteId { get; set; }
    public required string ProviderId { get; set; }
    public required string AccountId { get; set; }
    public required string Name { get; set; }
    public string? ParentId { get; set; }
    public CloudItemType Type { get; set; }
    public long Size { get; set; }
    public string? ContentHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
}
