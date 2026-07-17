using Clouder.Core.Models;

namespace Clouder.Core.Storage;

public interface IMetadataStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);

    // Items
    Task<CloudItem?> GetItemAsync(string itemId, CancellationToken ct = default);
    Task<IReadOnlyList<CloudItem>> GetChildrenAsync(string parentId, CancellationToken ct = default);
    Task<CloudItem> UpsertItemAsync(CloudItem item, CancellationToken ct = default);
    Task DeleteItemAsync(string itemId, CancellationToken ct = default);
    Task<IReadOnlyList<CloudItem>> GetItemsByAccountAsync(string accountId, CancellationToken ct = default);
    /// <summary>Items whose id starts with the given prefix (e.g. "poolId|folder\") — used for folder-level operations.</summary>
    Task<IReadOnlyList<CloudItem>> GetItemsByIdPrefixAsync(string idPrefix, CancellationToken ct = default);

    // Accounts
    Task<ProviderAccount?> GetAccountAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderAccount>> GetAllAccountsAsync(CancellationToken ct = default);
    Task<ProviderAccount> UpsertAccountAsync(ProviderAccount account, CancellationToken ct = default);
    Task DeleteAccountAsync(string accountId, CancellationToken ct = default);

    // Pools
    Task<StoragePool?> GetPoolAsync(string poolId, CancellationToken ct = default);
    Task<IReadOnlyList<StoragePool>> GetAllPoolsAsync(CancellationToken ct = default);
    Task<StoragePool> UpsertPoolAsync(StoragePool pool, CancellationToken ct = default);
    Task DeletePoolAsync(string poolId, CancellationToken ct = default);

    // Versions
    Task<IReadOnlyList<FileVersion>> GetFileVersionsAsync(string fileId, CancellationToken ct = default);
    Task<FileVersion> AddFileVersionAsync(FileVersion version, CancellationToken ct = default);

    // Stripe records (for files split across providers)
    Task<IReadOnlyList<StripePlan>> GetStripePlansAsync(string fileId, CancellationToken ct = default);
    Task SaveStripeePlansAsync(string fileId, IReadOnlyList<StripePlan> plans, CancellationToken ct = default);

    // Settings (key-value)
    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);
    Task SetSettingAsync(string key, string value, CancellationToken ct = default);

    // File rules
    Task<IReadOnlyList<FileRule>> GetFileRulesAsync(string? poolId = null, CancellationToken ct = default);
    Task<FileRule> UpsertFileRuleAsync(FileRule rule, CancellationToken ct = default);
    Task DeleteFileRuleAsync(string ruleId, CancellationToken ct = default);

    // Email configs
    Task<EmailAccountConfig?> GetEmailConfigAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyList<EmailAccountConfig>> GetAllEmailConfigsAsync(CancellationToken ct = default);
    Task<EmailAccountConfig> UpsertEmailConfigAsync(EmailAccountConfig config, CancellationToken ct = default);
    Task DeleteEmailConfigAsync(string configId, CancellationToken ct = default);

    // Notifications
    Task<IReadOnlyList<AppNotification>> GetNotificationsAsync(int limit = 50, bool unreadOnly = false, CancellationToken ct = default);
    Task<AppNotification> UpsertNotificationAsync(AppNotification notification, CancellationToken ct = default);
    Task MarkNotificationReadAsync(string notificationId, CancellationToken ct = default);
    Task MarkAllNotificationsReadAsync(CancellationToken ct = default);
    Task<int> GetUnreadCountAsync(CancellationToken ct = default);
}
