using Clouder.Core.Models;

namespace Clouder.Core.Email;

public sealed class EmailAlertPattern
{
    public required string PatternId { get; set; }
    public required string[] SenderAddresses { get; set; }
    public required string[] SubjectKeywords { get; set; }
    public required string[] BodyKeywords { get; set; }
    public required NotificationSeverity Severity { get; set; }
    public required string Category { get; set; }
}
