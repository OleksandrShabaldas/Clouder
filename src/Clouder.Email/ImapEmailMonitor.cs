using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Clouder.Core.Email;
using Clouder.Core.Models;

namespace Clouder.Email;

public sealed class ImapEmailMonitor : IEmailMonitor
{
    private static readonly IReadOnlyList<EmailAlertPattern> Patterns = AlertPatternLibrary.GetPatterns();

    public async Task<IReadOnlyList<AppNotification>> CheckForAlertsAsync(
        EmailAccountConfig config, ProviderAccount account, CancellationToken ct = default)
    {
        if (config.Method != EmailAccessMethod.Imap)
            return [];
        if (string.IsNullOrEmpty(config.ImapHost) || config.ImapPasswordProtected == null)
            return [];

        var password = CredentialProtector.Unprotect(config.ImapPasswordProtected);
        var username = config.ImapUsername ?? account.Email ?? "";
        var notifications = new List<AppNotification>();

        using var client = new ImapClient();
        await client.ConnectAsync(config.ImapHost, config.ImapPort, config.UseSsl, ct);
        await client.AuthenticateAsync(username, password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        var since = config.LastCheckedUtc?.AddMinutes(-5) ?? DateTime.UtcNow.AddDays(-7);
        var senderQuery = BuildSenderQuery();
        var dateQuery = SearchQuery.DeliveredAfter(since);
        var query = SearchQuery.And(dateQuery, senderQuery);

        var uids = await inbox.SearchAsync(query, ct);

        foreach (var uid in uids.Take(50))
        {
            var message = await inbox.GetMessageAsync(uid, ct);
            var alert = MatchMessage(message, account);
            if (alert != null)
                notifications.Add(alert);
        }

        await client.DisconnectAsync(true, ct);
        return notifications;
    }

    private static SearchQuery BuildSenderQuery()
    {
        SearchQuery? combined = null;
        foreach (var sender in AlertPatternLibrary.KnownCloudSenders)
        {
            var sq = SearchQuery.FromContains(sender);
            combined = combined == null ? sq : SearchQuery.Or(combined, sq);
        }
        return combined ?? SearchQuery.All;
    }

    private static AppNotification? MatchMessage(MimeMessage message, ProviderAccount account)
    {
        var from = message.From.Mailboxes.FirstOrDefault()?.Address ?? "";
        var subject = message.Subject ?? "";
        var bodyText = message.TextBody ?? message.HtmlBody ?? "";

        foreach (var pattern in Patterns)
        {
            if (!pattern.SenderAddresses.Any(s => from.Contains(s, StringComparison.OrdinalIgnoreCase)))
                continue;

            bool subjectMatch = pattern.SubjectKeywords.Any(k =>
                subject.Contains(k, StringComparison.OrdinalIgnoreCase));
            bool bodyMatch = pattern.BodyKeywords.Any(k =>
                bodyText.Contains(k, StringComparison.OrdinalIgnoreCase));

            if (!subjectMatch && !bodyMatch)
                continue;

            var id = $"email-{pattern.PatternId}-{message.MessageId ?? message.Date.UtcDateTime.Ticks.ToString()}";

            return new AppNotification
            {
                NotificationId = id,
                Title = $"[{pattern.Category}] {TruncateSubject(subject)}",
                Body = $"From: {from}\nDate: {message.Date.LocalDateTime:g}\n\n{TruncateBody(bodyText)}",
                Source = "email-monitor",
                Severity = pattern.Severity,
                TimestampUtc = message.Date.UtcDateTime,
                IsRead = false,
                RelatedAccountId = account.AccountId
            };
        }

        return null;
    }

    private static string TruncateSubject(string s) =>
        s.Length <= 120 ? s : s[..117] + "...";

    private static string TruncateBody(string s)
    {
        var clean = s.Replace("\r\n", "\n").Replace("\r", "\n");
        return clean.Length <= 500 ? clean : clean[..497] + "...";
    }
}
