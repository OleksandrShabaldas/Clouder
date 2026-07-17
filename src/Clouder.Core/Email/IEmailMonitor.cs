using Clouder.Core.Models;

namespace Clouder.Core.Email;

public interface IEmailMonitor
{
    Task<IReadOnlyList<AppNotification>> CheckForAlertsAsync(
        EmailAccountConfig config,
        ProviderAccount account,
        CancellationToken ct = default);
}
