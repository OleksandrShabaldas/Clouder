namespace Clouder.Core.Models;

public enum NotificationSeverity
{
    Info,
    Warning,
    Critical
}

public sealed class AppNotification
{
    public required string NotificationId { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public required string Source { get; set; }
    public NotificationSeverity Severity { get; set; }
    public DateTime TimestampUtc { get; set; }
    public bool IsRead { get; set; }
    public string? ActionUrl { get; set; }
    public string? RelatedAccountId { get; set; }
}
