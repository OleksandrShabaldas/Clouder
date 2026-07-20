using Clouder.Core.Models;
using Clouder.Core.Providers;

namespace Clouder.Storage.Tests;

/// <summary>
/// An in-memory cloud account with a real folder tree, used to exercise the sync
/// engine end to end. Can behave like Google Drive (incremental change feed) or
/// like MEGA (<see cref="SupportsIncremental"/> = false → full listings).
/// </summary>
internal sealed class InMemoryCloudProvider : ICloudProvider
{
    private sealed class Node
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public required string? ParentId { get; set; }
        public bool IsFolder { get; init; }
        public byte[] Content { get; set; } = [];
        public DateTime Created { get; init; } = DateTime.UtcNow;
        public DateTime Modified { get; set; } = DateTime.UtcNow;
    }

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly List<(long Seq, string RemoteId, bool Deleted)> _changeLog = [];
    private long _seq;
    private int _nextId = 1;

    public const string RootId = "root";

    /// <summary>Drive-style incremental changes when true; MEGA-style full listings when false.</summary>
    public bool SupportsIncremental { get; set; } = true;

    public bool FailNextUpload { get; set; }
    public List<string> DeletedRemoteIds { get; } = [];

    public InMemoryCloudProvider()
    {
        _nodes[RootId] = new Node { Id = RootId, Name = "root", ParentId = null, IsFolder = true };
    }

    public string ProviderId => "fake";
    public string DisplayName => "Fake Cloud";
    public ProviderCapabilities Capabilities => ProviderCapabilities.Full;

    // ── Test helpers ────────────────────────────────────────────────────

    /// <summary>Simulates someone adding/updating a file directly in the cloud.</summary>
    public string PutRemoteFile(string parentId, string name, string content, DateTime? modified = null)
    {
        var existing = _nodes.Values.FirstOrDefault(n =>
            n.ParentId == parentId && n.Name == name && !n.IsFolder);

        if (existing != null)
        {
            existing.Content = System.Text.Encoding.UTF8.GetBytes(content);
            existing.Modified = modified ?? DateTime.UtcNow;
            Record(existing.Id, deleted: false);
            return existing.Id;
        }

        var node = new Node
        {
            Id = $"r{_nextId++}",
            Name = name,
            ParentId = parentId,
            IsFolder = false,
            Content = System.Text.Encoding.UTF8.GetBytes(content),
            Modified = modified ?? DateTime.UtcNow
        };
        _nodes[node.Id] = node;
        Record(node.Id, deleted: false);
        return node.Id;
    }

    /// <summary>Simulates a folder created directly in the cloud.</summary>
    public string PutRemoteFolder(string parentId, string name)
    {
        var existing = _nodes.Values.FirstOrDefault(n =>
            n.ParentId == parentId && n.Name == name && n.IsFolder);
        if (existing != null) return existing.Id;

        var node = new Node { Id = $"f{_nextId++}", Name = name, ParentId = parentId, IsFolder = true };
        _nodes[node.Id] = node;
        Record(node.Id, deleted: false);
        return node.Id;
    }

    /// <summary>Simulates someone deleting a file directly in the cloud.</summary>
    public void RemoveRemote(string remoteId)
    {
        if (_nodes.Remove(remoteId))
            Record(remoteId, deleted: true);
    }

    public bool Exists(string remoteId) => _nodes.ContainsKey(remoteId);

    public string ReadContent(string remoteId) =>
        System.Text.Encoding.UTF8.GetString(_nodes[remoteId].Content);

    public string? FindByPath(string rootId, string relativePath)
    {
        var current = rootId;
        foreach (var part in relativePath.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = _nodes.Values.FirstOrDefault(n => n.ParentId == current && n.Name == part);
            if (next == null) return null;
            current = next.Id;
        }
        return current;
    }

    public int FileCountUnder(string rootId)
    {
        int count = 0;
        var queue = new Queue<string>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in _nodes.Values.Where(n => n.ParentId == parent))
            {
                if (child.IsFolder) queue.Enqueue(child.Id);
                else count++;
            }
        }
        return count;
    }

    private void Record(string remoteId, bool deleted)
    {
        _changeLog.Add((++_seq, remoteId, deleted));
    }

    // ── ICloudProvider ──────────────────────────────────────────────────

    public Task<StorageQuota> GetQuotaAsync(string accountId, CancellationToken ct = default) =>
        Task.FromResult(new StorageQuota { TotalBytes = 1L << 40, UsedBytes = 0 });

    public Task<CloudItem?> GetItemAsync(string accountId, string remoteId, CancellationToken ct = default) =>
        Task.FromResult(_nodes.TryGetValue(remoteId, out var n) ? Map(n, accountId) : null);

    public Task<IReadOnlyList<CloudItem>> ListFolderAsync(string accountId, string remoteFolderId, CancellationToken ct = default)
    {
        var children = _nodes.Values
            .Where(n => n.ParentId == remoteFolderId)
            .Select(n => Map(n, accountId))
            .ToList();
        return Task.FromResult<IReadOnlyList<CloudItem>>(children);
    }

    public Task<CloudItem> CreateFolderAsync(string accountId, string parentRemoteId, string name, CancellationToken ct = default)
    {
        var id = PutRemoteFolder(parentRemoteId, name);
        return Task.FromResult(Map(_nodes[id], accountId));
    }

    public Task<CloudItem> UploadAsync(string accountId, string remoteFolderId, string fileName, Stream content, CancellationToken ct = default)
    {
        if (FailNextUpload)
        {
            FailNextUpload = false;
            throw new IOException("simulated upload failure");
        }

        using var ms = new MemoryStream();
        content.CopyTo(ms);

        var node = new Node
        {
            Id = $"r{_nextId++}",
            Name = fileName,
            ParentId = remoteFolderId,
            IsFolder = false,
            Content = ms.ToArray(),
            Modified = DateTime.UtcNow
        };
        _nodes[node.Id] = node;
        Record(node.Id, deleted: false);
        return Task.FromResult(Map(node, accountId));
    }

    public Task<Stream> DownloadAsync(string accountId, string remoteId, CancellationToken ct = default)
    {
        if (!_nodes.TryGetValue(remoteId, out var node))
            throw new FileNotFoundException(remoteId);
        return Task.FromResult<Stream>(new MemoryStream(node.Content));
    }

    public Task DeleteAsync(string accountId, string remoteId, CancellationToken ct = default)
    {
        DeletedRemoteIds.Add(remoteId);
        RemoveRemote(remoteId);
        return Task.CompletedTask;
    }

    public Task<RemoteChangeSet> GetChangesAsync(
        string accountId, string rootFolderId, string? cursor, CancellationToken ct = default)
    {
        if (!SupportsIncremental)
        {
            // MEGA-style: everything currently under the root, deletions inferred.
            var set = new RemoteChangeSet { IsFullListing = true, Cursor = _seq.ToString() };
            var queue = new Queue<string>();
            queue.Enqueue(rootFolderId);
            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                foreach (var child in _nodes.Values.Where(n => n.ParentId == parent))
                {
                    set.Changes.Add(new RemoteChange
                    {
                        RemoteId = child.Id,
                        Type = RemoteChangeType.Upserted,
                        Item = Map(child, accountId)
                    });
                    if (child.IsFolder) queue.Enqueue(child.Id);
                }
            }
            return Task.FromResult(set);
        }

        // Drive-style: a null cursor only establishes a starting point.
        if (string.IsNullOrEmpty(cursor))
            return Task.FromResult(new RemoteChangeSet { Cursor = _seq.ToString() });

        var from = long.Parse(cursor);
        var result = new RemoteChangeSet { Cursor = _seq.ToString() };

        foreach (var (seq, remoteId, deleted) in _changeLog.Where(c => c.Seq > from))
        {
            if (deleted || !_nodes.ContainsKey(remoteId))
            {
                result.Changes.Add(new RemoteChange { RemoteId = remoteId, Type = RemoteChangeType.Deleted });
            }
            else
            {
                result.Changes.Add(new RemoteChange
                {
                    RemoteId = remoteId,
                    Type = RemoteChangeType.Upserted,
                    Item = Map(_nodes[remoteId], accountId)
                });
            }
        }

        return Task.FromResult(result);
    }

    private CloudItem Map(Node n, string accountId) => new()
    {
        Id = n.Id,
        RemoteId = n.Id,
        ProviderId = ProviderId,
        AccountId = accountId,
        Name = n.Name,
        ParentId = n.ParentId,
        Type = n.IsFolder ? CloudItemType.Folder : CloudItemType.File,
        Size = n.Content.Length,
        CreatedAtUtc = n.Created,
        ModifiedAtUtc = n.Modified
    };

    // ── Unused by these tests ───────────────────────────────────────────

    public Task<ProviderAccount> ConnectAccountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task DisconnectAccountAsync(string accountId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Stream> DownloadRangeAsync(string accountId, string remoteId, long offset, long length, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CloudItem> MoveAsync(string accountId, string remoteId, string newParentRemoteId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CloudItem> RenameAsync(string accountId, string remoteId, string newName, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<FileVersion>> GetVersionsAsync(string accountId, string remoteId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FileVersion>>([]);
    public Task<Stream> DownloadVersionAsync(string accountId, string remoteId, string versionId, CancellationToken ct = default) => throw new NotImplementedException();
}

/// <summary>Registry exposing a single provider (or none, to simulate a disconnected account).</summary>
internal sealed class SingleProviderRegistry(ICloudProvider? provider) : IProviderRegistry
{
    public ICloudProvider? GetProvider(string providerId) =>
        provider != null && provider.ProviderId == providerId ? provider : null;
    public IReadOnlyList<ICloudProvider> GetAllProviders() => provider == null ? [] : [provider];
    public void Register(ICloudProvider p) { }
}
