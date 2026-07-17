namespace Clouder.Core.Models;

public sealed class ProviderAccount
{
    public required string AccountId { get; set; }
    public required string ProviderId { get; set; }
    public required string DisplayName { get; set; }
    public string? Email { get; set; }
    public bool IsEnabled { get; set; } = true;
    public StorageQuota? Quota { get; set; }
    public DateTime ConnectedAtUtc { get; set; }
}
