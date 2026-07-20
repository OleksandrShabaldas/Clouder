using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Vanara.PInvoke;
using Clouder.Core.Logging;
using Clouder.Storage;
using static Vanara.PInvoke.CldApi;

namespace Clouder.CloudFilter;

/// <summary>
/// Hosts the CfApi connection for one pool's sync root and services Windows'
/// hydration requests: when the user opens a placeholder file, Windows calls
/// FETCH_DATA and we stream the bytes back from the cloud.
///
/// Two rules govern everything here, and breaking either is what made the previous
/// implementation crash and hang Explorer:
///   1. Callbacks must return promptly. Work happens on the thread pool and completes
///      later via CfExecute — never block the filter's callback thread.
///   2. Every transfer except the final one must be 4096-byte aligned
///      (see <see cref="AlignedTransfer"/>).
/// Every callback must also complete exactly once, success or failure, or the calling
/// application hangs on the file handle forever.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private const uint StatusSuccess = 0x00000000;
    private const uint StatusUnsuccessful = 0xC0000001;
    private const uint StatusCloudFileUnsuccessful = 0xC000CF07;

    private readonly string _syncRootPath;
    private readonly string _poolId;
    private readonly HydrationService _hydration;

    private CF_CONNECTION_KEY _connectionKey;
    private bool _connected;
    private bool _disposed;

    /// <summary>
    /// In-flight hydrations, keyed by normalized file path so CANCEL_FETCH_DATA can
    /// stop a large download the user abandoned. Keying by path (rather than transfer
    /// key) means two concurrent reads of the same file cancel together; Windows simply
    /// re-requests, so the cost is a retry rather than a failure.
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    // The callback delegates must be kept alive for as long as the connection exists,
    // or the GC will collect them and Windows will call into freed memory.
    private readonly CF_CALLBACK _fetchDataCallback;
    private readonly CF_CALLBACK _fetchPlaceholdersCallback;
    private readonly CF_CALLBACK _cancelFetchDataCallback;

    public string PoolId => _poolId;

    public SyncEngine(string syncRootPath, string poolId, HydrationService hydration)
    {
        _syncRootPath = syncRootPath;
        _poolId = poolId;
        _hydration = hydration;

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
        ClouderLog.Info($"Explorer integration connected for pool {_poolId}: {_syncRootPath}");
    }

    public void Disconnect()
    {
        if (!_connected) return;
        _connected = false;

        foreach (var cts in _inFlight.Values)
        {
            try { cts.Cancel(); } catch { }
        }
        _inFlight.Clear();

        try
        {
            CfDisconnectSyncRoot(_connectionKey);
            ClouderLog.Info($"Explorer integration disconnected for pool {_poolId}");
        }
        catch (Exception ex)
        {
            ClouderLog.Error($"Failed to disconnect sync root for pool {_poolId}", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    // ── FETCH_DATA: the user opened a placeholder; stream its bytes ─────

    private void OnFetchData(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParams)
    {
        // Copy out of the `in` parameters: the structs are only valid for the
        // duration of the synchronous callback, and the work continues past it.
        var info = callbackInfo;
        var connectionKey = callbackInfo.ConnectionKey;
        var transferKey = callbackInfo.TransferKey;
        long requiredOffset = callbackParams.FetchData.RequiredFileOffset;
        long requiredLength = callbackParams.FetchData.RequiredLength;
        var itemId = ExtractItemId(info);
        var pathKey = info.NormalizedPath ?? itemId;

        var cts = new CancellationTokenSource();
        _inFlight[pathKey] = cts;

        // Return immediately — the transfer completes asynchronously via CfExecute.
        _ = Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrEmpty(itemId))
                {
                    ClouderLog.Warn("Hydration request carried no file identity; failing the request");
                    ReportFailure(connectionKey, transferKey, requiredOffset, requiredLength);
                    return;
                }

                await using var source = await _hydration.OpenRangeAsync(
                    itemId, requiredOffset, requiredLength, cts.Token);

                long sent = await AlignedTransfer.RunAsync(
                    source, requiredOffset, requiredLength,
                    (offset, buffer, count, _) =>
                    {
                        TransferBlock(connectionKey, transferKey, offset, buffer, count);
                        return Task.CompletedTask;
                    },
                    AlignedTransfer.DefaultBlockSize,
                    cts.Token);

                if (sent < requiredLength)
                {
                    // The cloud returned less than Windows asked for; the file handle
                    // must still be completed or the opening application hangs.
                    ClouderLog.Warn(
                        $"Hydration of '{itemId}' returned {sent} of {requiredLength} requested bytes");
                    ReportFailure(connectionKey, transferKey, requiredOffset + sent, requiredLength - sent);
                }
                else
                {
                    ClouderLog.Debug($"Hydrated {sent} bytes of '{itemId}' at offset {requiredOffset}");
                }
            }
            catch (OperationCanceledException)
            {
                ClouderLog.Debug($"Hydration of '{itemId}' cancelled");
                TryReportFailure(connectionKey, transferKey, requiredOffset, requiredLength);
            }
            catch (Exception ex)
            {
                ClouderLog.Error($"Hydration failed for '{itemId}'", ex);
                TryReportFailure(connectionKey, transferKey, requiredOffset, requiredLength);
            }
            finally
            {
                _inFlight.TryRemove(pathKey, out _);
                cts.Dispose();
            }
        });
    }

    private static void TransferBlock(
        CF_CONNECTION_KEY connectionKey, CF_TRANSFER_KEY transferKey,
        long offset, byte[] buffer, int count)
    {
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                ConnectionKey = connectionKey,
                TransferKey = transferKey
            };

            var opParams = CF_OPERATION_PARAMETERS.Create(
                new CF_OPERATION_PARAMETERS.TRANSFERDATA
                {
                    Buffer = pinned.AddrOfPinnedObject(),
                    Offset = offset,
                    Length = count,
                    Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
                    CompletionStatus = new NTStatus(StatusSuccess)
                });

            CfExecute(opInfo, ref opParams).ThrowIfFailed("CfExecute TransferData failed");
        }
        finally
        {
            pinned.Free();
        }
    }

    // ── FETCH_PLACEHOLDERS ──────────────────────────────────────────────

    /// <summary>
    /// Windows asks us to enumerate a directory. Clouder populates placeholders eagerly
    /// from its own metadata (see PlaceholderHelper), so there is nothing to add here —
    /// but the callback must still be completed, or Explorer hangs on that folder.
    /// </summary>
    private void OnFetchPlaceholders(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParams)
    {
        var connectionKey = callbackInfo.ConnectionKey;
        var transferKey = callbackInfo.TransferKey;

        try
        {
            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
                ConnectionKey = connectionKey,
                TransferKey = transferKey
            };

            var opParams = CF_OPERATION_PARAMETERS.Create(
                new CF_OPERATION_PARAMETERS.TRANSFERPLACEHOLDERS
                {
                    Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION,
                    CompletionStatus = new NTStatus(StatusSuccess),
                    PlaceholderArray = IntPtr.Zero,
                    PlaceholderCount = 0,
                    PlaceholderTotalCount = 0,
                    EntriesProcessed = 0
                });

            CfExecute(opInfo, ref opParams);
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Failed to complete a placeholder enumeration request", ex);
        }
    }

    // ── CANCEL_FETCH_DATA ───────────────────────────────────────────────

    private void OnCancelFetchData(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParams)
    {
        var pathKey = callbackInfo.NormalizedPath ?? ExtractItemId(callbackInfo);
        if (string.IsNullOrEmpty(pathKey)) return;

        if (_inFlight.TryGetValue(pathKey, out var cts))
        {
            try { cts.Cancel(); } catch { }
            ClouderLog.Debug($"Windows cancelled hydration of '{pathKey}'");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Placeholders carry Clouder's internal item id ("{poolId}|{relativePath}") as their
    /// file identity. The previous implementation stored the provider's remote id, which
    /// no uploaded file could ever be looked up by — so those files never hydrated.
    /// </summary>
    private static string ExtractItemId(in CF_CALLBACK_INFO info)
    {
        if (info.FileIdentity == IntPtr.Zero || info.FileIdentityLength == 0)
            return "";

        try
        {
            var bytes = new byte[info.FileIdentityLength];
            Marshal.Copy(info.FileIdentity, bytes, 0, (int)info.FileIdentityLength);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            ClouderLog.Error("Could not read the file identity from a hydration request", ex);
            return "";
        }
    }

    private static void TryReportFailure(
        CF_CONNECTION_KEY connectionKey, CF_TRANSFER_KEY transferKey, long offset, long length)
    {
        try { ReportFailure(connectionKey, transferKey, offset, length); }
        catch (Exception ex) { ClouderLog.Error("Could not report hydration failure to Windows", ex); }
    }

    private static void ReportFailure(
        CF_CONNECTION_KEY connectionKey, CF_TRANSFER_KEY transferKey, long offset, long length)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = connectionKey,
            TransferKey = transferKey
        };

        var opParams = CF_OPERATION_PARAMETERS.Create(
            new CF_OPERATION_PARAMETERS.TRANSFERDATA
            {
                Buffer = IntPtr.Zero,
                Offset = offset,
                Length = length,
                Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
                CompletionStatus = new NTStatus(StatusCloudFileUnsuccessful)
            });

        CfExecute(opInfo, ref opParams);
    }
}
