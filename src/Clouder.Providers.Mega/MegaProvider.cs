using CG.Web.MegaApiClient;
using Newtonsoft.Json;
using Clouder.Core.Models;
using Clouder.Core.Providers;

namespace Clouder.Providers.Mega;

public sealed class MegaProvider : ICloudProvider
{
    private readonly MegaSettings _settings;
    private readonly Dictionary<string, MegaApiClient> _clients = new();

    public MegaProvider(MegaSettings settings)
    {
        _settings = settings;
    }

    public string ProviderId => "mega";
    public string DisplayName => "MEGA";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.BasicReadWrite
        | ProviderCapabilities.Move
        | ProviderCapabilities.Rename
        | ProviderCapabilities.ContentHashing;

    // ── Authentication ──────────────────────────────────────────────────

    public async Task<ProviderAccount> ConnectAccountAsync(CancellationToken ct = default)
    {
        if (_settings.CredentialPrompt == null)
            throw new InvalidOperationException(
                "Set MegaSettings.CredentialPrompt before calling ConnectAccountAsync, or use ConnectAccountAsync(email, password, ct) directly.");

        var (email, password) = await _settings.CredentialPrompt();
        return await ConnectAccountAsync(email, password, ct);
    }

    public async Task<ProviderAccount> ConnectAccountAsync(string email, string password, CancellationToken ct = default)
    {
        var accountId = $"mega-{Guid.NewGuid():N}";

        var client = new MegaApiClient();
        var authInfos = client.GenerateAuthInfos(email, password);
        await client.LoginAsync(authInfos);

        SaveSession(accountId, authInfos);
        _clients[accountId] = client;

        var accountInfo = await client.GetAccountInformationAsync();

        return new ProviderAccount
        {
            AccountId = accountId,
            ProviderId = ProviderId,
            DisplayName = email,
            Email = email,
            IsEnabled = true,
            ConnectedAtUtc = DateTime.UtcNow,
            Quota = new StorageQuota
            {
                TotalBytes = accountInfo.TotalQuota,
                UsedBytes = accountInfo.UsedQuota
            }
        };
    }

    /// <summary>
    /// Re-establishes a session for an EXISTING account id using email/password.
    /// Used to recover when a saved session has expired, without changing the account id.
    /// </summary>
    public async Task RestoreAsync(string accountId, string email, string password, CancellationToken ct = default)
    {
        var client = new MegaApiClient();
        var authInfos = client.GenerateAuthInfos(email, password);
        await client.LoginAsync(authInfos);
        SaveSession(accountId, authInfos);
        _clients[accountId] = client;
    }

    /// <summary>True if a saved session file exists for this account.</summary>
    public bool HasSession(string accountId) => File.Exists(GetSessionPath(accountId));

    public Task DisconnectAccountAsync(string accountId, CancellationToken ct = default)
    {
        if (_clients.TryGetValue(accountId, out var client))
        {
            client.Logout();
            _clients.Remove(accountId);
        }

        var sessionPath = GetSessionPath(accountId);
        if (File.Exists(sessionPath))
            File.Delete(sessionPath);

        var dir = Path.GetDirectoryName(sessionPath)!;
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            Directory.Delete(dir);

        return Task.CompletedTask;
    }

    public async Task<StorageQuota> GetQuotaAsync(string accountId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(accountId);
        var info = await client.GetAccountInformationAsync();
        return new StorageQuota
        {
            TotalBytes = info.TotalQuota,
            UsedBytes = info.UsedQuota
        };
    }

    // ── File operations ─────────────────────────────────────────────────

    public async Task<CloudItem?> GetItemAsync(string accountId, string remoteId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(accountId);
        var nodes = await client.GetNodesAsync();
        var node = nodes.FirstOrDefault(n => n.Id == remoteId);
        return node == null ? null : MapToCloudItem(node, accountId);
    }

    public async Task<IReadOnlyList<CloudItem>> ListFolderAsync(
        string accountId, string remoteFolderId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(accountId);
        var nodes = await client.GetNodesAsync();

        INode? parent;
        if (remoteFolderId is "root" or "")
            parent = nodes.Single(n => n.Type == NodeType.Root);
        else
            parent = nodes.FirstOrDefault(n => n.Id == remoteFolderId);

        if (parent == null)
            return [];

        return nodes
            .Where(n => n.ParentId == parent.Id && n.Type is NodeType.File or NodeType.Directory)
            .OrderByDescending(n => n.Type == NodeType.Directory)
            .ThenBy(n => n.Name)
            .Select(n => MapToCloudItem(n, accountId))
            .ToList();
    }

    public async Task<Stream> DownloadAsync(string accountId, string remoteId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(accountId);
        var node = await ResolveNodeAsync(client, remoteId);

        var tempPath = Path.GetTempFileName();
        await client.DownloadFileAsync(node, tempPath);

        return new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None, 4096,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    public async Task<Stream> DownloadRangeAsync(
        string accountId, string remoteId, long offset, long length, CancellationToken ct = default)
    {
        // MEGA doesn't support native range downloads.
        // Download to temp file and return a windowed stream.
        var client = await GetClientAsync(accountId);
        var node = await ResolveNodeAsync(client, remoteId);

        var tempPath = Path.GetTempFileName();
        await client.DownloadFileAsync(node, tempPath);

        var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None, 4096,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        stream.Seek(offset, SeekOrigin.Begin);
        return new BoundedStream(stream, length);
    }

    public async Task<CloudItem> UploadAsync(
        string accountId, string remoteFolderId, string fileName, Stream content, CancellationToken ct = default)
    {
        var client = await GetClientAsync(accountId);
        var parentNode = await ResolveNodeAsync(client, remoteFolderId);
        var uploaded = await client.UploadAsync(content, fileName, parentNode);
        return MapToCloudItem(uploaded, accountId);
    }

    public async Task DeleteAsync(string accountId, string remoteId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(accountId);
        var node = await ResolveNodeAsync(client, remoteId);
        await client.DeleteAsync(node, moveToTrash: false);
    }

    public async Task<CloudItem> CreateFolderAsync(
        string accountId, string parentRemoteId, string name, CancellationToken ct = default)
    {
        var client = await GetClientAsync(accountId);
        var parentNode = await ResolveNodeAsync(client, parentRemoteId);
        var folder = await client.CreateFolderAsync(name, parentNode);
        return MapToCloudItem(folder, accountId);
    }

    public async Task<CloudItem> MoveAsync(
        string accountId, string remoteId, string newParentRemoteId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(accountId);
        var node = await ResolveNodeAsync(client, remoteId);
        var dest = await ResolveNodeAsync(client, newParentRemoteId);
        var moved = await client.MoveAsync(node, dest);
        return MapToCloudItem(moved, accountId);
    }

    public async Task<CloudItem> RenameAsync(
        string accountId, string remoteId, string newName, CancellationToken ct = default)
    {
        var client = await GetClientAsync(accountId);
        var node = await ResolveNodeAsync(client, remoteId);
        var renamed = await client.RenameAsync(node, newName);
        return MapToCloudItem(renamed, accountId);
    }

    // ── Version history (not supported by MEGA) ─────────────────────────

    public Task<IReadOnlyList<FileVersion>> GetVersionsAsync(
        string accountId, string remoteId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FileVersion>>([]);

    public Task<Stream> DownloadVersionAsync(
        string accountId, string remoteId, string versionId, CancellationToken ct = default) =>
        throw new NotSupportedException("MEGA does not support file version history.");

    // ── Private helpers ─────────────────────────────────────────────────

    private async Task<MegaApiClient> GetClientAsync(string accountId)
    {
        if (_clients.TryGetValue(accountId, out var existing) && existing.IsLoggedIn)
            return existing;

        var client = new MegaApiClient();
        var authInfos = LoadSession(accountId)
            ?? throw new InvalidOperationException($"No saved session for MEGA account '{accountId}'. Reconnect the account.");

        await client.LoginAsync(authInfos);
        _clients[accountId] = client;
        return client;
    }

    private async Task<INode> ResolveNodeAsync(MegaApiClient client, string remoteId)
    {
        var nodes = await client.GetNodesAsync();

        if (remoteId is "root" or "")
            return nodes.Single(n => n.Type == NodeType.Root);

        return nodes.FirstOrDefault(n => n.Id == remoteId)
            ?? throw new FileNotFoundException($"MEGA node '{remoteId}' not found.");
    }

    // ── Session persistence ─────────────────────────────────────────────

    private string GetSessionPath(string accountId) =>
        Path.Combine(_settings.TokenStoragePath, accountId, "session.json");

    private void SaveSession(string accountId, MegaApiClient.AuthInfos authInfos)
    {
        var path = GetSessionPath(accountId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(authInfos));
    }

    private MegaApiClient.AuthInfos? LoadSession(string accountId)
    {
        var path = GetSessionPath(accountId);
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        return JsonConvert.DeserializeObject<MegaApiClient.AuthInfos>(json);
    }

    // ── Mapping ─────────────────────────────────────────────────────────

    private static CloudItem MapToCloudItem(INode node, string accountId)
    {
        var isFolder = node.Type == NodeType.Directory || node.Type == NodeType.Root;
        return new CloudItem
        {
            Id = node.Id,
            RemoteId = node.Id,
            ProviderId = "mega",
            AccountId = accountId,
            Name = node.Name ?? "Root",
            ParentId = null,
            Type = isFolder ? CloudItemType.Folder : CloudItemType.File,
            Size = isFolder ? 0 : node.Size,
            ContentHash = null,
            CreatedAtUtc = node.CreationDate ?? DateTime.UtcNow,
            ModifiedAtUtc = node.ModificationDate ?? node.CreationDate ?? DateTime.UtcNow
        };
    }
}

/// <summary>
/// Wraps a stream and limits reads to a maximum number of bytes.
/// Used for range downloads where MEGA doesn't support native byte ranges.
/// </summary>
internal sealed class BoundedStream : Stream
{
    private readonly Stream _inner;
    private long _remaining;

    public BoundedStream(Stream inner, long maxBytes)
    {
        _inner = inner;
        _remaining = maxBytes;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        int toRead = (int)Math.Min(count, _remaining);
        int read = _inner.Read(buffer, offset, toRead);
        _remaining -= read;
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (_remaining <= 0) return 0;
        int toRead = (int)Math.Min(count, _remaining);
        int read = await _inner.ReadAsync(buffer, offset, toRead, ct);
        _remaining -= read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
