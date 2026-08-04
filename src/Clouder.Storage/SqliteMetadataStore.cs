using Microsoft.Data.Sqlite;
using Clouder.Core.Models;
using Clouder.Core.Storage;

namespace Clouder.Storage;

public sealed class SqliteMetadataStore : IMetadataStore
{
    private readonly string _connectionString;

    public SqliteMetadataStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);

        await ExecuteNonQueryAsync(conn, "PRAGMA journal_mode = WAL", ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS accounts (
                account_id TEXT PRIMARY KEY,
                provider_id TEXT NOT NULL,
                display_name TEXT NOT NULL,
                email TEXT,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                connected_at_utc TEXT NOT NULL
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS pools (
                pool_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                local_path TEXT NOT NULL,
                mode INTEGER NOT NULL DEFAULT 0,
                default_strategy INTEGER NOT NULL DEFAULT 0
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS pool_members (
                pool_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                priority INTEGER NOT NULL DEFAULT 0,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                root_folder_id TEXT,
                PRIMARY KEY (pool_id, account_id),
                FOREIGN KEY (pool_id) REFERENCES pools(pool_id) ON DELETE CASCADE,
                FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS items (
                id TEXT PRIMARY KEY,
                remote_id TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                name TEXT NOT NULL,
                parent_id TEXT,
                type INTEGER NOT NULL,
                size INTEGER NOT NULL DEFAULT 0,
                content_hash TEXT,
                created_at_utc TEXT NOT NULL,
                modified_at_utc TEXT NOT NULL,
                FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_items_parent ON items(parent_id)", ct);
        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_items_account ON items(account_id)", ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS file_versions (
                version_id TEXT PRIMARY KEY,
                remote_version_id TEXT NOT NULL,
                file_id TEXT NOT NULL,
                size INTEGER NOT NULL DEFAULT 0,
                modified_at_utc TEXT NOT NULL,
                modified_by TEXT,
                FOREIGN KEY (file_id) REFERENCES items(id) ON DELETE CASCADE
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_versions_file ON file_versions(file_id)", ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS stripe_plans (
                file_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                account_id TEXT NOT NULL,
                offset_bytes INTEGER NOT NULL,
                length_bytes INTEGER NOT NULL,
                PRIMARY KEY (file_id, chunk_index),
                FOREIGN KEY (file_id) REFERENCES items(id) ON DELETE CASCADE,
                FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS file_rules (
                rule_id TEXT PRIMARY KEY,
                pool_id TEXT,
                name TEXT NOT NULL,
                type INTEGER NOT NULL,
                pattern TEXT NOT NULL,
                action INTEGER NOT NULL,
                target_account_id TEXT,
                target_provider_id TEXT,
                override_strategy INTEGER,
                priority INTEGER NOT NULL DEFAULT 0,
                is_enabled INTEGER NOT NULL DEFAULT 1
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_rules_pool ON file_rules(pool_id)", ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS notifications (
                notification_id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                source TEXT NOT NULL,
                severity INTEGER NOT NULL DEFAULT 0,
                timestamp_utc TEXT NOT NULL,
                is_read INTEGER NOT NULL DEFAULT 0,
                action_url TEXT,
                related_account_id TEXT
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_notif_time ON notifications(timestamp_utc DESC)", ct);

        // Add quota columns if missing (migration for existing databases)
        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS _migration_check (id INTEGER PRIMARY KEY)
            """, ct);
        try
        {
            await ExecuteNonQueryAsync(conn, "ALTER TABLE accounts ADD COLUMN quota_total_bytes INTEGER NOT NULL DEFAULT 0", ct);
            await ExecuteNonQueryAsync(conn, "ALTER TABLE accounts ADD COLUMN quota_used_bytes INTEGER NOT NULL DEFAULT 0", ct);
        }
        catch (SqliteException) { /* columns already exist */ }

        try
        {
            await ExecuteNonQueryAsync(conn, "ALTER TABLE stripe_plans ADD COLUMN remote_id TEXT", ct);
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            await ExecuteNonQueryAsync(conn, "ALTER TABLE items ADD COLUMN sync_state INTEGER NOT NULL DEFAULT 0", ct);
        }
        catch (SqliteException) { /* column already exists */ }

        try
        {
            await ExecuteNonQueryAsync(conn, "ALTER TABLE pool_members ADD COLUMN max_usage_bytes INTEGER NOT NULL DEFAULT 0", ct);
            await ExecuteNonQueryAsync(conn, "ALTER TABLE pool_members ADD COLUMN reserve_bytes INTEGER NOT NULL DEFAULT 0", ct);
        }
        catch (SqliteException) { /* columns already exist */ }

        try
        {
            await ExecuteNonQueryAsync(conn, "ALTER TABLE pool_members ADD COLUMN is_version_store INTEGER NOT NULL DEFAULT 0", ct);
            await ExecuteNonQueryAsync(conn, "ALTER TABLE pool_members ADD COLUMN exclude_from_files INTEGER NOT NULL DEFAULT 0", ct);
        }
        catch (SqliteException) { /* columns already exist */ }

        try
        {
            // The version policy is stored as JSON: it is a bag of optional settings that
            // gains fields over time, and a column per knob would mean a migration each time.
            await ExecuteNonQueryAsync(conn, "ALTER TABLE pools ADD COLUMN version_policy TEXT", ct);
        }
        catch (SqliteException) { /* column already exists */ }

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS conflicts (
                conflict_id TEXT PRIMARY KEY,
                pool_id TEXT NOT NULL,
                item_id TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                account_id TEXT NOT NULL,
                remote_id TEXT NOT NULL,
                local_modified_utc TEXT NOT NULL,
                remote_modified_utc TEXT NOT NULL,
                local_size INTEGER NOT NULL DEFAULT 0,
                remote_size INTEGER NOT NULL DEFAULT 0,
                detected_at_utc TEXT NOT NULL
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_conflicts_pool ON conflicts(pool_id)", ct);

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS transfers (
                transfer_id TEXT PRIMARY KEY,
                pool_id TEXT NOT NULL,
                account_id TEXT,
                file_name TEXT NOT NULL,
                relative_path TEXT,
                kind INTEGER NOT NULL,
                outcome INTEGER NOT NULL,
                bytes INTEGER NOT NULL DEFAULT 0,
                duration_ms INTEGER NOT NULL DEFAULT 0,
                timestamp_utc TEXT NOT NULL,
                error TEXT
            )
            """, ct);

        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_transfers_time ON transfers(timestamp_utc DESC)", ct);
        await ExecuteNonQueryAsync(conn, "CREATE INDEX IF NOT EXISTS idx_transfers_pool ON transfers(pool_id)", ct);

        try
        {
            await ExecuteNonQueryAsync(conn, "ALTER TABLE transfers ADD COLUMN item_id TEXT", ct);
            await ExecuteNonQueryAsync(conn, "ALTER TABLE transfers ADD COLUMN chunk_count INTEGER NOT NULL DEFAULT 0", ct);
            await ExecuteNonQueryAsync(conn, "ALTER TABLE transfers ADD COLUMN account_ids TEXT", ct);
        }
        catch (SqliteException) { /* columns already exist */ }

        try
        {
            await ExecuteNonQueryAsync(conn, "ALTER TABLE file_versions ADD COLUMN account_id TEXT", ct);
            await ExecuteNonQueryAsync(conn, "ALTER TABLE file_versions ADD COLUMN provider_id TEXT", ct);
            await ExecuteNonQueryAsync(conn, "ALTER TABLE file_versions ADD COLUMN version_number INTEGER NOT NULL DEFAULT 0", ct);
            await ExecuteNonQueryAsync(conn, "ALTER TABLE file_versions ADD COLUMN created_at_utc TEXT", ct);
            await ExecuteNonQueryAsync(conn, "ALTER TABLE file_versions ADD COLUMN chunk_manifest TEXT", ct);
        }
        catch (SqliteException) { /* columns already exist */ }

        await ExecuteNonQueryAsync(conn, """
            CREATE TABLE IF NOT EXISTS email_configs (
                config_id TEXT PRIMARY KEY,
                account_id TEXT NOT NULL UNIQUE,
                method INTEGER NOT NULL DEFAULT 0,
                imap_host TEXT,
                imap_port INTEGER NOT NULL DEFAULT 993,
                use_ssl INTEGER NOT NULL DEFAULT 1,
                imap_username TEXT,
                imap_password_protected BLOB,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                last_checked_utc TEXT,
                check_interval_minutes INTEGER NOT NULL DEFAULT 30,
                FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
            )
            """, ct);
    }

    // ── Items ────────────────────────────────────────────────────────────

    public async Task<CloudItem?> GetItemAsync(string itemId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadItem(reader) : null;
    }

    public async Task<IReadOnlyList<CloudItem>> GetChildrenAsync(string parentId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM items WHERE parent_id = @parentId ORDER BY type DESC, name";
        cmd.Parameters.AddWithValue("@parentId", parentId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<CloudItem>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadItem(reader));
        return results;
    }

    public async Task<CloudItem> UpsertItemAsync(CloudItem item, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO items (id, remote_id, provider_id, account_id, name, parent_id, type, size, content_hash, created_at_utc, modified_at_utc, sync_state)
            VALUES (@id, @remoteId, @providerId, @accountId, @name, @parentId, @type, @size, @hash, @created, @modified, @syncState)
            ON CONFLICT(id) DO UPDATE SET
                remote_id = excluded.remote_id,
                provider_id = excluded.provider_id,
                account_id = excluded.account_id,
                name = excluded.name,
                parent_id = excluded.parent_id,
                type = excluded.type,
                size = excluded.size,
                content_hash = excluded.content_hash,
                modified_at_utc = excluded.modified_at_utc,
                sync_state = excluded.sync_state
            """;
        cmd.Parameters.AddWithValue("@id", item.Id);
        cmd.Parameters.AddWithValue("@remoteId", item.RemoteId);
        cmd.Parameters.AddWithValue("@providerId", item.ProviderId);
        cmd.Parameters.AddWithValue("@accountId", item.AccountId);
        cmd.Parameters.AddWithValue("@name", item.Name);
        cmd.Parameters.AddWithValue("@parentId", (object?)item.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@type", (int)item.Type);
        cmd.Parameters.AddWithValue("@size", item.Size);
        cmd.Parameters.AddWithValue("@hash", (object?)item.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", item.CreatedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@modified", item.ModifiedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@syncState", (int)item.SyncState);

        await cmd.ExecuteNonQueryAsync(ct);
        return item;
    }

    public async Task DeleteItemAsync(string itemId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<CloudItem>> GetItemsByAccountAsync(string accountId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM items WHERE account_id = @accountId ORDER BY size DESC";
        cmd.Parameters.AddWithValue("@accountId", accountId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<CloudItem>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadItem(reader));
        return results;
    }

    public async Task<IReadOnlyList<CloudItem>> GetItemsByIdPrefixAsync(string idPrefix, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // substr comparison instead of LIKE so %, _ and \ in file names need no escaping
        cmd.CommandText = "SELECT * FROM items WHERE substr(id, 1, @len) = @prefix ORDER BY id";
        cmd.Parameters.AddWithValue("@len", idPrefix.Length);
        cmd.Parameters.AddWithValue("@prefix", idPrefix);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<CloudItem>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadItem(reader));
        return results;
    }

    public async Task<long> GetPoolUsageOnAccountAsync(string poolId, string accountId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Only this pool's files on this account — the account may hold plenty else.
        cmd.CommandText = """
            SELECT COALESCE(SUM(size), 0) FROM items
            WHERE account_id = @accountId AND substr(id, 1, @len) = @prefix
            """;
        var prefix = poolId + "|";
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@len", prefix.Length);
        cmd.Parameters.AddWithValue("@prefix", prefix);

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<CloudItem?> GetItemByRemoteIdAsync(string accountId, string remoteId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM items WHERE account_id = @accountId AND remote_id = @remoteId LIMIT 1";
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@remoteId", remoteId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadItem(reader) : null;
    }

    // ── Accounts ─────────────────────────────────────────────────────────

    public async Task<ProviderAccount?> GetAccountAsync(string accountId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM accounts WHERE account_id = @id";
        cmd.Parameters.AddWithValue("@id", accountId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAccount(reader) : null;
    }

    public async Task<IReadOnlyList<ProviderAccount>> GetAllAccountsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM accounts ORDER BY display_name";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<ProviderAccount>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadAccount(reader));
        return results;
    }

    public async Task<ProviderAccount> UpsertAccountAsync(ProviderAccount account, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO accounts (account_id, provider_id, display_name, email, is_enabled, connected_at_utc, quota_total_bytes, quota_used_bytes)
            VALUES (@id, @providerId, @name, @email, @enabled, @connected, @quotaTotal, @quotaUsed)
            ON CONFLICT(account_id) DO UPDATE SET
                provider_id = excluded.provider_id,
                display_name = excluded.display_name,
                email = excluded.email,
                is_enabled = excluded.is_enabled,
                quota_total_bytes = excluded.quota_total_bytes,
                quota_used_bytes = excluded.quota_used_bytes
            """;
        cmd.Parameters.AddWithValue("@id", account.AccountId);
        cmd.Parameters.AddWithValue("@providerId", account.ProviderId);
        cmd.Parameters.AddWithValue("@name", account.DisplayName);
        cmd.Parameters.AddWithValue("@email", (object?)account.Email ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled", account.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@connected", account.ConnectedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@quotaTotal", account.Quota?.TotalBytes ?? 0);
        cmd.Parameters.AddWithValue("@quotaUsed", account.Quota?.UsedBytes ?? 0);

        await cmd.ExecuteNonQueryAsync(ct);
        return account;
    }

    public async Task DeleteAccountAsync(string accountId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM accounts WHERE account_id = @id";
        cmd.Parameters.AddWithValue("@id", accountId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Pools ────────────────────────────────────────────────────────────

    public async Task<StoragePool?> GetPoolAsync(string poolId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM pools WHERE pool_id = @id";
        cmd.Parameters.AddWithValue("@id", poolId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var pool = ReadPool(reader);
        pool.Members = await LoadPoolMembersAsync(conn, poolId, ct);
        return pool;
    }

    public async Task<IReadOnlyList<StoragePool>> GetAllPoolsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM pools ORDER BY name";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var pools = new List<StoragePool>();
        while (await reader.ReadAsync(ct))
            pools.Add(ReadPool(reader));

        foreach (var pool in pools)
            pool.Members = await LoadPoolMembersAsync(conn, pool.PoolId, ct);

        return pools;
    }

    public async Task<StoragePool> UpsertPoolAsync(StoragePool pool, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var tx = conn.BeginTransaction();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO pools (pool_id, name, local_path, mode, default_strategy, version_policy)
                VALUES (@id, @name, @path, @mode, @strategy, @versionPolicy)
                ON CONFLICT(pool_id) DO UPDATE SET
                    name = excluded.name,
                    local_path = excluded.local_path,
                    mode = excluded.mode,
                    default_strategy = excluded.default_strategy,
                    version_policy = excluded.version_policy
                """;
            cmd.Parameters.AddWithValue("@id", pool.PoolId);
            cmd.Parameters.AddWithValue("@name", pool.Name);
            cmd.Parameters.AddWithValue("@path", pool.LocalPath);
            cmd.Parameters.AddWithValue("@versionPolicy",
                System.Text.Json.JsonSerializer.Serialize(pool.VersionPolicy));
            cmd.Parameters.AddWithValue("@mode", (int)pool.Mode);
            cmd.Parameters.AddWithValue("@strategy", (int)pool.DefaultStrategy);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM pool_members WHERE pool_id = @id";
            cmd.Parameters.AddWithValue("@id", pool.PoolId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var member in pool.Members)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO pool_members (pool_id, account_id, provider_id, priority, is_enabled, root_folder_id,
                                          max_usage_bytes, reserve_bytes, is_version_store, exclude_from_files)
                VALUES (@poolId, @accountId, @providerId, @priority, @enabled, @rootFolder,
                        @maxUsage, @reserve, @versionStore, @excludeFiles)
                """;
            cmd.Parameters.AddWithValue("@poolId", pool.PoolId);
            cmd.Parameters.AddWithValue("@accountId", member.AccountId);
            cmd.Parameters.AddWithValue("@providerId", member.ProviderId);
            cmd.Parameters.AddWithValue("@priority", member.Priority);
            cmd.Parameters.AddWithValue("@enabled", member.IsEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@rootFolder", (object?)member.RootFolderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@maxUsage", member.MaxUsageBytes);
            cmd.Parameters.AddWithValue("@reserve", member.ReserveBytes);
            cmd.Parameters.AddWithValue("@versionStore", member.IsVersionStore ? 1 : 0);
            cmd.Parameters.AddWithValue("@excludeFiles", member.ExcludeFromFilePlacement ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
        return pool;
    }

    public async Task DeletePoolAsync(string poolId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pools WHERE pool_id = @id";
        cmd.Parameters.AddWithValue("@id", poolId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Versions ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FileVersion>> GetFileVersionsAsync(string fileId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_versions WHERE file_id = @fileId ORDER BY modified_at_utc DESC";
        cmd.Parameters.AddWithValue("@fileId", fileId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<FileVersion>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadFileVersion(reader));
        return results;
    }

    public async Task<FileVersion> AddFileVersionAsync(FileVersion version, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO file_versions (version_id, remote_version_id, file_id, size, modified_at_utc, modified_by,
                                       account_id, provider_id, version_number, created_at_utc, chunk_manifest)
            VALUES (@id, @remoteId, @fileId, @size, @modified, @modifiedBy,
                    @accountId, @providerId, @versionNumber, @created, @manifest)
            """;
        cmd.Parameters.AddWithValue("@id", version.VersionId);
        cmd.Parameters.AddWithValue("@remoteId", version.RemoteVersionId);
        cmd.Parameters.AddWithValue("@fileId", version.FileId);
        cmd.Parameters.AddWithValue("@size", version.Size);
        cmd.Parameters.AddWithValue("@modified", version.ModifiedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@modifiedBy", (object?)version.ModifiedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@accountId", (object?)version.AccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@providerId", (object?)version.ProviderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@versionNumber", version.VersionNumber);
        cmd.Parameters.AddWithValue("@created", version.CreatedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@manifest", (object?)version.ChunkManifest ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
        return version;
    }

    public async Task<FileVersion?> GetFileVersionAsync(string versionId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_versions WHERE version_id = @id";
        cmd.Parameters.AddWithValue("@id", versionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadFileVersion(reader) : null;
    }

    public async Task DeleteFileVersionAsync(string versionId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM file_versions WHERE version_id = @id";
        cmd.Parameters.AddWithValue("@id", versionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Every retained version across all files — used to prune by age.</summary>
    public async Task<IReadOnlyList<FileVersion>> GetAllFileVersionsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM file_versions ORDER BY created_at_utc DESC";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<FileVersion>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadFileVersion(reader));
        return results;
    }

    // ── Stripe Plans ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<StripePlan>> GetStripePlansAsync(string fileId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM stripe_plans WHERE file_id = @fileId ORDER BY chunk_index";
        cmd.Parameters.AddWithValue("@fileId", fileId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<StripePlan>();
        while (await reader.ReadAsync(ct))
        {
            var remoteIdOrd = reader.GetOrdinal("remote_id");
            results.Add(new StripePlan
            {
                AccountId = reader.GetString(reader.GetOrdinal("account_id")),
                ChunkIndex = reader.GetInt32(reader.GetOrdinal("chunk_index")),
                Offset = reader.GetInt64(reader.GetOrdinal("offset_bytes")),
                Length = reader.GetInt64(reader.GetOrdinal("length_bytes")),
                RemoteId = reader.IsDBNull(remoteIdOrd) ? null : reader.GetString(remoteIdOrd)
            });
        }
        return results;
    }

    public async Task SaveStripeePlansAsync(string fileId, IReadOnlyList<StripePlan> plans, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var tx = conn.BeginTransaction();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM stripe_plans WHERE file_id = @fileId";
            cmd.Parameters.AddWithValue("@fileId", fileId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var plan in plans)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO stripe_plans (file_id, chunk_index, account_id, offset_bytes, length_bytes, remote_id)
                VALUES (@fileId, @index, @accountId, @offset, @length, @remoteId)
                """;
            cmd.Parameters.AddWithValue("@fileId", fileId);
            cmd.Parameters.AddWithValue("@index", plan.ChunkIndex);
            cmd.Parameters.AddWithValue("@accountId", plan.AccountId);
            cmd.Parameters.AddWithValue("@offset", plan.Offset);
            cmd.Parameters.AddWithValue("@length", plan.Length);
            cmd.Parameters.AddWithValue("@remoteId", (object?)plan.RemoteId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
    }

    // ── Settings ─────────────────────────────────────────────────────────

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── File Rules ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FileRule>> GetFileRulesAsync(string? poolId = null, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = poolId == null
            ? "SELECT * FROM file_rules ORDER BY priority"
            : "SELECT * FROM file_rules WHERE pool_id = @poolId OR pool_id IS NULL ORDER BY priority";
        if (poolId != null)
            cmd.Parameters.AddWithValue("@poolId", poolId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<FileRule>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadFileRule(reader));
        return results;
    }

    public async Task<FileRule> UpsertFileRuleAsync(FileRule rule, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO file_rules (rule_id, pool_id, name, type, pattern, action, target_account_id, target_provider_id, override_strategy, priority, is_enabled)
            VALUES (@id, @poolId, @name, @type, @pattern, @action, @targetAccount, @targetProvider, @strategy, @priority, @enabled)
            ON CONFLICT(rule_id) DO UPDATE SET
                pool_id = excluded.pool_id,
                name = excluded.name,
                type = excluded.type,
                pattern = excluded.pattern,
                action = excluded.action,
                target_account_id = excluded.target_account_id,
                target_provider_id = excluded.target_provider_id,
                override_strategy = excluded.override_strategy,
                priority = excluded.priority,
                is_enabled = excluded.is_enabled
            """;
        cmd.Parameters.AddWithValue("@id", rule.RuleId);
        cmd.Parameters.AddWithValue("@poolId", (object?)rule.PoolId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@name", rule.Name);
        cmd.Parameters.AddWithValue("@type", (int)rule.Type);
        cmd.Parameters.AddWithValue("@pattern", rule.Pattern);
        cmd.Parameters.AddWithValue("@action", (int)rule.Action);
        cmd.Parameters.AddWithValue("@targetAccount", (object?)rule.TargetAccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@targetProvider", (object?)rule.TargetProviderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@strategy", rule.OverrideStrategy.HasValue ? (int)rule.OverrideStrategy.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@priority", rule.Priority);
        cmd.Parameters.AddWithValue("@enabled", rule.IsEnabled ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
        return rule;
    }

    public async Task DeleteFileRuleAsync(string ruleId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM file_rules WHERE rule_id = @id";
        cmd.Parameters.AddWithValue("@id", ruleId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Email Configs ─────────────────────────────────────────────────────

    public async Task<EmailAccountConfig?> GetEmailConfigAsync(string accountId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM email_configs WHERE account_id = @accountId";
        cmd.Parameters.AddWithValue("@accountId", accountId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEmailConfig(reader) : null;
    }

    public async Task<IReadOnlyList<EmailAccountConfig>> GetAllEmailConfigsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM email_configs ORDER BY account_id";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<EmailAccountConfig>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadEmailConfig(reader));
        return results;
    }

    public async Task<EmailAccountConfig> UpsertEmailConfigAsync(EmailAccountConfig config, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO email_configs (config_id, account_id, method, imap_host, imap_port, use_ssl,
                imap_username, imap_password_protected, is_enabled, last_checked_utc, check_interval_minutes)
            VALUES (@id, @accountId, @method, @host, @port, @ssl, @user, @pass, @enabled, @lastChecked, @interval)
            ON CONFLICT(config_id) DO UPDATE SET
                account_id = excluded.account_id,
                method = excluded.method,
                imap_host = excluded.imap_host,
                imap_port = excluded.imap_port,
                use_ssl = excluded.use_ssl,
                imap_username = excluded.imap_username,
                imap_password_protected = excluded.imap_password_protected,
                is_enabled = excluded.is_enabled,
                last_checked_utc = excluded.last_checked_utc,
                check_interval_minutes = excluded.check_interval_minutes
            """;
        cmd.Parameters.AddWithValue("@id", config.ConfigId);
        cmd.Parameters.AddWithValue("@accountId", config.AccountId);
        cmd.Parameters.AddWithValue("@method", (int)config.Method);
        cmd.Parameters.AddWithValue("@host", (object?)config.ImapHost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@port", config.ImapPort);
        cmd.Parameters.AddWithValue("@ssl", config.UseSsl ? 1 : 0);
        cmd.Parameters.AddWithValue("@user", (object?)config.ImapUsername ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pass", (object?)config.ImapPasswordProtected ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled", config.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@lastChecked", config.LastCheckedUtc.HasValue ? config.LastCheckedUtc.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("@interval", config.CheckIntervalMinutes);

        await cmd.ExecuteNonQueryAsync(ct);
        return config;
    }

    public async Task DeleteEmailConfigAsync(string configId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM email_configs WHERE config_id = @id";
        cmd.Parameters.AddWithValue("@id", configId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Conflicts ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FileConflict>> GetConflictsAsync(string? poolId = null, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = poolId == null
            ? "SELECT * FROM conflicts ORDER BY detected_at_utc DESC"
            : "SELECT * FROM conflicts WHERE pool_id = @poolId ORDER BY detected_at_utc DESC";
        if (poolId != null)
            cmd.Parameters.AddWithValue("@poolId", poolId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<FileConflict>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadConflict(reader));
        return results;
    }

    public async Task<FileConflict?> GetConflictAsync(string conflictId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM conflicts WHERE conflict_id = @id";
        cmd.Parameters.AddWithValue("@id", conflictId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadConflict(reader) : null;
    }

    public async Task<FileConflict> UpsertConflictAsync(FileConflict conflict, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conflicts (conflict_id, pool_id, item_id, relative_path, account_id, remote_id,
                                   local_modified_utc, remote_modified_utc, local_size, remote_size, detected_at_utc)
            VALUES (@id, @poolId, @itemId, @path, @accountId, @remoteId,
                    @localMod, @remoteMod, @localSize, @remoteSize, @detected)
            ON CONFLICT(conflict_id) DO UPDATE SET
                local_modified_utc = excluded.local_modified_utc,
                remote_modified_utc = excluded.remote_modified_utc,
                local_size = excluded.local_size,
                remote_size = excluded.remote_size,
                remote_id = excluded.remote_id,
                detected_at_utc = excluded.detected_at_utc
            """;
        cmd.Parameters.AddWithValue("@id", conflict.ConflictId);
        cmd.Parameters.AddWithValue("@poolId", conflict.PoolId);
        cmd.Parameters.AddWithValue("@itemId", conflict.ItemId);
        cmd.Parameters.AddWithValue("@path", conflict.RelativePath);
        cmd.Parameters.AddWithValue("@accountId", conflict.AccountId);
        cmd.Parameters.AddWithValue("@remoteId", conflict.RemoteId);
        cmd.Parameters.AddWithValue("@localMod", conflict.LocalModifiedUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@remoteMod", conflict.RemoteModifiedUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@localSize", conflict.LocalSize);
        cmd.Parameters.AddWithValue("@remoteSize", conflict.RemoteSize);
        cmd.Parameters.AddWithValue("@detected", conflict.DetectedAtUtc.ToString("o"));

        await cmd.ExecuteNonQueryAsync(ct);
        return conflict;
    }

    public async Task DeleteConflictAsync(string conflictId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM conflicts WHERE conflict_id = @id";
        cmd.Parameters.AddWithValue("@id", conflictId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static FileConflict ReadConflict(SqliteDataReader reader) => new()
    {
        ConflictId = reader.GetString(reader.GetOrdinal("conflict_id")),
        PoolId = reader.GetString(reader.GetOrdinal("pool_id")),
        ItemId = reader.GetString(reader.GetOrdinal("item_id")),
        RelativePath = reader.GetString(reader.GetOrdinal("relative_path")),
        AccountId = reader.GetString(reader.GetOrdinal("account_id")),
        RemoteId = reader.GetString(reader.GetOrdinal("remote_id")),
        LocalModifiedUtc = ParseUtc(reader.GetString(reader.GetOrdinal("local_modified_utc"))),
        RemoteModifiedUtc = ParseUtc(reader.GetString(reader.GetOrdinal("remote_modified_utc"))),
        LocalSize = reader.GetInt64(reader.GetOrdinal("local_size")),
        RemoteSize = reader.GetInt64(reader.GetOrdinal("remote_size")),
        DetectedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("detected_at_utc")))
    };

    // ── Transfer history ─────────────────────────────────────────────────

    public async Task AddTransferAsync(TransferRecord record, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO transfers (transfer_id, pool_id, account_id, file_name, relative_path,
                                   kind, outcome, bytes, duration_ms, timestamp_utc, error,
                                   item_id, chunk_count, account_ids)
            VALUES (@id, @poolId, @accountId, @fileName, @path,
                    @kind, @outcome, @bytes, @duration, @timestamp, @error,
                    @itemId, @chunkCount, @accountIds)
            ON CONFLICT(transfer_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@id", record.TransferId);
        cmd.Parameters.AddWithValue("@poolId", record.PoolId);
        cmd.Parameters.AddWithValue("@accountId", (object?)record.AccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fileName", record.FileName);
        cmd.Parameters.AddWithValue("@path", (object?)record.RelativePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@kind", (int)record.Kind);
        cmd.Parameters.AddWithValue("@outcome", (int)record.Outcome);
        cmd.Parameters.AddWithValue("@bytes", record.Bytes);
        cmd.Parameters.AddWithValue("@duration", record.DurationMs);
        cmd.Parameters.AddWithValue("@timestamp", record.TimestampUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@error", (object?)record.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@itemId", (object?)record.ItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@chunkCount", record.ChunkCount);
        cmd.Parameters.AddWithValue("@accountIds", (object?)record.AccountIds ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Deletes transfer history. Pass true to keep entries that still need attention.</summary>
    public async Task ClearTransfersAsync(bool onlyCompleted = false, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = onlyCompleted
            ? "DELETE FROM transfers WHERE outcome = @success"
            : "DELETE FROM transfers";
        if (onlyCompleted)
            cmd.Parameters.AddWithValue("@success", (int)TransferOutcome.Success);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<TransferRecord>> GetRecentTransfersAsync(
        int limit = 50, string? poolId = null, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = poolId == null
            ? "SELECT * FROM transfers ORDER BY timestamp_utc DESC LIMIT @limit"
            : "SELECT * FROM transfers WHERE pool_id = @poolId ORDER BY timestamp_utc DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);
        if (poolId != null) cmd.Parameters.AddWithValue("@poolId", poolId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<TransferRecord>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadTransfer(reader));
        return results;
    }

    public async Task<TransferStats> GetTransferStatsAsync(DateTime sinceUtc, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT kind, outcome, COUNT(*), COALESCE(SUM(bytes), 0)
            FROM transfers
            WHERE timestamp_utc >= @since
            GROUP BY kind, outcome
            """;
        cmd.Parameters.AddWithValue("@since", sinceUtc.ToString("o"));

        var stats = new TransferStats();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var kind = (TransferKind)reader.GetInt32(0);
            var outcome = (TransferOutcome)reader.GetInt32(1);
            int count = reader.GetInt32(2);
            long bytes = reader.GetInt64(3);

            if (outcome == TransferOutcome.Failed)
            {
                stats.Failures += count;
                continue;
            }
            if (outcome != TransferOutcome.Success) continue;

            switch (kind)
            {
                case TransferKind.Upload:
                    stats.Uploads += count;
                    stats.BytesUploaded += bytes;
                    break;
                case TransferKind.Download:
                    stats.Downloads += count;
                    stats.BytesDownloaded += bytes;
                    break;
            }
        }

        return stats;
    }

    public async Task PruneTransfersAsync(int keep = 2000, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM transfers
            WHERE transfer_id NOT IN (
                SELECT transfer_id FROM transfers ORDER BY timestamp_utc DESC LIMIT @keep
            )
            """;
        cmd.Parameters.AddWithValue("@keep", keep);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static TransferRecord ReadTransfer(SqliteDataReader reader) => new()
    {
        TransferId = reader.GetString(reader.GetOrdinal("transfer_id")),
        PoolId = reader.GetString(reader.GetOrdinal("pool_id")),
        AccountId = reader.IsDBNull(reader.GetOrdinal("account_id")) ? null : reader.GetString(reader.GetOrdinal("account_id")),
        FileName = reader.GetString(reader.GetOrdinal("file_name")),
        RelativePath = reader.IsDBNull(reader.GetOrdinal("relative_path")) ? null : reader.GetString(reader.GetOrdinal("relative_path")),
        Kind = (TransferKind)reader.GetInt32(reader.GetOrdinal("kind")),
        Outcome = (TransferOutcome)reader.GetInt32(reader.GetOrdinal("outcome")),
        Bytes = reader.GetInt64(reader.GetOrdinal("bytes")),
        DurationMs = reader.GetInt64(reader.GetOrdinal("duration_ms")),
        TimestampUtc = ParseUtc(reader.GetString(reader.GetOrdinal("timestamp_utc"))),
        Error = reader.IsDBNull(reader.GetOrdinal("error")) ? null : reader.GetString(reader.GetOrdinal("error")),
        ItemId = ReadStringOrNull(reader, "item_id"),
        ChunkCount = (int)ReadLongOrZero(reader, "chunk_count"),
        AccountIds = ReadStringOrNull(reader, "account_ids")
    };

    /// <summary>Reads a text column that may not exist yet in an older database file.</summary>
    private static string? ReadStringOrNull(SqliteDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            return reader.IsDBNull(ord) ? null : reader.GetString(ord);
        }
        catch { return null; }
    }

    // ── Notifications ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AppNotification>> GetNotificationsAsync(
        int limit = 50, bool unreadOnly = false, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = unreadOnly
            ? "SELECT * FROM notifications WHERE is_read = 0 ORDER BY timestamp_utc DESC LIMIT @limit"
            : "SELECT * FROM notifications ORDER BY timestamp_utc DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<AppNotification>();
        while (await reader.ReadAsync(ct))
            results.Add(ReadNotification(reader));
        return results;
    }

    public async Task<AppNotification> UpsertNotificationAsync(AppNotification n, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notifications (notification_id, title, body, source, severity, timestamp_utc, is_read, action_url, related_account_id)
            VALUES (@id, @title, @body, @source, @severity, @ts, @read, @url, @account)
            ON CONFLICT(notification_id) DO UPDATE SET
                title = excluded.title, body = excluded.body, severity = excluded.severity,
                is_read = excluded.is_read, action_url = excluded.action_url
            """;
        cmd.Parameters.AddWithValue("@id", n.NotificationId);
        cmd.Parameters.AddWithValue("@title", n.Title);
        cmd.Parameters.AddWithValue("@body", n.Body);
        cmd.Parameters.AddWithValue("@source", n.Source);
        cmd.Parameters.AddWithValue("@severity", (int)n.Severity);
        cmd.Parameters.AddWithValue("@ts", n.TimestampUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@read", n.IsRead ? 1 : 0);
        cmd.Parameters.AddWithValue("@url", (object?)n.ActionUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@account", (object?)n.RelatedAccountId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return n;
    }

    public async Task MarkNotificationReadAsync(string notificationId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE notifications SET is_read = 1 WHERE notification_id = @id";
        cmd.Parameters.AddWithValue("@id", notificationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkAllNotificationsReadAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await ExecuteNonQueryAsync(conn, "UPDATE notifications SET is_read = 1 WHERE is_read = 0", ct);
    }

    public async Task<int> GetUnreadCountAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM notifications WHERE is_read = 0";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await ExecuteNonQueryAsync(conn, "PRAGMA foreign_keys = ON", ct);
        return conn;
    }

    /// <summary>
    /// Timestamps are stored as round-trip ("o") UTC strings. Plain DateTime.Parse
    /// would convert them to LOCAL time on read, shifting every timestamp by the
    /// UTC offset — which made "was this file modified since last sync?" checks
    /// wrongly skip any edit made within that offset. RoundtripKind preserves UTC.
    /// </summary>
    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static async Task ExecuteNonQueryAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<List<PoolMember>> LoadPoolMembersAsync(SqliteConnection conn, string poolId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM pool_members WHERE pool_id = @id ORDER BY priority";
        cmd.Parameters.AddWithValue("@id", poolId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var members = new List<PoolMember>();
        while (await reader.ReadAsync(ct))
        {
            members.Add(new PoolMember
            {
                AccountId = reader.GetString(reader.GetOrdinal("account_id")),
                ProviderId = reader.GetString(reader.GetOrdinal("provider_id")),
                Priority = reader.GetInt32(reader.GetOrdinal("priority")),
                IsEnabled = reader.GetInt32(reader.GetOrdinal("is_enabled")) == 1,
                RootFolderId = reader.IsDBNull(reader.GetOrdinal("root_folder_id"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("root_folder_id")),
                MaxUsageBytes = ReadLongOrZero(reader, "max_usage_bytes"),
                ReserveBytes = ReadLongOrZero(reader, "reserve_bytes"),
                IsVersionStore = ReadLongOrZero(reader, "is_version_store") == 1,
                ExcludeFromFilePlacement = ReadLongOrZero(reader, "exclude_from_files") == 1
            });
        }
        return members;
    }

    private static CloudItem ReadItem(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        RemoteId = reader.GetString(reader.GetOrdinal("remote_id")),
        ProviderId = reader.GetString(reader.GetOrdinal("provider_id")),
        AccountId = reader.GetString(reader.GetOrdinal("account_id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        ParentId = reader.IsDBNull(reader.GetOrdinal("parent_id"))
            ? null
            : reader.GetString(reader.GetOrdinal("parent_id")),
        Type = (CloudItemType)reader.GetInt32(reader.GetOrdinal("type")),
        Size = reader.GetInt64(reader.GetOrdinal("size")),
        ContentHash = reader.IsDBNull(reader.GetOrdinal("content_hash"))
            ? null
            : reader.GetString(reader.GetOrdinal("content_hash")),
        CreatedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("created_at_utc"))),
        ModifiedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("modified_at_utc"))),
        SyncState = ReadSyncState(reader)
    };

    /// <summary>Reads a column that may not exist yet in an older database file.</summary>
    private static long ReadLongOrZero(SqliteDataReader reader, string column)
    {
        try
        {
            var ord = reader.GetOrdinal(column);
            return reader.IsDBNull(ord) ? 0 : reader.GetInt64(ord);
        }
        catch { return 0; }
    }

    private static SyncState ReadSyncState(SqliteDataReader reader)
    {
        try
        {
            var ord = reader.GetOrdinal("sync_state");
            return reader.IsDBNull(ord) ? SyncState.Synced : (SyncState)reader.GetInt32(ord);
        }
        catch
        {
            return SyncState.Synced; // column missing in an older database
        }
    }

    private static ProviderAccount ReadAccount(SqliteDataReader reader)
    {
        long totalBytes = 0, usedBytes = 0;
        try
        {
            var totalOrd = reader.GetOrdinal("quota_total_bytes");
            var usedOrd = reader.GetOrdinal("quota_used_bytes");
            totalBytes = reader.IsDBNull(totalOrd) ? 0 : reader.GetInt64(totalOrd);
            usedBytes = reader.IsDBNull(usedOrd) ? 0 : reader.GetInt64(usedOrd);
        }
        catch { /* columns may not exist in older databases */ }

        return new ProviderAccount
        {
            AccountId = reader.GetString(reader.GetOrdinal("account_id")),
            ProviderId = reader.GetString(reader.GetOrdinal("provider_id")),
            DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
            Email = reader.IsDBNull(reader.GetOrdinal("email"))
                ? null
                : reader.GetString(reader.GetOrdinal("email")),
            IsEnabled = reader.GetInt32(reader.GetOrdinal("is_enabled")) == 1,
            ConnectedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("connected_at_utc"))),
            Quota = totalBytes > 0
                ? new StorageQuota { TotalBytes = totalBytes, UsedBytes = usedBytes }
                : null
        };
    }

    private static StoragePool ReadPool(SqliteDataReader reader) => new()
    {
        PoolId = reader.GetString(reader.GetOrdinal("pool_id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        LocalPath = reader.GetString(reader.GetOrdinal("local_path")),
        Mode = (PoolMode)reader.GetInt32(reader.GetOrdinal("mode")),
        DefaultStrategy = (PlacementStrategy)reader.GetInt32(reader.GetOrdinal("default_strategy")),
        VersionPolicy = ReadVersionPolicy(reader)
    };

    private static VersionPolicy ReadVersionPolicy(SqliteDataReader reader)
    {
        var json = ReadStringOrNull(reader, "version_policy");
        if (string.IsNullOrEmpty(json)) return new VersionPolicy();

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<VersionPolicy>(json) ?? new VersionPolicy();
        }
        catch
        {
            // A policy we can't read must not make the pool unusable.
            return new VersionPolicy();
        }
    }

    private static AppNotification ReadNotification(SqliteDataReader reader) => new()
    {
        NotificationId = reader.GetString(reader.GetOrdinal("notification_id")),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Body = reader.GetString(reader.GetOrdinal("body")),
        Source = reader.GetString(reader.GetOrdinal("source")),
        Severity = (NotificationSeverity)reader.GetInt32(reader.GetOrdinal("severity")),
        TimestampUtc = ParseUtc(reader.GetString(reader.GetOrdinal("timestamp_utc"))),
        IsRead = reader.GetInt32(reader.GetOrdinal("is_read")) == 1,
        ActionUrl = reader.IsDBNull(reader.GetOrdinal("action_url")) ? null : reader.GetString(reader.GetOrdinal("action_url")),
        RelatedAccountId = reader.IsDBNull(reader.GetOrdinal("related_account_id")) ? null : reader.GetString(reader.GetOrdinal("related_account_id"))
    };

    private static FileRule ReadFileRule(SqliteDataReader reader) => new()
    {
        RuleId = reader.GetString(reader.GetOrdinal("rule_id")),
        PoolId = reader.IsDBNull(reader.GetOrdinal("pool_id")) ? null : reader.GetString(reader.GetOrdinal("pool_id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        Type = (FileRuleType)reader.GetInt32(reader.GetOrdinal("type")),
        Pattern = reader.GetString(reader.GetOrdinal("pattern")),
        Action = (FileRuleAction)reader.GetInt32(reader.GetOrdinal("action")),
        TargetAccountId = reader.IsDBNull(reader.GetOrdinal("target_account_id")) ? null : reader.GetString(reader.GetOrdinal("target_account_id")),
        TargetProviderId = reader.IsDBNull(reader.GetOrdinal("target_provider_id")) ? null : reader.GetString(reader.GetOrdinal("target_provider_id")),
        OverrideStrategy = reader.IsDBNull(reader.GetOrdinal("override_strategy")) ? null : (PlacementStrategy)reader.GetInt32(reader.GetOrdinal("override_strategy")),
        Priority = reader.GetInt32(reader.GetOrdinal("priority")),
        IsEnabled = reader.GetInt32(reader.GetOrdinal("is_enabled")) == 1
    };

    private static EmailAccountConfig ReadEmailConfig(SqliteDataReader reader) => new()
    {
        ConfigId = reader.GetString(reader.GetOrdinal("config_id")),
        AccountId = reader.GetString(reader.GetOrdinal("account_id")),
        Method = (EmailAccessMethod)reader.GetInt32(reader.GetOrdinal("method")),
        ImapHost = reader.IsDBNull(reader.GetOrdinal("imap_host")) ? null : reader.GetString(reader.GetOrdinal("imap_host")),
        ImapPort = reader.GetInt32(reader.GetOrdinal("imap_port")),
        UseSsl = reader.GetInt32(reader.GetOrdinal("use_ssl")) == 1,
        ImapUsername = reader.IsDBNull(reader.GetOrdinal("imap_username")) ? null : reader.GetString(reader.GetOrdinal("imap_username")),
        ImapPasswordProtected = reader.IsDBNull(reader.GetOrdinal("imap_password_protected")) ? null : (byte[])reader["imap_password_protected"],
        IsEnabled = reader.GetInt32(reader.GetOrdinal("is_enabled")) == 1,
        LastCheckedUtc = reader.IsDBNull(reader.GetOrdinal("last_checked_utc")) ? null : ParseUtc(reader.GetString(reader.GetOrdinal("last_checked_utc"))),
        CheckIntervalMinutes = reader.GetInt32(reader.GetOrdinal("check_interval_minutes"))
    };

    private static FileVersion ReadFileVersion(SqliteDataReader reader) => new()
    {
        VersionId = reader.GetString(reader.GetOrdinal("version_id")),
        RemoteVersionId = reader.GetString(reader.GetOrdinal("remote_version_id")),
        FileId = reader.GetString(reader.GetOrdinal("file_id")),
        Size = reader.GetInt64(reader.GetOrdinal("size")),
        ModifiedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("modified_at_utc"))),
        ModifiedBy = reader.IsDBNull(reader.GetOrdinal("modified_by"))
            ? null
            : reader.GetString(reader.GetOrdinal("modified_by")),
        AccountId = ReadStringOrNull(reader, "account_id"),
        ProviderId = ReadStringOrNull(reader, "provider_id"),
        VersionNumber = (int)ReadLongOrZero(reader, "version_number"),
        CreatedAtUtc = ReadDateOrDefault(reader, "created_at_utc"),
        ChunkManifest = ReadStringOrNull(reader, "chunk_manifest")
    };

    private static DateTime ReadDateOrDefault(SqliteDataReader reader, string column)
    {
        var raw = ReadStringOrNull(reader, column);
        return raw == null ? DateTime.MinValue : ParseUtc(raw);
    }
}
