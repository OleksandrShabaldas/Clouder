using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using Clouder.Core.Models;
using Clouder.Core.Providers;
using GDriveFile = Google.Apis.Drive.v3.Data.File;

namespace Clouder.Providers.GoogleDrive;

public sealed class GoogleDriveProvider : ICloudProvider
{
    // Drive access plus read-only Gmail so the same sign-in also powers email
    // alert monitoring. gmail.readonly is sensitive; users grant it at consent.
    private static readonly string[] Scopes =
    [
        DriveService.Scope.Drive,
        "https://www.googleapis.com/auth/gmail.readonly"
    ];
    private const string FolderMimeType = "application/vnd.google-apps.folder";

    private readonly GoogleDriveSettings _settings;
    private readonly ClientSecrets _clientSecrets;

    public GoogleDriveProvider(GoogleDriveSettings settings)
    {
        _settings = settings;
        _clientSecrets = new ClientSecrets
        {
            ClientId = settings.ClientId,
            ClientSecret = settings.ClientSecret
        };
    }

    public string ProviderId => "google-drive";
    public string DisplayName => "Google Drive";
    public ProviderCapabilities Capabilities => ProviderCapabilities.Full;

    // ── Authentication ──────────────────────────────────────────────────

    public async Task<ProviderAccount> ConnectAccountAsync(CancellationToken ct = default)
    {
        var accountId = $"gd-{Guid.NewGuid():N}";
        var dataStore = CreateDataStore(accountId);

        // Clear any stale tokens so the browser always shows account chooser
        await dataStore.ClearAsync();

        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            _clientSecrets, Scopes, "user", ct, dataStore);

        var service = CreateDriveService(credential);
        var about = await GetAboutAsync(service, ct);

        return new ProviderAccount
        {
            AccountId = accountId,
            ProviderId = ProviderId,
            DisplayName = about.User?.DisplayName ?? about.User?.EmailAddress ?? accountId,
            Email = about.User?.EmailAddress,
            IsEnabled = true,
            ConnectedAtUtc = DateTime.UtcNow,
            Quota = MapQuota(about)
        };
    }

    public Task DisconnectAccountAsync(string accountId, CancellationToken ct = default)
    {
        var tokenPath = Path.Combine(_settings.TokenStoragePath, accountId);
        if (Directory.Exists(tokenPath))
            Directory.Delete(tokenPath, true);
        return Task.CompletedTask;
    }

    public async Task<StorageQuota> GetQuotaAsync(string accountId, CancellationToken ct = default)
    {
        var service = await CreateAuthenticatedServiceAsync(accountId, ct);
        var about = await GetAboutAsync(service, ct);
        return MapQuota(about);
    }

    // ── File operations ─────────────────────────────────────────────────

    public async Task<CloudItem?> GetItemAsync(string accountId, string remoteId, CancellationToken ct = default)
    {
        var service = await CreateAuthenticatedServiceAsync(accountId, ct);
        var request = service.Files.Get(remoteId);
        request.Fields = "id,name,mimeType,size,createdTime,modifiedTime,md5Checksum,parents";

        try
        {
            var file = await request.ExecuteAsync(ct);
            return MapToCloudItem(file, accountId);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<CloudItem>> ListFolderAsync(
        string accountId, string remoteFolderId, CancellationToken ct = default)
    {
        var service = await CreateAuthenticatedServiceAsync(accountId, ct);
        var results = new List<CloudItem>();

        var listRequest = service.Files.List();
        listRequest.Q = $"'{remoteFolderId}' in parents and trashed = false";
        listRequest.Fields = "nextPageToken,files(id,name,mimeType,size,createdTime,modifiedTime,md5Checksum,parents)";
        listRequest.PageSize = 1000;
        listRequest.OrderBy = "folder,name";

        do
        {
            var response = await listRequest.ExecuteAsync(ct);
            if (response.Files != null)
            {
                foreach (var file in response.Files)
                    results.Add(MapToCloudItem(file, accountId));
            }
            listRequest.PageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(listRequest.PageToken));

        return results;
    }

    public async Task<Stream> DownloadAsync(string accountId, string remoteId, CancellationToken ct = default)
    {
        var service = await CreateAuthenticatedServiceAsync(accountId, ct);

        // Stream to temp file to avoid holding large files in memory
        var tempPath = Path.GetTempFileName();
        using (var fileStream = File.Create(tempPath))
        {
            var request = service.Files.Get(remoteId);
            var progress = await request.DownloadAsync(fileStream, ct);
            if (progress.Status != Google.Apis.Download.DownloadStatus.Completed)
                throw new IOException($"Download failed: {progress.Exception?.Message}");
        }

        return new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None, 4096,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    public async Task<Stream> DownloadRangeAsync(
        string accountId, string remoteId, long offset, long length, CancellationToken ct = default)
    {
        var service = await CreateAuthenticatedServiceAsync(accountId, ct);

        var url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(remoteId)}?alt=media";
        var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        httpRequest.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

        var response = await service.HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStreamAsync(ct);
    }

    public async Task<CloudItem> UploadAsync(
        string accountId, string remoteFolderId, string fileName, Stream content, CancellationToken ct = default)
    {
        var service = await CreateAuthenticatedServiceAsync(accountId, ct);

        var fileMetadata = new GDriveFile
        {
            Name = fileName,
            Parents = [remoteFolderId]
        };

        var request = service.Files.Create(fileMetadata, content, "application/octet-stream");
        request.Fields = "id,name,mimeType,size,createdTime,modifiedTime,md5Checksum";

        var result = await request.UploadAsync(ct);
        if (result.Status != UploadStatus.Completed)
            throw new IOException($"Upload failed: {result.Exception?.Message}");

        return MapToCloudItem(request.ResponseBody, accountId);
    }

    public async Task DeleteAsync(string accountId, string remoteId, CancellationToken ct = default)
    {
        var service = await CreateAuthenticatedServiceAsync(accountId, ct);
        await service.Files.Delete(remoteId).ExecuteAsync(ct);
    }

    public async Task<CloudItem> CreateFolderAsync(
        string accountId, string parentRemoteId, string name, CancellationToken ct = default)
    {
        var service = await CreateAuthenticatedServiceAsync(accountId, ct);

        var folderMetadata = new GDriveFile
        {
            Name = name,
            MimeType = FolderMimeType,
            Parents = [parentRemoteId]
        };

        var request = service.Files.Create(folderMetadata);
        request.Fields = "id,name,mimeType,size,createdTime,modifiedTime";
        var folder = await request.ExecuteAsync(ct);

        return MapToCloudItem(folder, accountId);
    }

    public async Task<CloudItem> MoveAsync(
        string accountId, string remoteId, string newParentRemoteId, CancellationToken ct = default)
    {
        var service = await CreateAuthenticatedServiceAsync(accountId, ct);

        // Get current parents to remove
        var getRequest = service.Files.Get(remoteId);
        getRequest.Fields = "parents";
        var file = await getRequest.ExecuteAsync(ct);
        var previousParents = string.Join(",", file.Parents ?? []);

        var updateRequest = service.Files.Update(new GDriveFile(), remoteId);
        updateRequest.AddParents = newParentRemoteId;
        updateRequest.RemoveParents = previousParents;
        updateRequest.Fields = "id,name,mimeType,size,createdTime,modifiedTime,md5Checksum";
        var updated = await updateRequest.ExecuteAsync(ct);

        return MapToCloudItem(updated, accountId);
    }

    public async Task<CloudItem> RenameAsync(
        string accountId, string remoteId, string newName, CancellationToken ct = default)
    {
        var service = await CreateAuthenticatedServiceAsync(accountId, ct);

        var updateRequest = service.Files.Update(new GDriveFile { Name = newName }, remoteId);
        updateRequest.Fields = "id,name,mimeType,size,createdTime,modifiedTime,md5Checksum";
        var updated = await updateRequest.ExecuteAsync(ct);

        return MapToCloudItem(updated, accountId);
    }

    // ── Version history ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<FileVersion>> GetVersionsAsync(
        string accountId, string remoteId, CancellationToken ct = default)
    {
        var service = await CreateAuthenticatedServiceAsync(accountId, ct);

        var request = service.Revisions.List(remoteId);
        request.Fields = "revisions(id,size,modifiedTime,lastModifyingUser(displayName))";

        try
        {
            var response = await request.ExecuteAsync(ct);
            return response.Revisions?.Select(r => new FileVersion
            {
                VersionId = r.Id,
                RemoteVersionId = r.Id,
                FileId = remoteId,
                Size = r.Size ?? 0,
                ModifiedAtUtc = r.ModifiedTimeDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow,
                ModifiedBy = r.LastModifyingUser?.DisplayName
            }).ToList() ?? [];
        }
        catch (Google.GoogleApiException)
        {
            // Revisions not available for this file type
            return [];
        }
    }

    public async Task<Stream> DownloadVersionAsync(
        string accountId, string remoteId, string versionId, CancellationToken ct = default)
    {
        var service = await CreateAuthenticatedServiceAsync(accountId, ct);

        var url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(remoteId)}/revisions/{Uri.EscapeDataString(versionId)}?alt=media";
        var response = await service.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStreamAsync(ct);
    }

    // ── Change detection ────────────────────────────────────────────────

    public async Task<RemoteChangeSet> GetChangesAsync(
        string accountId, string rootFolderId, string? cursor, CancellationToken ct = default)
    {
        var service = await CreateAuthenticatedServiceAsync(accountId, ct);

        // No cursor yet: establish one and report nothing. Anything already in
        // the folder was put there by us (or predates tracking) — importing it
        // wholesale on first run would be a surprise mass download.
        if (string.IsNullOrEmpty(cursor))
        {
            var start = await service.Changes.GetStartPageToken().ExecuteAsync(ct);
            return new RemoteChangeSet { Cursor = start.StartPageTokenValue };
        }

        var result = new RemoteChangeSet();
        var pageToken = cursor;

        while (!string.IsNullOrEmpty(pageToken))
        {
            var request = service.Changes.List(pageToken);
            request.Fields = "nextPageToken,newStartPageToken,changes(fileId,removed,file("
                           + "id,name,mimeType,size,createdTime,modifiedTime,md5Checksum,parents,trashed))";
            request.PageSize = 1000;
            request.IncludeRemoved = true;

            var response = await request.ExecuteAsync(ct);

            foreach (var change in response.Changes ?? [])
            {
                if (string.IsNullOrEmpty(change.FileId)) continue;

                // Removed from Drive entirely, moved to trash, or no longer visible.
                if (change.Removed == true || change.File == null || change.File.Trashed == true)
                {
                    result.Changes.Add(new RemoteChange
                    {
                        RemoteId = change.FileId,
                        Type = RemoteChangeType.Deleted
                    });
                    continue;
                }

                result.Changes.Add(new RemoteChange
                {
                    RemoteId = change.FileId,
                    Type = RemoteChangeType.Upserted,
                    Item = MapToCloudItem(change.File, accountId)
                });
            }

            if (!string.IsNullOrEmpty(response.NewStartPageToken))
            {
                // Last page — this is the cursor to resume from next time.
                result.Cursor = response.NewStartPageToken;
                break;
            }

            pageToken = response.NextPageToken;
        }

        result.Cursor ??= cursor;
        return result;
    }

    // ── Private helpers ─────────────────────────────────────────────────

    private async Task<DriveService> CreateAuthenticatedServiceAsync(string accountId, CancellationToken ct)
    {
        var dataStore = CreateDataStore(accountId);
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            _clientSecrets, Scopes, "user", ct, dataStore);
        return CreateDriveService(credential);
    }

    private static DriveService CreateDriveService(UserCredential credential) =>
        new(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Clouder"
        });

    private FileDataStore CreateDataStore(string accountId) =>
        new(Path.Combine(_settings.TokenStoragePath, accountId), true);

    private static async Task<Google.Apis.Drive.v3.Data.About> GetAboutAsync(DriveService service, CancellationToken ct)
    {
        var request = service.About.Get();
        request.Fields = "user(displayName,emailAddress),storageQuota(limit,usage)";
        return await request.ExecuteAsync(ct);
    }

    private static StorageQuota MapQuota(Google.Apis.Drive.v3.Data.About about) => new()
    {
        TotalBytes = about.StorageQuota?.Limit ?? 0,
        UsedBytes = about.StorageQuota?.Usage ?? 0
    };

    private static CloudItem MapToCloudItem(GDriveFile file, string accountId)
    {
        var isFolder = file.MimeType == FolderMimeType;
        return new CloudItem
        {
            Id = file.Id,
            RemoteId = file.Id,
            ProviderId = "google-drive",
            AccountId = accountId,
            Name = file.Name ?? "Untitled",
            // Raw remote parent — the sync layer resolves paths from this.
            ParentId = file.Parents?.FirstOrDefault(),
            Type = isFolder ? CloudItemType.Folder : CloudItemType.File,
            Size = file.Size ?? 0,
            ContentHash = file.Md5Checksum,
            CreatedAtUtc = file.CreatedTimeDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow,
            ModifiedAtUtc = file.ModifiedTimeDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow
        };
    }
}
