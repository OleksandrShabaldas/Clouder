namespace Clouder.Core.Models;

public sealed class StorageQuota
{
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long FreeBytes => TotalBytes - UsedBytes;
}
