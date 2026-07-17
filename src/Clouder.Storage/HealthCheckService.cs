using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Providers;
using Clouder.Core.Storage;

namespace Clouder.Storage;

public sealed class HealthCheckService
{
    private readonly IMetadataStore _store;
    private readonly IProviderRegistry _providers;

    public HealthCheckService(IMetadataStore store, IProviderRegistry providers)
    {
        _store = store;
        _providers = providers;
    }

    public async Task<IReadOnlyList<AppNotification>> RunChecksAsync(CancellationToken ct = default)
    {
        var notifications = new List<AppNotification>();
        var accounts = await _store.GetAllAccountsAsync(ct);

        foreach (var account in accounts.Where(a => a.IsEnabled))
        {
            try
            {
                var provider = _providers.GetProvider(account.ProviderId);
                if (provider == null)
                {
                    notifications.Add(MakeNotification(
                        $"health-noprovider-{account.AccountId}",
                        $"Provider missing: {account.ProviderId}",
                        $"The provider for {account.DisplayName} is not registered. Reconnect the account.",
                        "system", NotificationSeverity.Warning, account.AccountId));
                    continue;
                }

                var quota = await provider.GetQuotaAsync(account.AccountId, ct);

                if (quota.TotalBytes > 0)
                {
                    double usagePercent = (double)quota.UsedBytes / quota.TotalBytes * 100;

                    if (usagePercent >= 95)
                    {
                        notifications.Add(MakeNotification(
                            $"health-quota95-{account.AccountId}",
                            $"Storage critical: {account.DisplayName}",
                            $"{usagePercent:F0}% used ({FormatBytes(quota.UsedBytes)} of {FormatBytes(quota.TotalBytes)}). "
                            + "Files may fail to upload. Free up space or remove this account from pools.",
                            account.ProviderId, NotificationSeverity.Critical, account.AccountId));
                    }
                    else if (usagePercent >= 80)
                    {
                        notifications.Add(MakeNotification(
                            $"health-quota80-{account.AccountId}",
                            $"Storage warning: {account.DisplayName}",
                            $"{usagePercent:F0}% used ({FormatBytes(quota.UsedBytes)} of {FormatBytes(quota.TotalBytes)}). "
                            + "Consider reorganizing files or adding another account.",
                            account.ProviderId, NotificationSeverity.Warning, account.AccountId));
                    }
                }
            }
            catch (Exception ex)
            {
                ClouderLog.Error($"Health check failed for {account.DisplayName}", ex);
                notifications.Add(MakeNotification(
                    $"health-error-{account.AccountId}",
                    $"Cannot reach: {account.DisplayName}",
                    $"Failed to check account status: {ex.Message}. "
                    + "The auth token may have expired — try reconnecting the account.",
                    account.ProviderId, NotificationSeverity.Warning, account.AccountId));
            }
        }

        return notifications;
    }

    private static AppNotification MakeNotification(
        string id, string title, string body, string source,
        NotificationSeverity severity, string? accountId) => new()
    {
        NotificationId = id,
        Title = title,
        Body = body,
        Source = source,
        Severity = severity,
        TimestampUtc = DateTime.UtcNow,
        IsRead = false,
        RelatedAccountId = accountId
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024 * 1024):F1} TB",
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F0} MB",
        _ => $"{bytes / 1024.0:F0} KB"
    };
}
