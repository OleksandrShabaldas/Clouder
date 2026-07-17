using Clouder.Core.Email;
using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Storage;

namespace Clouder.Email;

public sealed class EmailCheckService : IDisposable
{
    private readonly IMetadataStore _store;
    private readonly ImapEmailMonitor _imapMonitor;
    private readonly Func<GmailEmailMonitor?> _gmailFactory;
    private Timer? _timer;
    private bool _running;

    public event Action<int>? UnreadCountChanged;

    public EmailCheckService(
        IMetadataStore store,
        ImapEmailMonitor imapMonitor,
        Func<GmailEmailMonitor?> gmailFactory)
    {
        _store = store;
        _imapMonitor = imapMonitor;
        _gmailFactory = gmailFactory;
    }

    public void Start(TimeSpan interval)
    {
        _timer = new Timer(_ => _ = RunCheckAsync(), null, TimeSpan.FromSeconds(30), interval);
        ClouderLog.Info($"Email monitoring started (interval: {interval.TotalMinutes:F0} min)");
    }

    public async Task RunCheckAsync()
    {
        if (_running) return;
        _running = true;

        try
        {
            var configs = await _store.GetAllEmailConfigsAsync();
            if (configs.Count == 0) return;

            var accounts = await _store.GetAllAccountsAsync();
            var accountMap = accounts.ToDictionary(a => a.AccountId);
            var newCount = 0;

            foreach (var config in configs.Where(c => c.IsEnabled))
            {
                if (!accountMap.TryGetValue(config.AccountId, out var account))
                    continue;

                if (config.LastCheckedUtc.HasValue &&
                    DateTime.UtcNow - config.LastCheckedUtc.Value < TimeSpan.FromMinutes(config.CheckIntervalMinutes))
                    continue;

                try
                {
                    IEmailMonitor? monitor = null;
                    if (config.Method == EmailAccessMethod.GmailApi)
                    {
                        monitor = _gmailFactory();
                        if (monitor == null)
                        {
                            await _store.UpsertNotificationAsync(new AppNotification
                            {
                                NotificationId = $"email-gmail-noauth-{config.AccountId}",
                                Title = $"Gmail API not available for {account.DisplayName}",
                                Body = "Gmail API monitoring requires Google OAuth credentials. "
                                     + "Switch to IMAP in Notifications → Configure Email Monitoring, "
                                     + "or reconnect the Google Drive account.",
                                Source = "email-monitor",
                                Severity = NotificationSeverity.Info,
                                TimestampUtc = DateTime.UtcNow,
                                IsRead = false,
                                RelatedAccountId = account.AccountId
                            });
                            config.LastCheckedUtc = DateTime.UtcNow;
                            await _store.UpsertEmailConfigAsync(config);
                            continue;
                        }
                    }
                    monitor ??= _imapMonitor;

                    var alerts = await monitor.CheckForAlertsAsync(config, account);

                    foreach (var alert in alerts)
                    {
                        await _store.UpsertNotificationAsync(alert);
                        newCount++;
                    }

                    config.LastCheckedUtc = DateTime.UtcNow;
                    await _store.UpsertEmailConfigAsync(config);

                    ClouderLog.Debug($"Email check complete for {account.DisplayName}: {alerts.Count} alert(s)");
                }
                catch (Exception ex)
                {
                    ClouderLog.Error($"Email check failed for {account.DisplayName}", ex);

                    await _store.UpsertNotificationAsync(new AppNotification
                    {
                        NotificationId = $"email-error-{config.AccountId}-{DateTime.UtcNow.Ticks}",
                        Title = $"Email check failed: {account.DisplayName}",
                        Body = $"Could not check emails for {account.Email}: {ex.Message}\n\n"
                             + "Check your IMAP settings or re-authorize the account.",
                        Source = "email-monitor",
                        Severity = NotificationSeverity.Warning,
                        TimestampUtc = DateTime.UtcNow,
                        IsRead = false,
                        RelatedAccountId = account.AccountId
                    });
                }
            }

            if (newCount > 0)
            {
                var unread = await _store.GetUnreadCountAsync();
                UnreadCountChanged?.Invoke(unread);
            }
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Email check service error", ex);
        }
        finally
        {
            _running = false;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
