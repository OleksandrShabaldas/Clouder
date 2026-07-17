using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Clouder.Core.Email;
using Clouder.Core.Models;

namespace Clouder.Email;

public sealed class GmailEmailMonitor : IEmailMonitor
{
    // Must match the scopes requested by GoogleDriveProvider so the shared saved
    // token satisfies both — otherwise the auth library would force a re-consent.
    private static readonly string[] Scopes =
    [
        "https://www.googleapis.com/auth/drive",
        GmailService.Scope.GmailReadonly
    ];

    private static readonly IReadOnlyList<EmailAlertPattern> Patterns = AlertPatternLibrary.GetPatterns();

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _tokenStoragePath;

    public GmailEmailMonitor(string clientId, string clientSecret, string tokenStoragePath)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _tokenStoragePath = tokenStoragePath;
    }

    public async Task<IReadOnlyList<AppNotification>> CheckForAlertsAsync(
        EmailAccountConfig config, ProviderAccount account, CancellationToken ct = default)
    {
        if (config.Method != EmailAccessMethod.GmailApi)
            return [];

        var service = await CreateGmailServiceAsync(account.AccountId, ct);
        if (service == null)
            return [];

        var notifications = new List<AppNotification>();
        var senderFilter = string.Join(" OR ", AlertPatternLibrary.KnownCloudSenders.Select(s => $"from:{s}"));
        var since = config.LastCheckedUtc ?? DateTime.UtcNow.AddDays(-7);
        var afterEpoch = new DateTimeOffset(since).ToUnixTimeSeconds();
        var query = $"({senderFilter}) after:{afterEpoch}";

        var listRequest = service.Users.Messages.List("me");
        listRequest.Q = query;
        listRequest.MaxResults = 50;

        ListMessagesResponse response;
        try
        {
            response = await listRequest.ExecuteAsync(ct);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return
            [
                new AppNotification
                {
                    NotificationId = $"gmail-scope-{account.AccountId}",
                    Title = "Gmail access not authorized",
                    Body = "The Gmail API scope is not granted for this account. "
                         + "Disconnect and reconnect the account, or switch to IMAP monitoring.",
                    Source = "email-monitor",
                    Severity = NotificationSeverity.Warning,
                    TimestampUtc = DateTime.UtcNow,
                    IsRead = false,
                    RelatedAccountId = account.AccountId
                }
            ];
        }

        if (response.Messages == null)
            return [];

        foreach (var msgRef in response.Messages)
        {
            var msg = await service.Users.Messages.Get("me", msgRef.Id).ExecuteAsync(ct);
            var alert = MatchGmailMessage(msg, account);
            if (alert != null)
                notifications.Add(alert);
        }

        return notifications;
    }

    private async Task<GmailService?> CreateGmailServiceAsync(string accountId, CancellationToken ct)
    {
        var tokenPath = Path.Combine(_tokenStoragePath, accountId);
        if (!Directory.Exists(tokenPath))
            return null;

        var secrets = new ClientSecrets { ClientId = _clientId, ClientSecret = _clientSecret };
        var dataStore = new FileDataStore(tokenPath, true);

        try
        {
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets, Scopes, "user", ct, dataStore);

            return new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Clouder"
            });
        }
        catch
        {
            return null;
        }
    }

    private static AppNotification? MatchGmailMessage(Message msg, ProviderAccount account)
    {
        var headers = msg.Payload?.Headers ?? [];
        var from = headers.FirstOrDefault(h => h.Name == "From")?.Value ?? "";
        var subject = headers.FirstOrDefault(h => h.Name == "Subject")?.Value ?? "";
        var body = ExtractBody(msg);
        var date = DateTimeOffset.FromUnixTimeMilliseconds(msg.InternalDate ?? 0).UtcDateTime;

        foreach (var pattern in Patterns)
        {
            if (!pattern.SenderAddresses.Any(s => from.Contains(s, StringComparison.OrdinalIgnoreCase)))
                continue;

            bool subjectMatch = pattern.SubjectKeywords.Any(k =>
                subject.Contains(k, StringComparison.OrdinalIgnoreCase));
            bool bodyMatch = pattern.BodyKeywords.Any(k =>
                body.Contains(k, StringComparison.OrdinalIgnoreCase));

            if (!subjectMatch && !bodyMatch)
                continue;

            var id = $"gmail-{pattern.PatternId}-{msg.Id}";

            return new AppNotification
            {
                NotificationId = id,
                Title = $"[{pattern.Category}] {TruncateSubject(subject)}",
                Body = $"From: {from}\nDate: {date:g}\n\n{TruncateBody(body)}",
                Source = "email-monitor",
                Severity = pattern.Severity,
                TimestampUtc = date,
                IsRead = false,
                RelatedAccountId = account.AccountId
            };
        }

        return null;
    }

    private static string ExtractBody(Message msg)
    {
        if (msg.Payload?.Body?.Data != null)
            return DecodeBase64Url(msg.Payload.Body.Data);

        var parts = msg.Payload?.Parts;
        if (parts == null) return "";

        var textPart = parts.FirstOrDefault(p => p.MimeType == "text/plain")
                    ?? parts.FirstOrDefault(p => p.MimeType == "text/html");

        return textPart?.Body?.Data != null ? DecodeBase64Url(textPart.Body.Data) : "";
    }

    private static string DecodeBase64Url(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        var bytes = Convert.FromBase64String(padded);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static string TruncateSubject(string s) =>
        s.Length <= 120 ? s : s[..117] + "...";

    private static string TruncateBody(string s) =>
        s.Length <= 500 ? s : s[..497] + "...";
}
