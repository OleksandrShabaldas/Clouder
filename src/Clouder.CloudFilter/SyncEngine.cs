using System.Runtime.InteropServices;
using Vanara.PInvoke;
using Clouder.Core.Logging;
using Clouder.Core.Models;
using Clouder.Core.Providers;
using Clouder.Core.Storage;
using static Vanara.PInvoke.CldApi;

namespace Clouder.CloudFilter;

/// <summary>
/// Manages the CfApi connection for a sync root. Handles callbacks from Windows
/// when files are opened (hydration) or folders are browsed (placeholder population).
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private readonly string _syncRootPath;
    private readonly IMetadataStore _store;
    private readonly IProviderRegistry _providers;
    private readonly string _poolId;
    private CF_CONNECTION_KEY _connectionKey;
    private bool _connected;

    public string PoolId => _poolId;

    // Must prevent garbage collection of callback delegates
    private readonly CF_CALLBACK _fetchDataCallback;
    private readonly CF_CALLBACK _fetchPlaceholdersCallback;
    private readonly CF_CALLBACK _cancelFetchDataCallback;

    public SyncEngine(string syncRootPath, string poolId, IMetadataStore store, IProviderRegistry providers)
    {
        _syncRootPath = syncRootPath;
        _poolId = poolId;
        _store = store;
        _providers = providers;

        _fetchDataCallback = OnFetchData;
        _fetchPlaceholdersCallback = OnFetchPlaceholders;
        _cancelFetchDataCallback = OnCancelFetchData;
    }

    public void Connect()
    {
        var callbackTable = new CF_CALLBACK_REGISTRATION[]
        {
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = _fetchDataCallback },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS, Callback = _fetchPlaceholdersCallback },
            new() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA, Callback = _cancelFetchDataCallback },
            CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END
        };

        CfConnectSyncRoot(
            _syncRootPath,
            callbackTable,
            IntPtr.Zero,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO
            | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
            out _connectionKey).ThrowIfFailed("CfConnectSyncRoot failed");

        _connected = true;
        ClouderLog.Info($"SyncEngine connected: {_syncRootPath}");
    }

    public void Disconnect()
    {
        if (!_connected) return;
        CfDisconnectSyncRoot(_connectionKey);
        _connected = false;
    }

    public void Dispose() => Disconnect();

    // ── Callback: Fetch Data (hydrate a file on demand) ─────────────────

    private void OnFetchData(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParams)
    {
        var info = callbackInfo;
        var fetchParams = callbackParams.FetchData;
        var requiredOffset = fetchParams.RequiredFileOffset;
        var requiredLength = fetchParams.RequiredLength;
        var remoteId = ExtractRemoteId(info);

        try
        {
            Task.Run(async () =>
            {
                try
                {
                    var item = await FindItemByRemoteIdAsync(remoteId);
                    if (item == null) { ReportFailure(info); return; }

                    var provider = _providers.GetProvider(item.ProviderId);
                    if (provider == null) { ReportFailure(info); return; }

                    await using var stream = await provider.DownloadAsync(item.AccountId, item.RemoteId);
                    await TransferDataAsync(info, stream, requiredOffset, requiredLength, item.Size);
                }
                catch (Exception ex)
                {
                    ClouderLog.Error($"FetchData failed for {remoteId}", ex);
                    try { ReportFailure(info); } catch { }
                }
            }).Wait();
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"FetchData outer failure for {remoteId}", ex);
        }
    }

    // ── Callback: Fetch Placeholders (list folder contents) ─────────────

    private void OnFetchPlaceholders(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParams)
    {
        var info = callbackInfo;
        var folderPath = info.NormalizedPath;
        var remoteId = ExtractRemoteId(info);

        try
        {
            Task.Run(async () =>
            {
                try
                {
                    var pool = await _store.GetPoolAsync(_poolId);
                    if (pool == null) { ReportSuccess(info); return; }

                    var items = new List<CloudItem>();

                    foreach (var member in pool.Members.Where(m => m.IsEnabled))
                    {
                        try
                        {
                            var provider = _providers.GetProvider(member.ProviderId);
                            if (provider == null) continue;

                            var folderId = string.IsNullOrEmpty(remoteId) ? "root" : remoteId;
                            var memberItems = await provider.ListFolderAsync(member.AccountId, folderId);
                            items.AddRange(memberItems);
                        }
                        catch (Exception ex)
                        {
                            ClouderLog.Error($"ListFolder failed for member {member.AccountId}", ex);
                        }
                    }

                    if (items.Count > 0)
                    {
                        foreach (var item in items)
                            await _store.UpsertItemAsync(item);

                        var localDir = Path.Combine(_syncRootPath, folderPath?.TrimStart('\\') ?? "");
                        PlaceholderHelper.CreatePlaceholders(localDir, items);
                    }

                    ReportSuccess(info);
                }
                catch (Exception ex)
                {
                    ClouderLog.Error($"FetchPlaceholders failed for folder '{folderPath}'", ex);
                    try { ReportSuccess(info); } catch { }
                }
            }).Wait();
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"FetchPlaceholders outer failure", ex);
        }
    }

    // ── Callback: Cancel ────────────────────────────────────────────────

    private void OnCancelFetchData(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParams)
    {
        // Nothing to do — the background download will finish naturally
    }

    // ── Data transfer ───────────────────────────────────────────────────

    private async Task TransferDataAsync(
        CF_CALLBACK_INFO callbackInfo, Stream sourceStream,
        long requiredOffset, long requiredLength, long totalSize)
    {
        const int bufferSize = 4 * 1024 * 1024; // 4 MB chunks
        var buffer = new byte[bufferSize];
        long currentOffset = requiredOffset;
        long remaining = Math.Min(requiredLength, totalSize - requiredOffset);

        if (sourceStream.CanSeek)
            sourceStream.Seek(requiredOffset, SeekOrigin.Begin);
        else
        {
            // Skip bytes if stream isn't seekable
            long toSkip = requiredOffset;
            while (toSkip > 0)
            {
                int skipped = await sourceStream.ReadAsync(buffer, 0, (int)Math.Min(toSkip, bufferSize));
                if (skipped == 0) break;
                toSkip -= skipped;
            }
        }

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(remaining, bufferSize);
            int bytesRead = await sourceStream.ReadAsync(buffer, 0, toRead);
            if (bytesRead == 0) break;

            var pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var opInfo = new CF_OPERATION_INFO
                {
                    StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                    Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                    ConnectionKey = callbackInfo.ConnectionKey,
                    TransferKey = callbackInfo.TransferKey
                };

                var opParams = CF_OPERATION_PARAMETERS.Create(
                    new CF_OPERATION_PARAMETERS.TRANSFERDATA
                    {
                        Buffer = pinnedBuffer.AddrOfPinnedObject(),
                        Offset = currentOffset,
                        Length = bytesRead,
                        Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
                        CompletionStatus = new NTStatus(0) // STATUS_SUCCESS
                    });

                CfExecute(opInfo, ref opParams).ThrowIfFailed("CfExecute TransferData failed");
            }
            finally
            {
                pinnedBuffer.Free();
            }

            currentOffset += bytesRead;
            remaining -= bytesRead;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string ExtractRemoteId(in CF_CALLBACK_INFO info)
    {
        if (info.FileIdentity == IntPtr.Zero || info.FileIdentityLength == 0)
            return "";

        var bytes = new byte[info.FileIdentityLength];
        Marshal.Copy(info.FileIdentity, bytes, 0, (int)info.FileIdentityLength);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private async Task<CloudItem?> FindItemByRemoteIdAsync(string remoteId)
    {
        if (string.IsNullOrEmpty(remoteId)) return null;
        return await _store.GetItemAsync(remoteId);
    }

    private static void ReportSuccess(in CF_CALLBACK_INFO info)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DATA,
            ConnectionKey = info.ConnectionKey,
            TransferKey = info.TransferKey
        };

        var opParams = CF_OPERATION_PARAMETERS.Create(
            new CF_OPERATION_PARAMETERS.ACKDATA
            {
                CompletionStatus = new NTStatus(0), // STATUS_SUCCESS
                Flags = CF_OPERATION_ACK_DATA_FLAGS.CF_OPERATION_ACK_DATA_FLAG_NONE
            });

        CfExecute(opInfo, ref opParams);
    }

    private static void ReportFailure(in CF_CALLBACK_INFO info)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = info.ConnectionKey,
            TransferKey = info.TransferKey
        };

        var opParams = CF_OPERATION_PARAMETERS.Create(
            new CF_OPERATION_PARAMETERS.TRANSFERDATA
            {
                CompletionStatus = new NTStatus(0xC0000001), // STATUS_UNSUCCESSFUL
                Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
                Length = 0,
                Offset = 0,
                Buffer = IntPtr.Zero
            });

        CfExecute(opInfo, ref opParams);
    }
}
