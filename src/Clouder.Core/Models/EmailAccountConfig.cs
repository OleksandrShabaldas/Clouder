namespace Clouder.Core.Models;

public enum EmailAccessMethod
{
    Imap,
    GmailApi
}

public sealed class EmailAccountConfig
{
    public required string ConfigId { get; set; }
    public required string AccountId { get; set; }
    public EmailAccessMethod Method { get; set; }

    // IMAP
    public string? ImapHost { get; set; }
    public int ImapPort { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string? ImapUsername { get; set; }
    public byte[]? ImapPasswordProtected { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTime? LastCheckedUtc { get; set; }
    public int CheckIntervalMinutes { get; set; } = 30;
}
