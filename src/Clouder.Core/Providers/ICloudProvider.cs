using Clouder.Core.Models;

namespace Clouder.Core.Providers;

public interface ICloudProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    ProviderCapabilities Capabilities { get; }

    Task<ProviderAccount> ConnectAccountAsync(CancellationToken ct = default);
    Task DisconnectAccountAsync(string accountId, CancellationToken ct = default);
    Task<StorageQuota> GetQuotaAsync(string accountId, CancellationToken ct = default);

    Task<CloudItem?> GetItemAsync(string accountId, string remoteId, CancellationToken ct = default);
    Task<IReadOnlyList<CloudItem>> ListFolderAsync(string accountId, string remoteFolderId, CancellationToken ct = default);

    Task<Stream> DownloadAsync(string accountId, string remoteId, CancellationToken ct = default);
    Task<Stream> DownloadRangeAsync(string accountId, string remoteId, long offset, long length, CancellationToken ct = default);
    Task<CloudItem> UploadAsync(string accountId, string remoteFolderId, string fileName, Stream content, CancellationToken ct = default);
    Task DeleteAsync(string accountId, string remoteId, CancellationToken ct = default);

    Task<CloudItem> CreateFolderAsync(string accountId, string parentRemoteId, string name, CancellationToken ct = default);
    Task<CloudItem> MoveAsync(string accountId, string remoteId, string newParentRemoteId, CancellationToken ct = default);
    Task<CloudItem> RenameAsync(string accountId, string remoteId, string newName, CancellationToken ct = default);

    Task<IReadOnlyList<FileVersion>> GetVersionsAsync(string accountId, string remoteId, CancellationToken ct = default);
    Task<Stream> DownloadVersionAsync(string accountId, string remoteId, string versionId, CancellationToken ct = default);
}
