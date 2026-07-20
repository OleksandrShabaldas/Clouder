using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using Clouder.Core.Logging;

namespace Clouder.CloudFilter;

/// <summary>
/// Registers a pool's local folder with Windows as a cloud storage sync root, which is
/// what puts it in Explorer's navigation pane next to OneDrive and MEGA and enables the
/// sync-status icon overlays.
/// </summary>
public static class SyncRootRegistrar
{
    private const string ProviderName = "Clouder";

    // Bumping this makes Windows refresh a stale registration from an older build.
    private const string RegistrationVersion = "2.0";

    private static readonly Guid ClouderProviderId = new("C7A3F1E0-5B9D-4D7A-8E2F-3C1A9B0D6E4F");

    public static async Task RegisterAsync(string poolId, string poolName, string localPath)
    {
        Directory.CreateDirectory(localPath);

        var folder = await StorageFolder.GetFolderFromPathAsync(localPath);

        var syncRootInfo = new StorageProviderSyncRootInfo
        {
            Id = BuildSyncRootId(poolId),
            Path = folder,
            DisplayNameResource = poolName,
            IconResource = @"%SystemRoot%\system32\imageres.dll,-1043",
            Version = RegistrationVersion,
            RecycleBinUri = null,
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.StreamingAllowed,
            // Clouder creates placeholders itself from its metadata store rather than
            // relying on on-demand directory population, so the folder is always fully
            // enumerated. See SyncEngine.OnFetchPlaceholders.
            PopulationPolicy = StorageProviderPopulationPolicy.Full,
            InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime
                | StorageProviderInSyncPolicy.DirectoryCreationTime,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            ShowSiblingsAsGroup = false,
            ProviderId = ClouderProviderId,
            Context = CryptographicBuffer.ConvertStringToBinary(poolId, BinaryStringEncoding.Utf8)
        };

        StorageProviderSyncRootManager.Register(syncRootInfo);
        ClouderLog.Info($"Registered sync root '{poolName}' at {localPath}");
    }

    public static void Unregister(string poolId, string? localPath = null)
    {
        try
        {
            StorageProviderSyncRootManager.Unregister(BuildSyncRootId(poolId));
            ClouderLog.Info($"Unregistered sync root for pool {poolId}");
        }
        catch (Exception ex)
        {
            // Already unregistered, or never registered — not worth surfacing.
            ClouderLog.Debug($"Sync root for pool {poolId} was not unregistered: {ex.Message}");
        }

        // Earlier builds set ReadOnly on the pool folder to mark it as provider-owned
        // and never cleared it, which left the folder looking read-only after the
        // integration was turned off. Clear it defensively.
        if (!string.IsNullOrEmpty(localPath))
            ClearReadOnly(localPath);
    }

    public static bool IsRegistered(string poolId)
    {
        try
        {
            var syncRoot = StorageProviderSyncRootManager.GetSyncRootInformationForId(BuildSyncRootId(poolId));
            return syncRoot != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when this Windows build supports the cloud filter API at all.</summary>
    public static bool IsSupported()
    {
        try
        {
            return StorageProviderSyncRootManager.IsSupported();
        }
        catch
        {
            return false;
        }
    }

    private static void ClearReadOnly(string localPath)
    {
        try
        {
            var dir = new DirectoryInfo(localPath);
            if (dir.Exists && dir.Attributes.HasFlag(System.IO.FileAttributes.ReadOnly))
                dir.Attributes &= ~System.IO.FileAttributes.ReadOnly;
        }
        catch (Exception ex)
        {
            ClouderLog.Debug($"Could not clear the read-only attribute on '{localPath}': {ex.Message}");
        }
    }

    private static string BuildSyncRootId(string poolId) => $"{ProviderName}!{poolId}";
}
