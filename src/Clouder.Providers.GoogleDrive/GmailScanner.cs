using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Clouder.Core.Models;

namespace Clouder.Providers.GoogleDrive;

public sealed class GmailScanner
{
    private static readonly string[] ScanScopes =
    [
        DriveService.Scope.Drive,
        GmailService.Scope.GmailReadonly
    ];

    private static readonly string[] AlertSenders =
    [
        "no-reply@accounts.google.com",
        "accounts-noreply@google.com",
        "drive-noreply@google.com",
        "googlecommunityteam-noreply@google.com",
        "no-reply@google.com",
        "no-reply@mega.nz",
        "support@mega.nz",
        "noreply@mega.nz"
    ];

    private static readonly string[] CriticalKeywords =
    [
        "security alert", "unusual activity", "unauthorized access",
        "suspicious sign-in", "blocked sign-in", "password changed",
        "account will be deleted", "account will be closed",
        "account suspended", "account disabled", "account terminated",
        "action required", "immediate action",
        "storage full", "storage limit reached", "running out of space"
    ];

    private static readonly string[] WarningKeywords =
    [
        "new sign-in", "new device", "verify your identity",
        "storage almost full", "approaching storage limit",
        "inactivity", "inactive account",
        "policy update", "terms of service",
        "review your account", "confirm your account"
    ];

    private readonly GoogleDriveSettings _settings;

    public GmailScanner(GoogleDriveSettings settings)
    {
        _settings = settings;
    }

    public async Task<IReadOnlyList<AppNotification>> ScanAccountAsync(
        string accountId, int daysBack = 7, CancellationToken ct = default)
    {
        var notifications = new List<AppNotification>();

        try
        {
            var service = await CreateGmailServiceAsync(accountId, ct);
            if (service == null) return notifications;

            var senderQuery = string.Join(" OR ", AlertSenders.Select(s => $"from:{s}"));
            var query = $"({senderQuery}) newer_than:{daysBack}d";

            var request = service.Users.Messages.List("me");
            request.Q = query;
            request.MaxResults = 25;

            var response = await request.ExecuteAsync(ct);
            if (response.Messages == null) return notifications;

            foreach (var stub in response.Messages)
            {
                ct.ThrowIfCancellationRequested();

                var msg = await service.Users.Messages.Get("me", stub.Id).ExecuteAsync(ct);
                var subject = GetHeader(msg, "Subject") ?? "";
                var from = GetHeader(msg, "From") ?? "";
                var date = GetHeader(msg, "Date");
                var snippet = msg.Snippet ?? "";

                var severity = ClassifySeverity(subject, snippet);
                if (severity == null) continue;

                var timestamp = DateTime.TryParse(date, out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow;

                notifications.Add(new AppNotification
                {
                    NotificationId = $"email-{accountId}-{stub.Id}",
                    Title = subject,
                    Body = snippet.Length > 300 ? snippet[..300] + "..." : snippet,
                    Source = DetectSource(from),
                    Severity = severity.Value,
                    TimestampUtc = timestamp,
                    IsRead = false,
                    ActionUrl = $"https://mail.google.com/mail/u/0/#inbox/{stub.Id}",
                    RelatedAccountId = accountId
                });
            }
        }
        catch (Exception)
        {
            // Gmail scope might not be granted — silently skip
        }

        return notifications;
    }

    /// <summary>
    /// Returns the scopes needed for email scanning (Drive + Gmail).
    /// Use these instead of the Drive-only scopes when email monitoring is enabled.
    /// </summary>
    public static string[] GetRequiredScopes() => ScanScopes;

    private async Task<GmailService?> CreateGmailServiceAsync(string accountId, CancellationToken ct)
    {
        var dataStore = new FileDataStore(
            Path.Combine(_settings.TokenStoragePath, accountId), true);

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            new ClientSecrets { ClientId = _settings.ClientId, ClientSecret = _settings.ClientSecret },
            ScanScopes, "user", ct, dataStore);

        return new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Clouder"
        });
    }

    private static string? GetHeader(Message msg, string name) =>
        msg.Payload?.Headers?.FirstOrDefault(h =>
            h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static NotificationSeverity? ClassifySeverity(string subject, string snippet)
    {
        var combined = $"{subject} {snippet}".ToLowerInvariant();

        if (CriticalKeywords.Any(k => combined.Contains(k)))
            return NotificationSeverity.Critical;
        if (WarningKeywords.Any(k => combined.Contains(k)))
            return NotificationSeverity.Warning;

        return null; // Not an alert we care about
    }

    private static string DetectSource(string from) => from.ToLowerInvariant() switch
    {
        var f when f.Contains("mega") => "mega",
        var f when f.Contains("google") || f.Contains("accounts") => "google",
        _ => "email"
    };
}
